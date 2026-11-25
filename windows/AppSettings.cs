using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WipeKit
{
    /// <summary>Zugangsdaten für die Benachrichtigungen aufs Handy</summary>
    public sealed class PushConfig
    {
        public string Service { get; set; } = "off";        // off | hub | ntfy | discord
        public bool OnDone { get; set; } = true;
        public bool OnFailed { get; set; } = true;
        public bool OnCancelled { get; set; } = true;
        public bool OnWarning { get; set; } = true;
        public bool HubFiles { get; set; } = true;
        public string NtfyServer { get; set; } = "https://ntfy.sh";
        public string NtfyTopic { get; set; } = "";
        public string NtfyAuth { get; set; } = "none";      // none | token | password
        public string NtfyUser { get; set; } = "";
        [JsonIgnore] public string HubCode { get; set; } = "";
        [JsonIgnore] public string NtfySecret { get; set; } = "";
        [JsonIgnore] public string DiscordWebhook { get; set; } = "";

        // Verschlüsselt mit DPAPI (nur dieser Windows-Benutzer kann sie lesen)
        public string HubCodeProtected { get; set; } = "";
        public string NtfySecretProtected { get; set; } = "";
        public string DiscordWebhookProtected { get; set; } = "";

        public bool IsConfigured => Service switch
        {
            "hub" => Push.ParseHubCode(HubCode) != null,
            "ntfy" => Uri.TryCreate(NtfyServer, UriKind.Absolute, out var u) && u.Scheme.StartsWith("http") && NtfyTopic.Trim() != "",
            "discord" => Push.ValidDiscord(DiscordWebhook),
            _ => false,
        };
    }

    /// <summary>Einstellungen unter %APPDATA%\WipeKit\settings.json</summary>
    public sealed class AppSettings
    {
        public bool NotifyLocal { get; set; } = true;
        public bool PlaySound { get; set; } = true;
        public string DefaultMode { get; set; } = "secure";
        public string ReportsPath { get; set; } = "";
        public bool DetailedReport { get; set; } = true;
        public bool SerialInFileName { get; set; } = true;
        public string OperatorName { get; set; } = "";
        public string Organization { get; set; } = "";
        public string ReportNote { get; set; } = "";
        public bool AutoCheck { get; set; } = false;
        public bool AutoInstall { get; set; } = false;
        public bool UpdatesAsked { get; set; } = false;
        public PushConfig Push { get; set; } = new PushConfig();

        static AppSettings _current;
        public static AppSettings Current => _current ??= Load();

        static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WipeKit");
        static string FilePath => Path.Combine(Dir, "settings.json");

        public static string DefaultReportsPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "WipeKit-Protokolle");

        static AppSettings Load()
        {
            AppSettings s = null;
            try
            {
                if (File.Exists(FilePath)) s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
            }
            catch { }
            s ??= new AppSettings();
            s.Push ??= new PushConfig();
            s.Push.HubCode = Unprotect(s.Push.HubCodeProtected);
            s.Push.NtfySecret = Unprotect(s.Push.NtfySecretProtected);
            s.Push.DiscordWebhook = Unprotect(s.Push.DiscordWebhookProtected);
            return s;
        }

        public void Save()
        {
            try
            {
                Push.HubCodeProtected = Protect(Push.HubCode);
                Push.NtfySecretProtected = Protect(Push.NtfySecret);
                Push.DiscordWebhookProtected = Protect(Push.DiscordWebhook);
                Directory.CreateDirectory(Dir);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        public void Reset()
        {
            try { File.Delete(FilePath); } catch { }
            _current = new AppSettings { UpdatesAsked = true };
            _current.Save();
        }

        public string ReportsFolder => string.IsNullOrWhiteSpace(ReportsPath) ? DefaultReportsPath : ReportsPath;

        public List<(string, string)> ReportMeta
        {
            get
            {
                var rows = new List<(string, string)>();
                if (OperatorName.Trim() != "") rows.Add(("Durchgeführt von", OperatorName.Trim()));
                if (Organization.Trim() != "") rows.Add(("Firma / Organisation", Organization.Trim()));
                if (ReportNote.Trim() != "") rows.Add(("Notiz", ReportNote.Trim()));
                return rows;
            }
        }

        // --- DPAPI (crypt32.dll, ohne Zusatzpaket) -------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        struct DataBlob { public int cbData; public IntPtr pbData; }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CryptProtectData(ref DataBlob dataIn, string description, IntPtr entropy, IntPtr reserved,
                                            IntPtr prompt, int flags, ref DataBlob dataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, IntPtr entropy, IntPtr reserved,
                                              IntPtr prompt, int flags, ref DataBlob dataOut);

        [DllImport("kernel32.dll")]
        static extern IntPtr LocalFree(IntPtr mem);

        static byte[] Dpapi(byte[] data, bool protect)
        {
            var input = new DataBlob { cbData = data.Length, pbData = Marshal.AllocHGlobal(Math.Max(1, data.Length)) };
            var output = new DataBlob();
            try
            {
                Marshal.Copy(data, 0, input.pbData, data.Length);
                bool ok = protect
                    ? CryptProtectData(ref input, "WipeKit", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1 /* UI_FORBIDDEN */, ref output)
                    : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, ref output);
                if (!ok) return null;
                var result = new byte[output.cbData];
                Marshal.Copy(output.pbData, result, 0, output.cbData);
                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(input.pbData);
                if (output.pbData != IntPtr.Zero) LocalFree(output.pbData);
            }
        }

        static string Protect(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var b = Dpapi(Encoding.UTF8.GetBytes(s), true);
            return b == null ? "" : Convert.ToBase64String(b);
        }

        static string Unprotect(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            try
            {
                var b = Dpapi(Convert.FromBase64String(s), false);
                return b == null ? "" : Encoding.UTF8.GetString(b);
            }
            catch { return ""; }
        }
    }
}
