using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace WipeKit
{
    /// <summary>Laufwerke über die Windows-Speicherverwaltung (PowerShell) abfragen und vorbereiten.</summary>
    public static class DiskService
    {
        static readonly string[] ExternalBuses = { "USB", "SD", "MMC", "1394" };

        /// <summary>Nur für automatische Tests: virtuelle Festplatten (VHD) zulassen.</summary>
        internal static bool AllowVirtualForTests;

        const string ListScript = @"
$ErrorActionPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$disks = @(Get-Disk | ForEach-Object { [pscustomobject]@{ Number = [int]$_.Number; Name = [string]$_.FriendlyName; Size = [int64]$_.Size; Bus = [string]$_.BusType; IsBoot = [bool]$_.IsBoot; IsSystem = [bool]$_.IsSystem } })
$phys = @(Get-PhysicalDisk | ForEach-Object { [pscustomobject]@{ Id = [string]$_.DeviceId; Media = [string]$_.MediaType } })
$vols = @(Get-Partition | Where-Object { [int]$_.DriveLetter -ne 0 } | ForEach-Object { $v = $_ | Get-Volume; [pscustomobject]@{ Disk = [int]$_.DiskNumber; Letter = [string]$_.DriveLetter; Label = [string]$v.FileSystemLabel } })
[pscustomobject]@{ Disks = $disks; Physical = $phys; Volumes = $vols } | ConvertTo-Json -Depth 4 -Compress
";

        public static List<DiskInfo> ListExternalDisks()
        {
            var result = new List<DiskInfo>();
            string json = RunPowerShell(ListScript);
            if (string.IsNullOrWhiteSpace(json)) return result;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(json); }
            catch (JsonException) { return result; }
            using var _ = doc;
            var root = doc.RootElement;

            var media = new Dictionary<string, string>();
            foreach (var p in Items(root, "Physical"))
                media[Str(p, "Id")] = Str(p, "Media");

            var volumes = new Dictionary<int, List<string>>();
            foreach (var v in Items(root, "Volumes"))
            {
                int disk = Int(v, "Disk");
                string label = Str(v, "Label");
                string letter = Str(v, "Letter");
                string text = string.IsNullOrWhiteSpace(label) ? letter + ":" : $"{label} ({letter}:)";
                if (!volumes.TryGetValue(disk, out var list)) volumes[disk] = list = new List<string>();
                list.Add(text);
            }

            foreach (var d in Items(root, "Disks"))
            {
                string bus = Str(d, "Bus");
                bool testDisk = AllowVirtualForTests && bus == "File Backed Virtual";
                if (!ExternalBuses.Contains(bus, StringComparer.OrdinalIgnoreCase) && !testDisk) continue;
                long size = Long(d, "Size");
                if (size <= 0) continue;   // z. B. leerer Kartenleser
                int number = Int(d, "Number");
                media.TryGetValue(number.ToString(), out string m);
                bool? ssd = m switch { "SSD" => true, "HDD" => false, _ => null };
                result.Add(new DiskInfo
                {
                    Number = number,
                    Name = string.IsNullOrWhiteSpace(Str(d, "Name")) ? "Unbekanntes Laufwerk" : Str(d, "Name").Trim(),
                    Size = size,
                    Bus = bus.Equals("1394", StringComparison.Ordinal) ? "FireWire" : bus.ToUpperInvariant() == "MMC" ? "SD" : bus,
                    IsSsd = ssd,
                    Volumes = volumes.TryGetValue(number, out var vl) ? vl : new List<string>(),
                    IsProtected = Bool(d, "IsBoot") || Bool(d, "IsSystem"),
                });
            }
            return result.OrderBy(x => x.Number).ToList();
        }

        public static string RunPowerShell(string script, int timeoutMs = 120000)
        {
            string exe = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var psi = new ProcessStartInfo(exe, "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi);
            if (p == null) return "";
            var errTask = p.StandardError.ReadToEndAsync();
            string output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(true); } catch { }
            }
            errTask.Wait(2000);
            return output.Trim('﻿', ' ', '\r', '\n', '\t');
        }

        // --- JSON-Hilfen -------------------------------------------------------------

        static IEnumerable<JsonElement> Items(JsonElement root, string name)
        {
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var e)) yield break;
            if (e.ValueKind == JsonValueKind.Array)
            {
                foreach (var x in e.EnumerateArray()) yield return x;
            }
            else if (e.ValueKind == JsonValueKind.Object)
            {
                yield return e;
            }
        }

        static string Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" :
            e.TryGetProperty(name, out v) && v.ValueKind == JsonValueKind.Number ? v.GetRawText() : "";

        static int Int(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : -1;

        static long Long(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long l) ? l : 0;

        static bool Bool(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
    }
}
