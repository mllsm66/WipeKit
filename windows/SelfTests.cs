using System;
using System.IO;
using Microsoft.Win32.SafeHandles;

namespace WipeKit
{
    /// <summary>Automatische Prüfungen, die beim Build auf GitHub laufen (nicht für Endanwender).</summary>
    internal static class SelfTests
    {
        /// <summary>Lässt die Lösch-Logik auf einer Testdatei laufen statt auf einem Laufwerk.</summary>
        public static int EngineTest(string path)
        {
            string log = path + ".log";
            try
            {
                long size = 40L * 1024 * 1024 + 3 * 4096;   // absichtlich kein Vielfaches von 4 MiB
                foreach (var key in new[] { "quick", "secure", "maximum", "ssd" })
                {
                    using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                        fs.SetLength(size);
                    var mode = WipeMode.ByKey(key);
                    var job = new WipeJob(new DiskInfo { Number = -1, Name = "Testdatei", Size = size }, mode);
                    using (SafeFileHandle h = Open(path))
                        job.ExecutePasses(h, size);
                    if (mode.Verifies && job.Verified != true)
                        throw new Exception($"{key}: Prüfung nicht bestanden");
                    File.AppendAllText(log, $"{key}: ok (TRIM: {job.TrimResult ?? "–"})\n");
                }

                // Eine nicht überschriebene Stelle muss erkannt werden
                var secure = WipeMode.ByKey("secure");
                var tamper = new TamperJob(new DiskInfo { Number = -1, Name = "Testdatei", Size = size }, secure, path, size);
                if (!tamper.DetectsLeftover())
                    throw new Exception("Manipulation wurde nicht erkannt");
                File.AppendAllText(log, "Manipulation erkannt: ok\n");
                File.Delete(path);
                return 0;
            }
            catch (Exception e)
            {
                File.AppendAllText(log, "FEHLER: " + e + "\n");
                return 1;
            }
        }

        /// <summary>Löscht einen echten (virtuellen) Datenträger mit eingehängtem Laufwerk – wie im Alltag.</summary>
        public static int DiskTest(int number, string modeKey)
        {
            string log = Path.Combine(Path.GetTempPath(), "WipeKit-DiskTest.log");
            try
            {
                DiskService.AllowVirtualForTests = true;
                var disk = DiskService.ListExternalDisks().Find(d => d.Number == number);
                if (disk == null) throw new Exception($"Datenträger {number} nicht gefunden");
                File.AppendAllText(log, $"Datenträger: {disk.Name}, {disk.Size} Bytes, Volumes: {string.Join(", ", disk.Volumes)}\n");
                var job = new WipeJob(disk, WipeMode.ByKey(modeKey));
                job.Start();
                job.Join(TimeSpan.FromMinutes(10));
                File.AppendAllText(log, $"Ergebnis: {job.State} – {job.Message} (Prüfung: {job.Verified}, TRIM: {job.TrimResult ?? "–"})\n");
                return job.State == JobState.Done && (!job.Mode.Verifies || job.Verified == true) ? 0 : 1;
            }
            catch (Exception e)
            {
                File.AppendAllText(log, "FEHLER: " + e + "\n");
                return 1;
            }
        }

        static SafeFileHandle Open(string path)
        {
            var h = Native.CreateFile(path, Native.GENERIC_READ | Native.GENERIC_WRITE,
                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING,
                Native.FILE_FLAG_NO_BUFFERING | Native.FILE_FLAG_WRITE_THROUGH, IntPtr.Zero);
            if (h.IsInvalid) throw new IOException("Testdatei konnte nicht geöffnet werden");
            return h;
        }

        /// <summary>Schreibt Nullen, verfälscht ein Byte und prüft, ob die Verifikation anschlägt.</summary>
        sealed class TamperJob
        {
            readonly string _path;
            readonly long _size;

            public TamperJob(DiskInfo disk, WipeMode mode, string path, long size)
            {
                _path = path;
                _size = size;
            }

            public bool DetectsLeftover()
            {
                // Datei mit Nullen, aber einem Byte != 0 kurz vor Schluss
                using (var fs = new FileStream(_path, FileMode.Create, FileAccess.Write))
                {
                    fs.SetLength(_size);
                    fs.Position = _size - 10;
                    fs.WriteByte(0x5A);
                }
                // Nur Prüfung ausführen: Modus ohne Schreibdurchgänge
                var verifyOnly = new WipeMode
                {
                    Key = "verify", Title = "Prüfung", Glyph = "", Tagline = "",
                    Passes = new Pattern[0], Verifies = true, Trims = false,
                };
                var job = new WipeJob(new DiskInfo { Number = -1, Name = "Testdatei", Size = _size }, verifyOnly);
                try
                {
                    using (SafeFileHandle h = Open(_path))
                        job.ExecutePasses(h, _size);
                    return false;
                }
                catch (WipeException)
                {
                    return true;
                }
            }
        }
    }
}
