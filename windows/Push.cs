using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WipeKit
{
    /// <summary>Ein Ereignis für die Benachrichtigung aufs Handy</summary>
    public sealed class PushEvent
    {
        public string Kind = "wipe";          // wipe | check | test
        public string State = "done";         // done | failed | cancelled
        public bool Warning;
        public string Notice = "";
        public Dictionary<string, object> Meta = new Dictionary<string, object>();
        public List<(string Name, byte[] Data)> Files = new List<(string, byte[])>();
    }

    public sealed class HubCode
    {
        public Uri Url;
        public string Token;
        public byte[] Key;
    }

    /// <summary>WipeKit Hub (Ende-zu-Ende-verschlüsselt), ntfy, Discord – siehe docs/hub-protocol.md</summary>
    public static class Push
    {
        static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

        public static bool ValidDiscord(string s)
        {
            if (!Uri.TryCreate((s ?? "").Trim(), UriKind.Absolute, out var u) || u.Scheme != "https") return false;
            string h = u.Host.ToLowerInvariant();
            return (h == "discord.com" || h == "discordapp.com" || h.EndsWith(".discord.com")) && u.AbsolutePath.StartsWith("/api/webhooks/");
        }

        public static HubCode ParseHubCode(string code)
        {
            try
            {
                string t = (code ?? "").Trim();
                if (!t.StartsWith("WKHUB1:")) return null;
                string b = t.Substring(7).Replace('-', '+').Replace('_', '/');
                while (b.Length % 4 != 0) b += "=";
                using var doc = JsonDocument.Parse(Convert.FromBase64String(b));
                var r = doc.RootElement;
                var url = new Uri(r.GetProperty("url").GetString());
                if (url.Scheme != "https" && url.Scheme != "http") return null;
                string token = r.GetProperty("token").GetString();
                var key = Convert.FromBase64String(r.GetProperty("key").GetString());
                if (string.IsNullOrEmpty(token) || key.Length != 65 || key[0] != 4) return null;
                return new HubCode { Url = url, Token = token, Key = key };
            }
            catch { return null; }
        }

        /// <summary>Verschlüsselt für den Hub („WKE1“)</summary>
        public static byte[] Seal(byte[] plain, byte[] pub)
        {
            var curve = ECCurve.NamedCurves.nistP256;
            using var recipient = ECDiffieHellman.Create(new ECParameters
            {
                Curve = curve,
                Q = new ECPoint { X = pub.Skip(1).Take(32).ToArray(), Y = pub.Skip(33).Take(32).ToArray() },
            });
            using var eph = ECDiffieHellman.Create(curve);
            var p = eph.ExportParameters(false);
            byte[] ephPub = new byte[65];
            ephPub[0] = 4;
            Buffer.BlockCopy(p.Q.X, 0, ephPub, 1, 32);
            Buffer.BlockCopy(p.Q.Y, 0, ephPub, 33, 32);
            byte[] secret = eph.DeriveRawSecretAgreement(recipient.PublicKey);
            byte[] key = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, ephPub.Concat(pub).ToArray(), Encoding.UTF8.GetBytes("WipeKit Hub v1"));
            byte[] nonce = RandomNumberGenerator.GetBytes(12);
            byte[] cipher = new byte[plain.Length];
            byte[] tag = new byte[16];
            using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, plain, cipher, tag, Encoding.ASCII.GetBytes("WKE1"));
            return Encoding.ASCII.GetBytes("WKE1").Concat(ephPub).Concat(nonce).Concat(cipher).Concat(tag).ToArray();
        }

        /// <summary>Sendet synchron. null = Erfolg, sonst Fehlermeldung.</summary>
        public static string Send(PushConfig c, PushEvent e, bool force = false)
        {
            if (!force)
            {
                bool wanted = (e.State == "done" && c.OnDone) || (e.State == "failed" && c.OnFailed)
                              || (e.State == "cancelled" && c.OnCancelled) || (e.Warning && c.OnWarning);
                if (!wanted) return null;
            }
            try
            {
                return c.Service switch
                {
                    "hub" => SendHub(c, e),
                    "ntfy" => SendNtfy(c, e),
                    "discord" => SendDiscord(c, e),
                    _ => "Keine Benachrichtigung eingerichtet.",
                };
            }
            catch (Exception ex)
            {
                return ex.InnerException?.Message ?? ex.Message;
            }
        }

        static string Run(HttpRequestMessage r)
        {
            r.Headers.UserAgent.ParseAdd("WipeKit/" + ShellWindow.AppVersion);
            using var resp = Http.Send(r);
            if ((int)resp.StatusCode >= 200 && (int)resp.StatusCode < 300) return null;
            string body = "";
            try { body = resp.Content.ReadAsStringAsync().Result; } catch { }
            if (body.Length > 200) body = body.Substring(0, 200);
            return $"Server antwortet mit {(int)resp.StatusCode}. {body}".Trim();
        }

        static string SendHub(PushConfig c, PushEvent e)
        {
            var hub = ParseHubCode(c.HubCode);
            if (hub == null) return "Der Kopplungscode für den Hub ist ungültig.";
            var files = c.HubFiles ? e.Files : new List<(string Name, byte[] Data)>();
            var meta = new Dictionary<string, object>(e.Meta) { ["files"] = files.Select(f => f.Name).ToArray() };
            var body = new Dictionary<string, object>
            {
                ["v"] = 1, ["id"] = Guid.NewGuid().ToString(), ["created"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["kind"] = e.Kind, ["state"] = e.State, ["warning"] = e.Warning, ["notice"] = e.Notice,
                ["meta"] = Convert.ToBase64String(Seal(JsonSerializer.SerializeToUtf8Bytes(meta), hub.Key)),
                ["files"] = files.Select(f => Convert.ToBase64String(Seal(f.Data, hub.Key))).ToArray(),
            };
            var r = new HttpRequestMessage(HttpMethod.Post, new Uri(hub.Url, "api/v1/events"))
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            };
            r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", hub.Token);
            return Run(r);
        }

        static string SendNtfy(PushConfig c, PushEvent e)
        {
            if (!Uri.TryCreate(c.NtfyServer.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var b) || c.NtfyTopic.Trim() == "")
                return "Server oder Thema für ntfy fehlt.";
            var r = new HttpRequestMessage(HttpMethod.Post, new Uri(b, Uri.EscapeDataString(c.NtfyTopic.Trim())))
            {
                Content = new StringContent(e.Notice, Encoding.UTF8, "text/plain"),
            };
            r.Headers.Add("Title", "WipeKit");
            r.Headers.Add("Tags", e.State == "done" && !e.Warning ? "white_check_mark" : (e.State == "cancelled" ? "warning" : "x"));
            if (e.State == "failed" || e.Warning) r.Headers.Add("Priority", "high");
            if (c.NtfyAuth == "token") r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", c.NtfySecret);
            else if (c.NtfyAuth == "password")
                r.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes($"{c.NtfyUser}:{c.NtfySecret}")));
            return Run(r);
        }

        static string SendDiscord(PushConfig c, PushEvent e)
        {
            if (!ValidDiscord(c.DiscordWebhook)) return "Die Webhook-Adresse für Discord ist ungültig.";
            int color = e.State == "done" && !e.Warning ? 0x1F8F3D : (e.State == "cancelled" || e.Warning ? 0xDB8C00 : 0xD93026);
            var body = new Dictionary<string, object>
            {
                ["username"] = "WipeKit",
                ["allowed_mentions"] = new Dictionary<string, object> { ["parse"] = Array.Empty<string>() },
                ["embeds"] = new[] { new Dictionary<string, object> { ["title"] = "WipeKit", ["description"] = e.Notice, ["color"] = color } },
            };
            var r = new HttpRequestMessage(HttpMethod.Post, c.DiscordWebhook.Trim())
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            };
            return Run(r);
        }
    }
}
