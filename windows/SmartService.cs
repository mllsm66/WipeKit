using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace WipeKit
{
    /// <summary>
    /// S.M.A.R.T.-Daten über smartctl.exe (smartmontools 7.5, GPL-2.0-or-later).
    /// smartctl ist ein eigenständiges Programm, das in WipeKit.exe eingebettet ist. Es wird in einen Ordner
    /// entpackt, in den nur Administratoren schreiben dürfen, und vor jedem Start anhand der Prüfsumme kontrolliert.
    /// </summary>
    public static class SmartService
    {
        const string ResourceName = "WipeKit.smartctl.exe";
        static readonly string[] UsbTypes = { "sat", "sntasmedia", "sntjmicron", "sntrealtek", "usbjmicron", "usbcypress", "usbprolific", "usbsunplus" };
        static readonly object Lock = new object();
        static byte[] _embedded;
        static byte[] _hash;

        public const string MissingJson = "{\"smartctl\":{\"messages\":[{\"string\":\"smartctl fehlt\",\"severity\":\"error\"}]}}";

        public static bool Available => Embedded != null;

        static byte[] Embedded
        {
            get
            {
                if (_embedded != null) return _embedded;
                using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
                if (s == null) return null;
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                _embedded = ms.ToArray();
                _hash = SHA256.HashData(_embedded);
                return _embedded;
            }
        }

        static string BinDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WipeKit", "smartctl-7.5");

        /// <summary>Gibt den Pfad zu einer geprüften smartctl.exe zurück (oder null).</summary>
        static string EnsureExtracted()
        {
            lock (Lock)
            {
                var data = Embedded;
                if (data == null) return null;
                string dir = BinDir;
                string exe = Path.Combine(dir, "smartctl.exe");
                SecureDirectory(Path.GetDirectoryName(dir));
                SecureDirectory(dir);
                bool ok = false;
                try { ok = File.Exists(exe) && SHA256.HashData(File.ReadAllBytes(exe)).SequenceEqual(_hash); } catch { }
                if (!ok)
                {
                    try { if (File.Exists(exe)) File.Delete(exe); } catch { }
                    File.WriteAllBytes(exe, data);
                    try { File.WriteAllText(Path.Combine(dir, "LIESMICH.txt"), "smartctl.exe stammt aus smartmontools 7.5 (GPL-2.0-or-later) und wird von WipeKit für die Diagnose verwendet.\r\nQuelltext: https://github.com/mllsm66/WipeKit/releases (smartmontools-7.5.tar.gz)\r\n"); } catch { }
                }
                return exe;
            }
        }

        /// <summary>Ordner nur für Administratoren und SYSTEM beschreibbar machen (Benutzer: lesen/ausführen).</summary>
        static void SecureDirectory(string dir)
        {
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            var sec = new DirectorySecurity();
            sec.SetAccessRuleProtection(true, false);
            const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            sec.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            sec.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            sec.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
            sec.SetOwner(admins);
            var info = new DirectoryInfo(dir);
            if (!info.Exists) info.Create(sec);
            else info.SetAccessControl(sec);
        }

        static (int Code, string Out) Run(string exe, string args, int timeoutMs = 90000)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi);
            if (p == null) return (-1, "");
            var err = p.StandardError.ReadToEndAsync();
            string output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } }
            err.Wait(2000);
            return (p.HasExited ? p.ExitCode : -1, output);
        }

        static bool HasData(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                return HealthParser.HasData(doc.RootElement);
            }
            catch (JsonException) { return false; }
        }

        /// <summary>Liest die Gesundheitsdaten. Gibt die Auswertung und das rohe JSON zurück.</summary>
        public static (Health Health, string Json, string DevType) Read(DiskInfo d)
        {
            string exe;
            try { exe = EnsureExtracted(); }
            catch (Exception e)
            {
                var h = HealthParser.ParseText(MissingJson);
                h.Reason = "Das Diagnoseprogramm smartctl konnte nicht vorbereitet werden: " + e.Message;
                return (h, MissingJson, "");
            }
            if (exe == null) return (HealthParser.ParseText(MissingJson), MissingJson, "");
            string dev = "/dev/pd" + d.Number;
            var types = new[] { "" }.Concat(d.Bus.Equals("USB", StringComparison.OrdinalIgnoreCase) ? UsbTypes : Array.Empty<string>());
            string first = null;
            foreach (var t in types)
            {
                var (_, json) = Run(exe, "-x -j " + (t == "" ? "" : "-d " + t + " ") + dev);
                if (string.IsNullOrWhiteSpace(json)) continue;
                first ??= json;
                if (HasData(json)) return (HealthParser.ParseText(json), json, t);
            }
            first ??= MissingJson;
            return (HealthParser.ParseText(first), first, "");
        }

        /// <summary>Startet einen Selbsttest (short, long, conveyance) oder bricht ihn ab (abort).</summary>
        public static (bool Ok, string Message) SelfTest(DiskInfo d, string devType, string test)
        {
            string exe = EnsureExtracted();
            if (exe == null) return (false, "smartctl fehlt.");
            string args = (test == "abort" ? "-j -X " : "-j -t " + test + " ") + (string.IsNullOrEmpty(devType) ? "" : "-d " + devType + " ") + "/dev/pd" + d.Number;
            var (code, json) = Run(exe, args, 60000);
            string msg = "";
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("smartctl", out var sm) && sm.TryGetProperty("messages", out var ms))
                    msg = string.Join("; ", ms.EnumerateArray().Select(m => m.TryGetProperty("string", out var s) ? s.GetString() : ""));
            }
            catch (JsonException) { }
            bool ok = code >= 0 && (code & 0b11) == 0 && !msg.ToLowerInvariant().Contains("not supported");
            return (ok, msg);
        }
    }
}
