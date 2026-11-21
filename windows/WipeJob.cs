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
    /// <summary>Ein Vorgang (Löschen, Prüfen, Firmware-Löschung). Die eigentliche Arbeit läuft in einem eigenen Thread.</summary>
    public sealed class WipeJob
    {
        const int Chunk = 4 * 1024 * 1024;   // 4 MiB pro Schreib-/Lesevorgang
        const long MiB = 1024 * 1024;
        const int Sub = 64 * 1024;           // Auflösung bei Lesefehlern
        const int MaxBad = 4096;
        const uint IOCTL_STORAGE_REINITIALIZE_MEDIA = 0x002D9640;

        public Guid Id { get; } = Guid.NewGuid();
        public DiskInfo Disk { get; }
        public WipeMode Mode { get; }
        public string Purpose { get; }
        public bool InternalConfirmed { get; set; }
        public DateTime CreatedAt { get; } = DateTime.Now;
        public Dictionary<string, string> Reports { get; set; } = new Dictionary<string, string>();
        public string ProtocolPath => Reports.TryGetValue("pdf", out var p) ? p : null;
        public bool Reported { get; set; }
        public string HealthBefore { get; private set; }
        public string HealthAfter { get; private set; }

        readonly object _lock = new object();
        readonly ulong _seed = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8), 0);
        readonly List<(DateTime Time, long Bytes)> _samples = new List<(DateTime, long)>();
        readonly Dictionary<long, byte[]> _sampleHashes = new Dictionary<long, byte[]>();
        DateTime _lastReport = DateTime.MinValue;
        volatile bool _cancel;
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
        string _sampleResult, _purgeMethod;
        int? _badBlocks;
        long? _nonZero;
        List<double> _curve;
        bool _indeterminate;

        public WipeJob(DiskInfo disk, WipeMode mode, string purpose = null)
        {
            Disk = disk;
            Mode = mode;
            Purpose = purpose;
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
        public string SampleResult { get { lock (_lock) return _sampleResult; } }
        public string PurgeMethod { get { lock (_lock) return _purgeMethod; } }
        public int? BadBlocks { get { lock (_lock) return _badBlocks; } }
        public long? NonZeroBytes { get { lock (_lock) return _nonZero; } }
        public bool Indeterminate { get { lock (_lock) return _indeterminate; } }
        public List<double> Curve { get { lock (_lock) return _curve?.ToList(); } }
        public bool CancelRequested => _cancel;

        public bool IsActive => State == JobState.Starting || State == JobState.Running;
        public bool IsCheck => Mode.ReadOnly;
        public string Title => IsCheck ? (Purpose == "scan" ? "Oberflächentest" : "Prüfung") : "Löschen";
        public bool FoundProblem => IsCheck && State == JobState.Done && (BadBlocks ?? 0) > 0;

        public string DoneText
        {
            get
            {
                if (!IsCheck) return "Erfolgreich gelöscht";
                if ((BadBlocks ?? 0) > 0) return "Unlesbare Bereiche gefunden";
                if (Purpose == "scan") return "Alle Sektoren lesbar";
                return NonZeroBytes == 0 ? "Laufwerk ist leer" : "Laufwerk enthält Daten";
            }
        }

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
                    if (_state != JobState.Running || _speed <= 1 || _total <= _done || Mode.Purge) return null;
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
                    case JobState.Done: return DoneText;
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
        internal void SetTestState(JobState state, double fraction, string message, string healthJson = null)
        {
            lock (_lock)
            {
                _state = state;
                _diskSize = Disk.Size;
                _total = Disk.Size * Math.Max(1, Mode.WorkUnits);
                _done = (long)(_total * fraction);
                _speed = 142_000_000;
                _step = 1;
                _phase = IsCheck ? "Alle Sektoren lesen" : "Durchgang 1: Zufallsdaten schreiben";
                _started = DateTime.Now.AddMinutes(-50);
                _finished = state == JobState.Done || state == JobState.Failed || state == JobState.Cancelled ? DateTime.Now : null;
                _message = message;
                _verified = state == JobState.Done ? true : null;
                _trim = Mode.Trims ? "durchgeführt – alle Blöcke freigegeben" : null;
                _sleep = state == JobState.Running;
                if (Mode.Verify == VerifyKind.Samples) _sampleResult = "bestanden – 2080 Stellen (je 1 MiB, inkl. Anfang und Ende) gelesen und verglichen";
                if (Mode.Purge) _purgeMethod = "NVMe Sanitize – Crypto Erase";
                if (IsCheck)
                {
                    int n = state == JobState.Running ? 90 : 200;
                    _curve = Enumerable.Range(0, n).Select(i => 160 - i * 0.35 - (i % 7 == 0 ? 12 : 0)).ToList();
                    _badBlocks = 0;
                    _nonZero = 0;
                }
            }
            HealthBefore = healthJson;
            HealthAfter = healthJson;
        }

        void Run()
        {
            Native.SetThreadExecutionState(Native.ES_CONTINUOUS | Native.ES_SYSTEM_REQUIRED);
            lock (_lock) _sleep = true;
            JobState result;
            string message;
            try
            {
                message = RunCore();
                result = JobState.Done;
            }
            catch (OperationCanceledException)
            {
                result = JobState.Cancelled;
                message = IsCheck ? "Vom Benutzer abgebrochen." : "Vom Benutzer abgebrochen. Das Laufwerk ist nur teilweise überschrieben.";
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

            if (Disk.Number >= 0)
            {
                SetPhase("Gesundheitsdaten werden gelesen …");
                try { HealthAfter = SmartService.Read(Disk).Json; } catch { }
            }
            Native.SetThreadExecutionState(Native.ES_CONTINUOUS);   // Ruhezustand wieder erlauben
            lock (_lock)
            {
                _sleep = false;
                _indeterminate = false;
                _state = result;
                _message = message;
                _finished = DateTime.Now;
                _phase = "Abgeschlossen";
                if (result == JobState.Done) _done = _total;
            }
        }

        string RunCore()
        {
            SetPhase("Laufwerk wird geprüft …");
            var current = DiskService.ListDisks().FirstOrDefault(d => d.Number == Disk.Number);
            if (current == null)
                throw new WipeException($"{Disk.Id} ist nicht (mehr) angeschlossen.");
            if (!Mode.ReadOnly)
            {
                if (current.IsProtected)
                    throw new WipeException($"{Disk.Id} enthält das laufende Windows und wird niemals gelöscht.");
                if (current.IsReadOnly)
                    throw new WipeException($"{Disk.Id} ist schreibgeschützt.");
                if (current.IsInternal && !InternalConfirmed)
                    throw new WipeException($"{Disk.Id} ist ein internes Laufwerk – das Löschen wurde nicht bestätigt.");
                if (!Mode.AvailableFor(current))
                    throw new WipeException("Die Firmware-Löschung ist unter Windows nur für direkt angeschlossene NVMe-SSDs möglich.");
            }
            if (Math.Abs(current.Size - Disk.Size) > 1024 * 1024)
                throw new WipeException($"Die Größe von {Disk.Id} hat sich geändert ({Fmt.Bytes(current.Size)} statt {Fmt.Bytes(Disk.Size)}). Wurde das Laufwerk getauscht? Abbruch aus Sicherheitsgründen.");
            CheckCancel();

            SetPhase("Gesundheitsdaten werden gelesen …");
            try { HealthBefore = SmartService.Read(current).Json; } catch { }
            CheckCancel();

            if (Mode.ReadOnly)
            {
                using var rh = Native.CreateFile(Disk.DevicePath, Native.GENERIC_READ,
                    Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING,
                    Native.FILE_FLAG_NO_BUFFERING, IntPtr.Zero);
                if (rh.IsInvalid)
                    throw new WipeException($"{Disk.Id} konnte nicht geöffnet werden: {LastError()}");
                long rsize = GetLength(rh);
                if (rsize <= 0) rsize = Disk.Size;
                return ExecuteCheck(rh, rsize);
            }

            // Alle Volumes (Laufwerksbuchstaben) des Datenträgers sperren und aushängen.
            // Ohne Sperre verweigert Windows das Überschreiben (»Zugriff verweigert«).
            SetPhase("Laufwerk wird ausgehängt …");
            using var handle = Native.CreateFile(Disk.DevicePath, Native.GENERIC_READ | Native.GENERIC_WRITE,
                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING,
                Native.FILE_FLAG_NO_BUFFERING | Native.FILE_FLAG_WRITE_THROUGH, IntPtr.Zero);
            if (handle.IsInvalid)
                throw new WipeException($"{Disk.Id} konnte nicht geöffnet werden: {LastError()}");

            long size = GetLength(handle);
            if (size <= 0) size = Disk.Size;
            if (Math.Abs(size - Disk.Size) > 1024 * 1024)
                throw new WipeException($"Die Größe von {Disk.Id} passt nicht ({Fmt.Bytes(size)} statt {Fmt.Bytes(Disk.Size)}). Abbruch aus Sicherheitsgründen.");

            var locker = VolumeLocker.LockAllOn(Disk.Number, () => _cancel);
            try
            {
                CheckCancel();
                try
                {
                    if (Mode.Purge) return ExecutePurge(handle, size);
                    ExecutePasses(handle, size);
                    return Mode.Verifies
                        ? "Alle Sektoren wurden überschrieben und die Prüfung war erfolgreich."
                        : "Alle Sektoren wurden mit Zufallsdaten überschrieben; die Stichproben-Prüfung war erfolgreich.";
                }
                catch (WipeException e) when (locker.Problems.Count > 0)
                {
                    throw new WipeException(e.Message + " Hinweis: " + string.Join(" ", locker.Problems) +
                        " Bitte alle Programme schließen, die auf das Laufwerk zugreifen, und erneut versuchen.");
                }
            }
            finally
            {
                locker.Dispose();
                UpdateDiskProperties(handle);   // Windows liest die (jetzt gelöschte) Partitionstabelle neu ein
            }
        }

        static unsafe void UpdateDiskProperties(SafeFileHandle handle)
        {
            Native.DeviceIoControl(handle, Native.IOCTL_DISK_UPDATE_PROPERTIES, null, 0, null, 0, out _, IntPtr.Zero);
        }

        /// <summary>1-MiB-Stichproben: Anfang und Ende (je 16 MiB) plus zufällig verteilte Stellen.</summary>
        internal static List<long> PickSamples(long size, int count)
        {
            var result = new List<long>();
            if (size < MiB) return result;
            long last = (size - MiB) / MiB;
            var picks = new HashSet<long>();
            for (long i = 0; i < Math.Min(16, last + 1); i++) picks.Add(i);
            for (long i = Math.Max(0, last - 15); i <= last; i++) picks.Add(i);
            long want = Math.Min(last + 1, picks.Count + count);
            while (picks.Count < want)
                picks.Add((long)(BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8), 0) % (ulong)(last + 1)));
            return picks.OrderBy(p => p).Select(p => p * MiB).ToList();
        }

        /// <summary>Alle Durchgänge, Prüfung und TRIM auf einem bereits geöffneten Laufwerk (oder einer Testdatei).</summary>
        internal unsafe void ExecutePasses(SafeFileHandle handle, long size)
        {
            byte* wbuf = (byte*)NativeMemory.AlignedAlloc(Chunk, 4096);
            byte* rbuf = (byte*)NativeMemory.AlignedAlloc(Chunk, 4096);
            try
            {
                var samples = Mode.Verify == VerifyKind.Samples ? PickSamples(size, 2048) : new List<long>();
                lock (_lock)
                {
                    _diskSize = size;
                    _total = size * Mode.WorkUnits + samples.Count * MiB;
                    _state = JobState.Running;
                    _started = DateTime.Now;
                }
                long baseOffset = 0;
                for (int i = 0; i < Mode.Passes.Length; i++)
                {
                    var p = Mode.Passes[i];
                    string label = WipeMode.PatternLabel(p);
                    SetStep(i + 1, Mode.Passes.Length > 1 ? $"Durchgang {i + 1}: {label}" : label);
                    WritePass(handle, wbuf, size, p, baseOffset, i == Mode.Passes.Length - 1 ? samples : null);
                    baseOffset += size;
                }

                if (Mode.Verify == VerifyKind.Full)
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
                else if (Mode.Verify == VerifyKind.Samples)
                {
                    SetStep(Mode.Passes.Length + 1, "Stichproben prüfen");
                    var (ok, text) = VerifySamples(handle, rbuf, samples, true, i => Report(baseOffset + (i + 1) * MiB));
                    lock (_lock) { _verified = ok; _sampleResult = text; }
                    if (!ok)
                        throw new WipeException("Stichproben-Prüfung FEHLGESCHLAGEN: " + text + " Das Laufwerk (oder der Adapter) arbeitet nicht zuverlässig.");
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

        unsafe void WritePass(SafeFileHandle handle, byte* buf, long size, Pattern pattern, long baseOffset, List<long> samples)
        {
            var span = new Span<byte>(buf, Chunk);
            if (pattern == Pattern.Zeros) span.Clear();
            var sampleSet = samples != null ? new HashSet<long>(samples) : null;
            long offset = 0;
            while (offset < size)
            {
                CheckCancel();
                int n = (int)Math.Min(Chunk, size - offset);
                if (pattern == Pattern.Random) RandomNumberGenerator.Fill(span.Slice(0, n));
                else if (pattern == Pattern.Seeded) FillSeeded(span.Slice(0, n), offset);
                if (sampleSet != null && sampleSet.Count > 0)
                    for (long s = offset; s + MiB <= offset + n; s += MiB)
                        if (sampleSet.Contains(s))
                            _sampleHashes[s] = SHA256.HashData(span.Slice((int)(s - offset), (int)MiB));
                WriteAt(handle, buf, n, offset, size);
                offset += n;
                Report(baseOffset + offset);
            }
            Native.FlushFileBuffers(handle);   // Schreib-Cache der Platte leeren
        }

        unsafe void WriteAt(SafeFileHandle handle, byte* buf, int n, long offset, long size)
        {
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
        }

        /// <summary>Liest n Bytes ab offset. Gibt false bei einem Lesefehler zurück.</summary>
        static unsafe bool ReadAt(SafeFileHandle handle, byte* buf, int n, long offset)
        {
            int done = 0;
            while (done < n)
            {
                long pos = offset + done;
                var ov = new NativeOverlapped { OffsetLow = (int)(pos & 0xFFFFFFFFL), OffsetHigh = (int)(pos >> 32) };
                if (!Native.ReadFile(handle, buf + done, n - done, out int read, &ov) || read == 0) return false;
                done += read;
            }
            return true;
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
                if (!ReadAt(handle, buf, n, offset))
                    throw new WipeException($"Lesefehler bei der Prüfung ({Fmt.Bytes(offset)}): {LastError()}");

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

        /// <summary>expectEqual: Daten müssen den Prüfsummen entsprechen (Schnell) bzw. sich verändert haben (Firmware-Löschung).</summary>
        unsafe (bool, string) VerifySamples(SafeFileHandle handle, byte* buf, List<long> samples, bool expectEqual, Action<int> progress)
        {
            var bad = new List<long>();
            for (int i = 0; i < samples.Count; i++)
            {
                CheckCancel();
                long s = samples[i];
                bool? same = null;
                if (ReadAt(handle, buf, (int)MiB, s))
                    same = _sampleHashes.TryGetValue(s, out var h) && SHA256.HashData(new ReadOnlySpan<byte>(buf, (int)MiB)).AsSpan().SequenceEqual(h);
                if (same == null || same.Value != expectEqual) bad.Add(s);
                progress(i);
            }
            if (bad.Count > 0)
                return (false, $"{bad.Count} von {samples.Count} geprüften Stellen auffällig (z. B. bei {string.Join(", ", bad.Take(3).Select(b => Fmt.Bytes(b)))}).");
            return (true, expectEqual
                ? $"bestanden – {samples.Count} Stellen (je 1 MiB, inkl. Anfang und Ende) gelesen und verglichen"
                : $"bestanden – alle {samples.Count} vorher geschriebenen Prüfmarken sind verschwunden");
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

        // --- Nur prüfen / Oberflächentest -------------------------------------------------------

        internal unsafe string ExecuteCheck(SafeFileHandle handle, long size)
        {
            byte* buf = (byte*)NativeMemory.AlignedAlloc(Chunk, 4096);
            try
            {
                lock (_lock)
                {
                    _diskSize = size;
                    _total = size;
                    _state = JobState.Running;
                    _started = DateTime.Now;
                    _step = 1;
                    _phase = "Alle Sektoren lesen";
                    _curve = new List<double>();
                    _badBlocks = 0;
                    _nonZero = 0;
                }
                long bucket = Math.Max(Chunk, size / 200);
                long bucketStart = 0;
                var bucketTime = DateTime.Now;
                long offset = 0;
                long nonZero = 0;
                int bad = 0;
                while (offset < size)
                {
                    CheckCancel();
                    int n = (int)Math.Min(Chunk, size - offset);
                    if (ReadAt(handle, buf, n, offset))
                    {
                        nonZero += CountNonZero(new ReadOnlySpan<byte>(buf, n), (int)MiB);
                    }
                    else
                    {
                        for (int o = 0; o < n; o += Sub)
                        {
                            int m = Math.Min(Sub, n - o);
                            if (ReadAt(handle, buf + o, m, offset + o)) nonZero += CountNonZero(new ReadOnlySpan<byte>(buf + o, m), m);
                            else if (++bad >= MaxBad)
                                throw new WipeException($"Mehr als {MaxBad} unlesbare Bereiche – Test abgebrochen. Das Laufwerk ist stark beschädigt.");
                        }
                    }
                    offset += n;
                    lock (_lock) { _nonZero = nonZero; _badBlocks = bad; }
                    if (offset - bucketStart >= bucket || offset >= size)
                    {
                        double dt = Math.Max(1e-6, (DateTime.Now - bucketTime).TotalSeconds);
                        lock (_lock) _curve.Add(Math.Round((offset - bucketStart) / dt / 1e6, 1));
                        bucketStart = offset;
                        bucketTime = DateTime.Now;
                    }
                    Report(offset);
                }
                string msg = bad > 0 ? $"{bad} unlesbare Bereiche (je 64 KiB) gefunden – das Laufwerk ist beschädigt. " : "Alle Sektoren sind lesbar. ";
                msg += nonZero == 0 ? "Das Laufwerk ist leer (nur Nullen)."
                    : $"{Fmt.Bytes(nonZero)} ({(nonZero * 100.0 / size).ToString("0.0", System.Globalization.CultureInfo.GetCultureInfo("de-DE"))} %) enthalten Daten (nicht nur Nullen).";
                lock (_lock) _verified = nonZero == 0 && bad == 0;
                return msg;
            }
            finally
            {
                NativeMemory.AlignedFree(buf);
            }
        }

        static long CountNonZero(ReadOnlySpan<byte> data, int step)
        {
            long total = 0;
            for (int o = 0; o < data.Length; o += step)
            {
                int m = Math.Min(step, data.Length - o);
                if (data.Slice(o, m).IndexOfAnyExcept((byte)0) >= 0) total += m;
            }
            return total;
        }

        // --- Firmware-Löschung (NVMe, IOCTL_STORAGE_REINITIALIZE_MEDIA) ------------------------------

        unsafe string ExecutePurge(SafeFileHandle handle, long size)
        {
            byte* buf = (byte*)NativeMemory.AlignedAlloc(Chunk, 4096);
            try
            {
                lock (_lock)
                {
                    _diskSize = size;
                    _total = 1000;
                    _state = JobState.Running;
                    _started = DateTime.Now;
                }
                // 1) Prüfmarken schreiben
                SetStep(1, "Prüfmarken schreiben");
                var markers = PickSamples(size, 64);
                var span = new Span<byte>(buf, (int)MiB);
                for (int i = 0; i < markers.Count; i++)
                {
                    CheckCancel();
                    RandomNumberGenerator.Fill(span);
                    _sampleHashes[markers[i]] = SHA256.HashData(span);
                    WriteAt(handle, buf, (int)MiB, markers[i], size);
                    Report(50L * (i + 1) / markers.Count);
                }
                Native.FlushFileBuffers(handle);

                // 2) Firmware-Befehl (blockiert, bis die SSD fertig ist)
                SetStep(2, "Firmware-Löschung läuft – nicht ausschalten");
                lock (_lock) { _indeterminate = true; _purgeMethod = "NVMe Sanitize – Crypto Erase (Windows)"; }
                Report(500);
                byte* input = stackalloc byte[16];
                var inp = new Span<byte>(input, 16);
                inp.Clear();
                BinaryPrimitives.WriteUInt32LittleEndian(inp.Slice(0), 16);       // Version = sizeof(STORAGE_REINITIALIZE_MEDIA)
                BinaryPrimitives.WriteUInt32LittleEndian(inp.Slice(4), 16);       // Size
                BinaryPrimitives.WriteUInt32LittleEndian(inp.Slice(8), 6 * 3600); // TimeoutInSeconds
                BinaryPrimitives.WriteUInt32LittleEndian(inp.Slice(12), 2);       // SanitizeMethod = CryptoErase
                bool ok = Native.DeviceIoControl(handle, IOCTL_STORAGE_REINITIALIZE_MEDIA, input, 16, null, 0, out _, IntPtr.Zero);
                int err = ok ? 0 : Marshal.GetLastWin32Error();
                if (!ok && err == 87)   // ERROR_INVALID_PARAMETER: ältere Windows-Versionen kennen die Parameter nicht
                {
                    ok = Native.DeviceIoControl(handle, IOCTL_STORAGE_REINITIALIZE_MEDIA, null, 0, null, 0, out _, IntPtr.Zero);
                    err = ok ? 0 : Marshal.GetLastWin32Error();
                }
                lock (_lock) _indeterminate = false;
                if (!ok)
                    throw new WipeException($"Die SSD bzw. Windows hat die Firmware-Löschung abgelehnt ({new Win32Exception(err).Message}). " +
                        "Viele SSDs unterstützen den Befehl nicht. Bitte den Modus »SSD« verwenden. Das Laufwerk wurde nicht gelöscht – nur einige Prüfmarken wurden geschrieben.");
                UpdateDiskProperties(handle);

                // 3) Kontrolle
                SetStep(3, "Prüfmarken kontrollieren");
                var (vok, text) = VerifySamples(handle, buf, markers, false, i => Report(950 + 50L * (i + 1) / markers.Count));
                lock (_lock) { _verified = vok; _sampleResult = text; }
                if (!vok)
                    throw new WipeException("Die Firmware-Löschung wurde gemeldet, aber " + text + " Die Löschung war vermutlich wirkungslos – bitte »SSD« verwenden.");
                return "Die SSD hat sich selbst gelöscht (Sanitize – Crypto Erase). Die Kontrolle der Prüfmarken war erfolgreich.";
            }
            finally
            {
                NativeMemory.AlignedFree(buf);
            }
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
