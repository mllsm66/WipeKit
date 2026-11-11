using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace WipeKit
{
    /// <summary>Ein Löschvorgang. Die eigentliche Arbeit läuft in einem eigenen Thread.</summary>
    public sealed class WipeJob
    {
        const int Chunk = 4 * 1024 * 1024;   // 4 MiB pro Schreibvorgang

        public Guid Id { get; } = Guid.NewGuid();
        public DiskInfo Disk { get; }
        public WipeMode Mode { get; }
        public DateTime CreatedAt { get; } = DateTime.Now;
        public string ProtocolPath { get; set; }
        public bool Reported { get; set; }

        readonly object _lock = new object();
        readonly ulong _seed = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8), 0);
        readonly List<(DateTime Time, long Bytes)> _samples = new List<(DateTime, long)>();
        DateTime _lastReport = DateTime.MinValue;
        volatile bool _cancel;
        bool _prepared;
        Thread _thread;

        JobState _state = JobState.Starting;
        string _phase = "Vorbereitung …";
        int _step;
        long _diskSize, _done, _total;
        double _speed;
        DateTime _started = DateTime.Now;
        DateTime? _finished;
        string _message;
        bool? _verified;
        string _trim;
        bool _sleep;

        public WipeJob(DiskInfo disk, WipeMode mode)
        {
            Disk = disk;
            Mode = mode;
        }

        // --- Zustand (thread-sicher lesbar) -----------------------------------------------

        public JobState State { get { lock (_lock) return _state; } }
        public string Phase { get { lock (_lock) return _phase; } }
        public int Step { get { lock (_lock) return _step; } }
        public long DiskSize { get { lock (_lock) return _diskSize; } }
        public long BytesDone { get { lock (_lock) return _done; } }
        public long BytesTotal { get { lock (_lock) return _total; } }
        public double Speed { get { lock (_lock) return _speed; } }
        public DateTime StartedAt { get { lock (_lock) return _started; } }
        public DateTime? FinishedAt { get { lock (_lock) return _finished; } }
        public string Message { get { lock (_lock) return _message; } }
        public bool? Verified { get { lock (_lock) return _verified; } }
        public string TrimResult { get { lock (_lock) return _trim; } }
        public bool SleepPrevented { get { lock (_lock) return _sleep; } }
        public bool CancelRequested => _cancel;

        public bool IsActive => State == JobState.Starting || State == JobState.Running;

        public double Fraction
        {
            get
            {
                lock (_lock)
                {
                    if (_total <= 0) return _state == JobState.Done ? 1 : 0;
                    return Math.Min(1.0, (double)_done / _total);
                }
            }
        }

        public TimeSpan? Eta
        {
            get
            {
                lock (_lock)
                {
                    if (_state != JobState.Running || _speed <= 1 || _total <= _done) return null;
                    if ((DateTime.Now - _started).TotalSeconds < 15) return null;
                    return TimeSpan.FromSeconds((_total - _done) / _speed);
                }
            }
        }

        public TimeSpan? Elapsed
        {
            get
            {
                lock (_lock)
                {
                    if (_state == JobState.Starting) return null;
                    return (_finished ?? DateTime.Now) - _started;
                }
            }
        }

        public string ShortStatus
        {
            get
            {
                switch (State)
                {
                    case JobState.Starting: return "Wird gestartet …";
                    case JobState.Running:
                        var eta = Eta;
                        return eta.HasValue ? $"{Fmt.Percent(Fraction)} · noch {Fmt.ShortDuration(eta.Value)}" : Fmt.Percent(Fraction);
                    case JobState.Done: return "Erfolgreich gelöscht";
                    case JobState.Failed: return "Fehlgeschlagen";
                    default: return "Abgebrochen";
                }
            }
        }

        // --- Steuerung ----------------------------------------------------------------------

        public void Start()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "WipeKit " + Disk.Id };
            _thread.Start();
        }

        public void RequestCancel() => _cancel = true;

        public void Join(TimeSpan timeout) => _thread?.Join(timeout);

        /// <summary>Nur für die automatische Prüfung der Oberfläche.</summary>
        internal void SetTestState(JobState state, double fraction, string message)
        {
            lock (_lock)
            {
                _state = state;
                _diskSize = Disk.Size;
                _total = Disk.Size * Mode.WorkUnits;
                _done = (long)(_total * fraction);
                _speed = 150_000_000;
                _step = 1;
                _phase = "Durchgang 1: Zufallsdaten schreiben";
                _started = DateTime.Now.AddMinutes(-5);
                _finished = state == JobState.Done || state == JobState.Failed || state == JobState.Cancelled ? DateTime.Now : null;
                _message = message;
                _verified = state == JobState.Done ? true : null;
                _trim = Mode.Trims ? "durchgeführt – alle Blöcke freigegeben" : null;
                _sleep = state == JobState.Running;
            }
        }

        void Run()
        {
            Native.SetThreadExecutionState(Native.ES_CONTINUOUS | Native.ES_SYSTEM_REQUIRED);
            lock (_lock) _sleep = true;
            JobState result;
            string message;
            try
            {
                RunCore();
                result = JobState.Done;
                message = Mode.Verifies
                    ? "Alle Sektoren wurden überschrieben und die Prüfung war erfolgreich."
                    : "Alle Sektoren wurden mit Zufallsdaten überschrieben.";
            }
            catch (OperationCanceledException)
            {
                result = JobState.Cancelled;
                message = "Vom Benutzer abgebrochen. Das Laufwerk ist nur teilweise überschrieben.";
            }
            catch (WipeException e)
            {
                result = JobState.Failed;
                message = e.Message;
            }
            catch (Exception e)
            {
                result = JobState.Failed;
                message = "Unerwarteter Fehler: " + e.Message;
            }

            Native.SetThreadExecutionState(Native.ES_CONTINUOUS);   // Ruhezustand wieder erlauben
            if (_prepared)
            {
                SetPhase("Laufwerk wird wieder freigegeben …");
                try { DiskService.SetOnline(Disk.Number); } catch { }
            }
            lock (_lock)
            {
                _sleep = false;
                _state = result;
                _message = message;
                _finished = DateTime.Now;
                if (result == JobState.Done) _done = _total;
            }
        }

        void RunCore()
        {
            SetPhase("Laufwerk wird geprüft …");
            var current = DiskService.ListExternalDisks().FirstOrDefault(d => d.Number == Disk.Number);
            if (current == null)
                throw new WipeException($"{Disk.Id} ist kein angeschlossenes externes Laufwerk (mehr).");
            if (current.IsProtected)
                throw new WipeException($"{Disk.Id} enthält das laufende Windows und wird niemals gelöscht.");
            if (Math.Abs(current.Size - Disk.Size) > 1024 * 1024)
                throw new WipeException($"Die Größe von {Disk.Id} hat sich geändert ({Fmt.Bytes(current.Size)} statt {Fmt.Bytes(Disk.Size)}). Wurde das Laufwerk getauscht? Abbruch aus Sicherheitsgründen.");
            CheckCancel();

            SetPhase("Laufwerk wird vorbereitet …");
            _prepared = true;
            DiskService.PrepareForWipe(Disk.Number);
            CheckCancel();

            using var handle = Native.CreateFile(Disk.DevicePath, Native.GENERIC_READ | Native.GENERIC_WRITE,
                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING,
                Native.FILE_FLAG_NO_BUFFERING | Native.FILE_FLAG_WRITE_THROUGH, IntPtr.Zero);
            if (handle.IsInvalid)
                throw new WipeException($"{Disk.Id} konnte nicht geöffnet werden: {LastError()}");

            long size = GetLength(handle);
            if (size <= 0) size = Disk.Size;
            if (Math.Abs(size - Disk.Size) > 1024 * 1024)
                throw new WipeException($"Die Größe von {Disk.Id} passt nicht ({Fmt.Bytes(size)} statt {Fmt.Bytes(Disk.Size)}). Abbruch aus Sicherheitsgründen.");

            ExecutePasses(handle, size);
        }

        /// <summary>Alle Durchgänge, Prüfung und TRIM auf einem bereits geöffneten Laufwerk (oder einer Testdatei).</summary>
        internal unsafe void ExecutePasses(SafeFileHandle handle, long size)
        {
            byte* wbuf = (byte*)NativeMemory.AlignedAlloc(Chunk, 4096);
            byte* rbuf = (byte*)NativeMemory.AlignedAlloc(Chunk, 4096);
            try
            {
                lock (_lock)
                {
                    _diskSize = size;
                    _total = size * Mode.WorkUnits;
                    _state = JobState.Running;
                    _started = DateTime.Now;
                }
                long baseOffset = 0;
                for (int i = 0; i < Mode.Passes.Length; i++)
                {
                    var p = Mode.Passes[i];
                    string label = WipeMode.PatternLabel(p);
                    SetStep(i + 1, Mode.Passes.Length > 1 ? $"Durchgang {i + 1}: {label}" : label);
                    WritePass(handle, wbuf, size, p, baseOffset);
                    baseOffset += size;
                }

                if (Mode.Verifies)
                {
                    SetStep(Mode.Passes.Length + 1, "Prüfen (gesamte Platte lesen)");
                    try
                    {
                        VerifyPass(handle, rbuf, wbuf, size, Mode.Passes.Length > 0 ? Mode.Passes[Mode.Passes.Length - 1] : Pattern.Zeros, baseOffset);
                        lock (_lock) _verified = true;
                    }
                    catch (WipeException)
                    {
                        lock (_lock) _verified = false;
                        throw;
                    }
                }

                if (Mode.Trims)
                {
                    SetStep(Mode.StepCount, "TRIM – Blöcke freigeben");
                    string t = TrimAll(handle, size);
                    lock (_lock) _trim = t;
                }
            }
            finally
            {
                NativeMemory.AlignedFree(wbuf);
                NativeMemory.AlignedFree(rbuf);
            }
        }

        // --- Durchgänge -----------------------------------------------------------------------

        unsafe void WritePass(SafeFileHandle handle, byte* buf, long size, Pattern pattern, long baseOffset)
        {
            var span = new Span<byte>(buf, Chunk);
            if (pattern == Pattern.Zeros) span.Clear();
            long offset = 0;
            while (offset < size)
            {
                CheckCancel();
                int n = (int)Math.Min(Chunk, size - offset);
                if (pattern == Pattern.Random) RandomNumberGenerator.Fill(span.Slice(0, n));
                else if (pattern == Pattern.Seeded) FillSeeded(span.Slice(0, n), offset);

                int done = 0;
                while (done < n)
                {
                    long pos = offset + done;
                    var ov = new NativeOverlapped { OffsetLow = (int)(pos & 0xFFFFFFFFL), OffsetHigh = (int)(pos >> 32) };
                    if (!Native.WriteFile(handle, buf + done, n - done, out int written, &ov))
                        throw new WipeException($"Schreibfehler bei {Fmt.Bytes(pos)} von {Fmt.Bytes(size)}: {LastError()} Laufwerk getrennt oder defekt?");
                    if (written == 0)
                        throw new WipeException($"Das Laufwerk nimmt bei {Fmt.Bytes(pos)} keine Daten mehr an.");
                    done += written;
                }
                offset += n;
                Report(baseOffset + offset);
            }
            Native.FlushFileBuffers(handle);   // Schreib-Cache der Platte leeren
        }

        unsafe void VerifyPass(SafeFileHandle handle, byte* buf, byte* expect, long size, Pattern pattern, long baseOffset)
        {
            var got = new Span<byte>(buf, Chunk);
            var exp = new Span<byte>(expect, Chunk);
            long offset = 0;
            while (offset < size)
            {
                CheckCancel();
                int n = (int)Math.Min(Chunk, size - offset);
                int done = 0;
                while (done < n)
                {
                    long pos = offset + done;
                    var ov = new NativeOverlapped { OffsetLow = (int)(pos & 0xFFFFFFFFL), OffsetHigh = (int)(pos >> 32) };
                    if (!Native.ReadFile(handle, buf + done, n - done, out int read, &ov))
                        throw new WipeException($"Lesefehler bei der Prüfung ({Fmt.Bytes(pos)}): {LastError()}");
                    if (read == 0)
                        throw new WipeException($"Die Platte lieferte bei {Fmt.Bytes(pos)} keine Daten mehr.");
                    done += read;
                }

                bool ok;
                if (pattern == Pattern.Seeded)
                {
                    FillSeeded(exp.Slice(0, n), offset);
                    ok = got.Slice(0, n).SequenceEqual(exp.Slice(0, n));
                }
                else
                {
                    ok = got.Slice(0, n).IndexOfAnyExcept((byte)0) < 0;
                }
                if (!ok)
                    throw new WipeException($"Prüfung FEHLGESCHLAGEN: Im Bereich ab {Fmt.Bytes(offset)} stehen noch Daten, die nicht überschrieben wurden. Das Laufwerk (oder der Adapter) arbeitet nicht zuverlässig.");

                offset += n;
                Report(baseOffset + offset);
            }
        }

        unsafe string TrimAll(SafeFileHandle handle, long size)
        {
            byte* input = stackalloc byte[48];
            var span = new Span<byte>(input, 48);
            long offset = 0;
            DateTime last = DateTime.MinValue;
            while (offset < size)
            {
                CheckCancel();
                long length = Math.Min(1L << 30, size - offset);   // 1 GiB pro Aufruf
                span.Clear();
                BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(0), 28);   // Size von DEVICE_MANAGE_DATA_SET_ATTRIBUTES
                BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(4), 1);    // Action = DeviceDsmAction_Trim
                BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(20), 32);  // DataSetRangesOffset
                BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(24), 16);  // DataSetRangesLength
                BinaryPrimitives.WriteInt64LittleEndian(span.Slice(32), offset);
                BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(40), (ulong)length);
                if (!Native.DeviceIoControl(handle, Native.IOCTL_STORAGE_MANAGE_DATA_SET_ATTRIBUTES, input, 48, null, 0, out _, IntPtr.Zero))
                {
                    string err = LastError();
                    return offset == 0
                        ? $"vom Laufwerk bzw. USB-Adapter nicht unterstützt ({err}) – übersprungen"
                        : $"teilweise durchgeführt ({Fmt.Bytes(offset)} freigegeben, dann: {err})";
                }
                offset += length;
                if ((DateTime.Now - last).TotalSeconds >= 1)
                {
                    last = DateTime.Now;
                    SetPhase($"TRIM – Blöcke freigeben ({(int)(offset * 100 / size)} %)");
                }
            }
            return "durchgeführt – alle Blöcke freigegeben";
        }

        // --- Hilfen ----------------------------------------------------------------------------

        /// <summary>Reproduzierbare Zufallsdaten (SplitMix64), abhängig von Startwert und Position.</summary>
        void FillSeeded(Span<byte> span, long offset)
        {
            var words = MemoryMarshal.Cast<byte, ulong>(span);
            ulong state = _seed ^ unchecked((ulong)offset * 0x9E3779B97F4A7C15UL);
            for (int i = 0; i < words.Length; i++)
            {
                unchecked
                {
                    state += 0x9E3779B97F4A7C15UL;
                    ulong z = state;
                    z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                    z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                    words[i] = z ^ (z >> 31);
                }
            }
        }

        static unsafe long GetLength(SafeFileHandle handle)
        {
            long length = 0;
            if (Native.DeviceIoControl(handle, Native.IOCTL_DISK_GET_LENGTH_INFO, null, 0, (byte*)&length, 8, out _, IntPtr.Zero))
                return length;
            return 0;
        }

        static string LastError() => new Win32Exception(Marshal.GetLastWin32Error()).Message;

        void CheckCancel()
        {
            if (_cancel) throw new OperationCanceledException();
        }

        void SetPhase(string phase)
        {
            lock (_lock) _phase = phase;
        }

        void SetStep(int step, string phase)
        {
            lock (_lock)
            {
                _step = step;
                _phase = phase;
            }
        }

        void Report(long total)
        {
            var now = DateTime.Now;
            lock (_lock)
            {
                _done = total;
                if ((now - _lastReport).TotalSeconds < 1) return;
                _lastReport = now;
                _samples.Add((now, total));
                _samples.RemoveAll(s => (now - s.Time).TotalSeconds > 60);   // gleitendes 60-s-Fenster
                var first = _samples[0];
                double span = (now - first.Time).TotalSeconds;
                if (span >= 3) _speed = (total - first.Bytes) / span;
                else
                {
                    double elapsed = (now - _started).TotalSeconds;
                    if (elapsed > 0.5) _speed = total / elapsed;
                }
            }
        }
    }
}
