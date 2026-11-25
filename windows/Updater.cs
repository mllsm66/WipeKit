using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;

namespace WipeKit
{
    public sealed class ReleaseInfo
    {
        public string Version;
        public string PageUrl;
        public string SetupUrl;
        public string SignatureUrl;
    }

    /// <summary>
    /// Updates aus den GitHub-Releases: Installer und Signatur (.sig) laden, Signatur mit dem eingebauten
    /// öffentlichen Schlüssel prüfen (ECDSA P-256, SHA-256), dann den Installer still ausführen.
    /// </summary>
    public static class Updater
    {
        const string Repo = "mllsm66/WipeKit";
        static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

        public static string Status = "Noch nicht gesucht.";

        static string PublicKeyPem
        {
            get
            {
                using var st = typeof(Updater).Assembly.GetManifestResourceStream("WipeKit.update-public-key.pem");
                if (st == null) return null;
                using var r = new StreamReader(st);
                return r.ReadToEnd();
            }
        }

        /// <summary>Mit dem Installer eingerichtet (nicht die portable Fassung)?</summary>
        public static bool IsInstalled
        {
            get
            {
                string dir = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "";
                return File.Exists(Path.Combine(dir, "unins000.exe"));
            }
        }

        public static int Compare(string a, string b)
        {
            int[] pa = a.Split('.', '-').Select(x => int.TryParse(x, out var n) ? n : 0).ToArray();
            int[] pb = b.Split('.', '-').Select(x => int.TryParse(x, out var n) ? n : 0).ToArray();
            for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
            {
                int x = i < pa.Length ? pa[i] : 0, y = i < pb.Length ? pb[i] : 0;
                if (x != y) return x < y ? -1 : 1;
            }
            return 0;
        }

        public static bool CanInstall(ReleaseInfo r) =>
            r != null && r.SetupUrl != null && r.SignatureUrl != null && PublicKeyPem != null && IsInstalled;

        /// <summary>Sucht das neueste Release. null = keine neuere Version (Status erklärt warum).</summary>
        public static async Task<ReleaseInfo> Check()
        {
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
                req.Headers.UserAgent.ParseAdd("WipeKit/" + ShellWindow.AppVersion);
                req.Headers.Accept.ParseAdd("application/vnd.github+json");
                using var resp = await Http.SendAsync(req);
                if ((int)resp.StatusCode == 404) { Status = "Noch keine Version veröffentlicht."; return null; }
                resp.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                var root = doc.RootElement;
                string tag = root.GetProperty("tag_name").GetString() ?? "";
                string v = tag.TrimStart('v');
                Stamp();
                if (Compare(v, ShellWindow.AppVersion) <= 0) { Status = "WipeKit ist aktuell." + _time; return null; }
                string name = $"WipeKit-Windows-Setup-{v}.exe";
                string Url(string n) => root.GetProperty("assets").EnumerateArray()
                    .Where(a => a.GetProperty("name").GetString() == n)
                    .Select(a => a.GetProperty("browser_download_url").GetString()).FirstOrDefault();
                var info = new ReleaseInfo
                {
                    Version = v,
                    PageUrl = root.TryGetProperty("html_url", out var h) ? h.GetString() : $"https://github.com/{Repo}/releases/latest",
                    SetupUrl = Url(name),
                    SignatureUrl = Url(name + ".sig"),
                };
                Status = $"Version {v} ist verfügbar." + _time;
                if (!IsInstalled) Status += " (Portable Fassung: bitte von GitHub laden.)";
                else if (!CanInstall(info)) Status += " Automatisch installieren geht nicht (keine Signatur) – bitte von GitHub laden.";
                return info;
            }
            catch (Exception ex)
            {
                Status = "Keine Verbindung: " + ex.Message;
                return null;
            }
        }

        static string _time = "";
        static void Stamp() => _time = $" ({DateTime.Now:HH:mm} Uhr)";

        /// <summary>Lädt, prüft und startet den Installer. Gibt eine Fehlermeldung zurück oder null (dann beenden).</summary>
        public static async Task<string> Install(ReleaseInfo r)
        {
            if (!CanInstall(r)) return "Dieses Update lässt sich nicht automatisch installieren.";
            try
            {
                byte[] setup = await Http.GetByteArrayAsync(r.SetupUrl);
                byte[] sig = await Http.GetByteArrayAsync(r.SignatureUrl);
                using var ec = ECDsa.Create();
                ec.ImportFromPem(PublicKeyPem);
                if (!ec.VerifyData(setup, sig, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
                    return "Die Signatur des Updates ist ungültig – es wurde nichts installiert.";
                string dir = Path.Combine(Path.GetTempPath(), "WipeKit-Update-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, $"WipeKit-Windows-Setup-{r.Version}.exe");
                await File.WriteAllBytesAsync(path, setup);
                Process.Start(new ProcessStartInfo(path, "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS")
                {
                    UseShellExecute = true,
                });
                return null;
            }
            catch (Exception ex)
            {
                return "Update fehlgeschlagen: " + ex.Message;
            }
        }
    }
}
