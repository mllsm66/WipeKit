using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WipeKit
{
    // Auswertung der S.M.A.R.T.-Daten (smartctl -x -j) – gleiche Logik wie in der Linux- und Mac-Version.

    public enum Rating { VeryGood, Good, Caution, Bad, Unknown }

    public sealed class SmartAttribute
    {
        public int Id;
        public string Name;
        public long Value, Worst, Thresh;
        public long? Raw;
        public string RawString;
        public string Failed;
        public string Status;   // OK, Warnung, Fehler
    }

    public sealed class SelfTestEntry
    {
        public string Kind, Result;
        public long? Hours;
        public bool? Passed;
    }

    public sealed class Health
    {
        public bool Available;
        public string Reason = "";
        public string Protocol = "", Vendor = "", Family = "", Model = "", ModelNumber = "", Serial = "", Firmware = "";
        public long Capacity;
        public bool? IsSsd;
        public long? Rpm;
        public string FormFactor = "", Interface = "", InterfaceMax = "", InterfaceCurrent = "", SataVersion = "", AtaVersion = "";
        public bool? Trim;
        public string Security = "";
        public bool? SecurityFrozen;
        public bool? SmartPassed;
        public long? Temperature, PowerOnHours, PowerCycles;
        public long? BytesWritten, BytesRead;
        public long? Pending, Uncorrectable, Reallocated, CrcErrors;
        public long? LifePercent;
        public List<KeyValuePair<string, JsonElement>> Nvme = new List<KeyValuePair<string, JsonElement>>();
        public List<SmartAttribute> Attributes = new List<SmartAttribute>();
        public List<SelfTestEntry> SelfTests = new List<SelfTestEntry>();
        public long? SelfTestRemaining;
        public bool SupportsSelfTests, SupportsConveyance;
        public long? ShortMinutes, LongMinutes;
        public Rating Rating = Rating.Unknown;
        public List<string> Reasons = new List<string>();

        public long? NvmeValue(string key)
        {
            foreach (var kv in Nvme)
                if (kv.Key == key && kv.Value.ValueKind == JsonValueKind.Number && kv.Value.TryGetInt64(out long v)) return v;
            return null;
        }
    }

    public static class HealthParser
    {
        public static string RatingText(Rating r) => r switch
        {
            Rating.VeryGood => "Sehr gut",
            Rating.Good => "Gut",
            Rating.Caution => "Bedenklich",
            Rating.Bad => "Schlecht",
            _ => "Unbekannt",
        };

        public static (double R, double G, double B) RatingRgb(Rating r) => r switch
        {
            Rating.VeryGood => (0.12, 0.56, 0.24),
            Rating.Good => (0.10, 0.45, 0.91),
            Rating.Caution => (0.86, 0.55, 0.00),
            Rating.Bad => (0.85, 0.19, 0.15),
            _ => (0.50, 0.53, 0.55),
        };

        static readonly Dictionary<int, string> AttributeNames = new Dictionary<int, string>
        {
            [1] = "Lesefehlerrate", [2] = "Durchsatzleistung", [3] = "Anlaufzeit", [4] = "Start/Stopp-Zähler",
            [5] = "Ersetzte Sektoren", [7] = "Suchfehlerrate", [8] = "Suchzeit-Leistung", [9] = "Betriebsstunden",
            [10] = "Anlauf-Wiederholungen", [11] = "Kalibrierungs-Wiederholungen", [12] = "Einschaltvorgänge",
            [13] = "Soft-Lesefehlerrate", [160] = "Unkorrigierbare Sektoren (Lesen/Schreiben)", [161] = "Verfügbare Reserveblöcke",
            [163] = "Anfänglich defekte Blöcke", [164] = "Gesamte Löschzyklen", [165] = "Maximale Löschzyklen",
            [166] = "Minimale Löschzyklen", [167] = "Durchschnittliche Löschzyklen", [168] = "Maximale Löschzyklen (Spezifikation)",
            [169] = "Restlebensdauer", [170] = "Verfügbare Reserveblöcke", [171] = "Programmierfehler", [172] = "Löschfehler",
            [173] = "Verschleißausgleich (Löschzyklen)", [174] = "Unerwartete Stromausfälle", [175] = "Stromausfallschutz",
            [177] = "Verschleißausgleich", [179] = "Belegte Reserveblöcke", [180] = "Freie Reserveblöcke",
            [181] = "Programmierfehler", [182] = "Löschfehler", [183] = "SATA-Herabstufungen", [184] = "Ende-zu-Ende-Fehler",
            [187] = "Gemeldete unkorrigierbare Fehler", [188] = "Befehls-Zeitüberschreitungen", [189] = "Hohe Flughöhe",
            [190] = "Luftstromtemperatur", [191] = "Erschütterungen", [192] = "Notparkvorgänge", [193] = "Lade-/Entladezyklen",
            [194] = "Temperatur", [195] = "Hardware-ECC-Korrekturen", [196] = "Umlagerungsereignisse",
            [197] = "Ausstehende Sektoren", [198] = "Unkorrigierbare Sektoren", [199] = "CRC-Fehler (Kabel/Adapter)",
            [200] = "Schreibfehlerrate", [201] = "Soft-Lesefehler", [202] = "Restlebensdauer", [206] = "Flughöhe",
            [220] = "Plattenverschiebung", [222] = "Belastete Stunden", [223] = "Lade-/Entlade-Wiederholungen",
            [224] = "Lade-Reibung", [225] = "Lade-/Entladezyklen", [226] = "Ladezeit", [230] = "Kopfamplitude",
            [231] = "Restlebensdauer", [232] = "Verfügbare Reserve", [233] = "Verschleißindikator", [234] = "Durchschnittliche Löschzyklen",
            [235] = "Gute Blöcke / Systemblöcke", [240] = "Kopf-Flugstunden", [241] = "Geschriebene Daten", [242] = "Gelesene Daten",
            [246] = "Geschriebene Daten (gesamt)", [247] = "Geschriebene Seiten (Host)", [248] = "Geschriebene Seiten (FTL)",
            [249] = "Geschriebene Daten (NAND, GiB)", [254] = "Freifall-Schutz",
        };

        static readonly (string Pattern, string Vendor)[] Vendors =
        {
            (@"^WDC\b", "Western Digital"), (@"^WD_?BLACK", "Western Digital"), (@"^WD\d", "Western Digital"), (@"^WDS\d", "Western Digital"),
            (@"^WD\b", "Western Digital"), (@"^Western Digital", "Western Digital"), (@"^ST\d", "Seagate"),
            (@"^Seagate", "Seagate"), (@"^TOSHIBA", "Toshiba"), (@"^KIOXIA", "Kioxia"), (@"^HGST", "HGST"),
            (@"^(HUS|HUH|HUA)", "HGST"), (@"^(Hitachi|HDS\d|HTS\d|HDT\d)", "Hitachi"), (@"^(SAMSUNG|Samsung|MZ-|MZ7|MZV)", "Samsung"), (@"^HD\d{3}[A-Z]{1,2}J\b", "Samsung"),
            (@"^(Crucial|CT\d)", "Crucial"), (@"^(Micron|MTFD)", "Micron"), (@"^(INTEL|Intel|SSDSC|SSDPE)", "Intel"),
            (@"^(KINGSTON|Kingston|SA400|SKC|SNV|SUV)", "Kingston"), (@"^(SanDisk|SDSSD)", "SanDisk"),
            (@"^(SK ?hynix|HFS\d|HFM\d)", "SK hynix"), (@"^(ADATA|XPG)", "ADATA"), (@"^(Transcend|TS\d+G)", "Transcend"),
            (@"^(APPLE|Apple)", "Apple"), (@"^Maxtor", "Maxtor"), (@"^(FUJITSU|Fujitsu|MHV|MHW|MHZ|MJA)", "Fujitsu"),
            (@"^(Corsair|CSSD)", "Corsair"), (@"^Patriot", "Patriot"), (@"^PNY", "PNY"), (@"^Lexar", "Lexar"),
            (@"^Intenso", "Intenso"), (@"^Verbatim", "Verbatim"), (@"^Phison", "Phison"), (@"^(TEAM|Team)", "Teamgroup"),
            (@"^Netac", "Netac"), (@"^(Silicon Power|SPCC)", "Silicon Power"), (@"^(Gigabyte|GIGABYTE|GP-)", "Gigabyte"),
            (@"^Sabrent", "Sabrent"), (@"^(LITEON|LITE-ON|Lite-On)", "Lite-On"), (@"^(Plextor|PLEXTOR)", "Plextor"),
            (@"^OCZ", "OCZ"), (@"^Lenovo", "Lenovo"), (@"^(Dell|DELL)", "Dell"), (@"^(HP\b|HPE)", "HP"),
            (@"^(Hikvision|HS-SSD)", "Hikvision"), (@"^Mushkin", "Mushkin"), (@"^(Goodram|GOODRAM|IR-SSD)", "Goodram"),
            (@"^Inland", "Inland"), (@"^Kingdian", "Kingdian"), (@"^Fanxiang", "Fanxiang"), (@"^(Biwin|BIWIN)", "Biwin"),
        };

        static readonly HashSet<string> VendorPrefixTokens = new HashSet<string>
        {
            "WDC", "WD", "TOSHIBA", "HGST", "Hitachi", "SAMSUNG", "Samsung", "INTEL", "KINGSTON", "Kingston", "SanDisk",
            "Crucial", "Micron", "APPLE", "Apple", "FUJITSU", "ADATA", "Transcend", "Seagate", "Corsair", "PNY", "Lexar",
            "Intenso", "Verbatim", "Patriot",
        };

        public static string DetectVendor(params string[] names)
        {
            foreach (var n in names)
            {
                if (string.IsNullOrWhiteSpace(n)) continue;
                foreach (var (p, v) in Vendors)
                    if (Regex.IsMatch(n.Trim(), p)) return v;
            }
            return "";
        }

        public static string ModelNumber(string name)
        {
            var parts = (name ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (parts.Count > 1 && VendorPrefixTokens.Contains(parts[0])) parts.RemoveAt(0);
            return string.Join(" ", parts);
        }

        // --- JSON-Hilfen ---------------------------------------------------------------------

        static JsonElement Get(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;

        static bool Has(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out _);

        static string Str(JsonElement e) => e.ValueKind == JsonValueKind.String ? (e.GetString() ?? "").Trim() : "";

        static long? Num(JsonElement e) =>
            e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out long v) ? v :
            e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out double d) ? (long)d : null;

        static bool? Bool(JsonElement e) =>
            e.ValueKind == JsonValueKind.True ? true : e.ValueKind == JsonValueKind.False ? false : null;

        static IEnumerable<JsonElement> Arr(JsonElement e) =>
            e.ValueKind == JsonValueKind.Array ? e.EnumerateArray() : Enumerable.Empty<JsonElement>();

        public static string FriendlyReason(IEnumerable<string> msgs)
        {
            string text = string.Join(" ", msgs).ToLowerInvariant();
            if (text.Contains("smartctl fehlt"))
                return "Das Diagnoseprogramm smartctl fehlt in dieser WipeKit-Version.";
            if (text.Contains("usb bridge") || text.Contains("unsupported usb") || text.Contains("please specify device type") || text.Contains("unknown usb"))
                return "Der USB-Adapter bzw. das USB-Gehäuse gibt die Gesundheitsdaten (S.M.A.R.T.) nicht weiter.";
            if (text.Contains("permission") || text.Contains("access is denied") || text.Contains("zugriff verweigert"))
                return "Für das Auslesen der Gesundheitsdaten fehlen die nötigen Rechte.";
            if (text.Contains("smart support is: unavailable") || text.Contains("smart support is: disabled") || text.Contains("smart not supported"))
                return "Das Laufwerk unterstützt keine Gesundheitsdaten (S.M.A.R.T.) oder hat sie abgeschaltet.";
            if (text.Contains("no such device") || text.Contains("open device") || text.Contains("unable to detect device type"))
                return "Das Laufwerk konnte nicht abgefragt werden (Adapter oder Treiber unterstützt es nicht).";
            return "Für dieses Laufwerk sind keine Gesundheitsdaten (S.M.A.R.T.) verfügbar.";
        }

        public static Health ParseText(string text)
        {
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
                return Parse(doc.RootElement.Clone());
            }
            catch (JsonException)
            {
                return new Health { Reason = "Die Diagnosedaten konnten nicht gelesen werden." };
            }
        }

        public static bool HasData(JsonElement j) =>
            Has(j, "nvme_smart_health_information_log") || Arr(Get(Get(j, "ata_smart_attributes"), "table")).Any() || Has(j, "smart_status");

        public static Health Parse(JsonElement j)
        {
            var h = new Health();
            h.Protocol = Str(Get(Get(j, "device"), "protocol"));
            h.Model = Str(Get(j, "model_name"));
            if (h.Model == "") h.Model = Str(Get(j, "scsi_model_name"));
            h.Family = Str(Get(j, "model_family"));
            h.Vendor = DetectVendor(h.Family, h.Model, Str(Get(j, "scsi_vendor")));
            h.ModelNumber = ModelNumber(h.Model);
            h.Serial = Str(Get(j, "serial_number"));
            h.Firmware = Str(Get(j, "firmware_version"));
            h.Capacity = Num(Get(Get(j, "user_capacity"), "bytes")) ?? Num(Get(j, "nvme_total_capacity")) ?? 0;

            var nvme = Get(j, "nvme_smart_health_information_log");
            var table = Arr(Get(Get(j, "ata_smart_attributes"), "table")).ToList();
            h.Available = HasData(j);
            if (!h.Available)
            {
                var msgs = Arr(Get(Get(j, "smartctl"), "messages")).Select(m => Str(Get(m, "string")));
                h.Reason = FriendlyReason(msgs);
                return h;
            }
            long block = Num(Get(j, "logical_block_size")) ?? 512;
            var rr = Num(Get(j, "rotation_rate"));
            if (h.Protocol == "NVMe") h.IsSsd = true;
            else if (rr.HasValue) { h.IsSsd = rr == 0; h.Rpm = rr > 0 ? rr : null; }
            h.FormFactor = Str(Get(Get(j, "form_factor"), "name"));
            h.SataVersion = Str(Get(Get(j, "sata_version"), "string"));
            h.AtaVersion = Str(Get(Get(j, "ata_version"), "string"));
            var sp = Get(j, "interface_speed");
            h.InterfaceMax = SataLabel(Get(sp, "max"));
            h.InterfaceCurrent = SataLabel(Get(sp, "current"));
            if (h.Protocol == "NVMe") h.Interface = "NVMe (PCIe)";
            else if (h.InterfaceMax != "")
                h.Interface = h.InterfaceMax + (h.InterfaceCurrent != "" && h.InterfaceCurrent != h.InterfaceMax ? $" (aktuell {h.InterfaceCurrent})" : "");
            if (Get(j, "trim").ValueKind == JsonValueKind.Object) h.Trim = Bool(Get(Get(j, "trim"), "supported"));
            var sec = Get(j, "ata_security");
            if (sec.ValueKind == JsonValueKind.Object) { h.Security = Str(Get(sec, "string")); h.SecurityFrozen = Bool(Get(sec, "frozen")); }
            h.SmartPassed = Bool(Get(Get(j, "smart_status"), "passed"));
            h.Temperature = Num(Get(Get(j, "temperature"), "current"));
            h.PowerOnHours = Num(Get(Get(j, "power_on_time"), "hours"));
            h.PowerCycles = Num(Get(j, "power_cycle_count"));

            var attrs = new Dictionary<int, JsonElement>();
            foreach (var a in table)
            {
                var idv = Num(Get(a, "id"));
                if (!idv.HasValue) continue;
                int id = (int)idv.Value;
                attrs[id] = a;
                var raw = Get(a, "raw");
                string failed = Str(Get(a, "when_failed"));
                long thresh = Num(Get(a, "thresh")) ?? 0, value = Num(Get(a, "value")) ?? 0;
                long? rawv = Num(Get(raw, "value"));
                string status = "OK";
                if (failed == "now" || failed == "FAILING_NOW") status = "Fehler";
                else if (failed == "past" || failed == "In_the_past") status = "Warnung";
                else if ((id == 5 || id == 196 || id == 197 || id == 198) && rawv > 0) status = "Warnung";
                else if (thresh > 0 && value <= thresh) status = "Fehler";
                h.Attributes.Add(new SmartAttribute
                {
                    Id = id,
                    Name = AttributeNames.TryGetValue(id, out var n) ? n : Str(Get(a, "name")).Replace('_', ' '),
                    Value = value, Worst = Num(Get(a, "worst")) ?? 0, Thresh = thresh, Raw = rawv,
                    RawString = Get(raw, "string").ValueKind == JsonValueKind.String ? Get(raw, "string").GetString() : rawv?.ToString() ?? "",
                    Failed = failed, Status = status,
                });
            }
            long? RawOf(int id) => attrs.TryGetValue(id, out var a) ? Num(Get(Get(a, "raw"), "value")) : null;
            long? Stat(string name)
            {
                foreach (var page in Arr(Get(Get(j, "ata_device_statistics"), "pages")))
                    foreach (var e in Arr(Get(page, "table")))
                        if (Str(Get(e, "name")) == name && Num(Get(e, "value")) is long v) return v;
                return null;
            }

            if (nvme.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in nvme.EnumerateObject()) h.Nvme.Add(new KeyValuePair<string, JsonElement>(p.Name, p.Value.Clone()));
                h.Temperature ??= Num(Get(nvme, "temperature"));
                h.PowerOnHours = Num(Get(nvme, "power_on_hours")) ?? h.PowerOnHours;
                h.PowerCycles = Num(Get(nvme, "power_cycles")) ?? h.PowerCycles;
                if (Num(Get(nvme, "data_units_written")) is long w) h.BytesWritten = w * 512000;
                if (Num(Get(nvme, "data_units_read")) is long r) h.BytesRead = r * 512000;
                if (Num(Get(nvme, "percentage_used")) is long pu) h.LifePercent = Math.Max(0, 100 - pu);
                h.Uncorrectable = Num(Get(nvme, "media_errors"));
                h.SupportsSelfTests = Has(j, "nvme_self_test_log");
            }
            else
            {
                h.Pending = RawOf(197);
                h.Uncorrectable = RawOf(198);
                h.Reallocated = RawOf(5);
                h.CrcErrors = RawOf(199);
                if (Stat("Logical Sectors Written") is long w) h.BytesWritten = w * block;
                if (Stat("Logical Sectors Read") is long r) h.BytesRead = r * block;
                if (h.BytesWritten == null)
                    foreach (int id in new[] { 241, 246 })
                        if (attrs.TryGetValue(id, out var a) && BytesFromLba(a, block) is long b) { h.BytesWritten = b; break; }
                if (h.BytesRead == null && attrs.TryGetValue(242, out var a2)) h.BytesRead = BytesFromLba(a2, block);
            }

            if (h.LifePercent == null && h.IsSsd == true)
            {
                if (Num(Get(Get(j, "endurance_used"), "current_percent")) is long eu) h.LifePercent = Math.Max(0, 100 - eu);
                else if (Stat("Percentage Used Endurance Indicator") is long pu) h.LifePercent = Math.Max(0, 100 - pu);
                else
                    foreach (int id in new[] { 231, 169, 202, 233, 177 })
                        if (attrs.TryGetValue(id, out var a) && Num(Get(a, "value")) is long v && v > 0 && v <= 100) { h.LifePercent = v; break; }
            }

            var data = Get(j, "ata_smart_data");
            var st = Get(data, "self_test");
            var caps = Get(data, "capabilities");
            if (Bool(Get(caps, "self_tests_supported")) == true) h.SupportsSelfTests = true;
            h.SupportsConveyance = Bool(Get(caps, "conveyance_self_test_supported")) == true;
            h.ShortMinutes = Num(Get(Get(st, "polling_minutes"), "short"));
            h.LongMinutes = Num(Get(Get(st, "polling_minutes"), "extended"));
            var testStatus = Get(st, "status");
            if (Num(Get(testStatus, "value")) is long sv && (sv >> 4) == 0xF) h.SelfTestRemaining = Num(Get(testStatus, "remaining_percent")) ?? 100;
            foreach (var e in Arr(Get(Get(Get(j, "ata_smart_self_test_log"), "standard"), "table")).Take(20))
                h.SelfTests.Add(new SelfTestEntry
                {
                    Kind = TestKind(Str(Get(Get(e, "type"), "string"))), Result = TestResult(Str(Get(Get(e, "status"), "string"))),
                    Hours = Num(Get(e, "lifetime_hours")), Passed = Bool(Get(Get(e, "status"), "passed")),
                });
            var nlog = Get(j, "nvme_self_test_log");
            if (Num(Get(Get(nlog, "current_self_test_operation"), "value")) is long op && op != 0)
                h.SelfTestRemaining = 100 - (Num(Get(nlog, "current_self_test_completion_percent")) ?? 0);
            foreach (var e in Arr(Get(nlog, "table")).Take(20))
            {
                var res = Get(e, "self_test_result");
                h.SelfTests.Add(new SelfTestEntry
                {
                    Kind = TestKind(Str(Get(Get(e, "self_test_code"), "string"))), Result = TestResult(Str(Get(res, "string"))),
                    Hours = Num(Get(e, "power_on_hours")), Passed = Num(Get(res, "value")) == 0,
                });
            }
            Rate(h);
            return h;
        }

        static string SataLabel(JsonElement speed)
        {
            if (speed.ValueKind != JsonValueKind.Object) return "";
            if (Num(Get(speed, "units_per_second")) is long ups && Num(Get(speed, "bits_per_unit")) is long bpu)
            {
                double gbit = ups * (double)bpu / 1e9;
                if (Math.Abs(gbit - 1.5) < 0.2) return "SATA 150";
                if (Math.Abs(gbit - 3.0) < 0.2) return "SATA 300";
                if (Math.Abs(gbit - 6.0) < 0.2) return "SATA 600";
                return gbit.ToString("0.#", CultureInfo.GetCultureInfo("de-DE")) + " Gbit/s";
            }
            return Str(Get(speed, "string")) switch
            {
                "1.5 Gb/s" => "SATA 150",
                "3.0 Gb/s" => "SATA 300",
                "6.0 Gb/s" => "SATA 600",
                var s => s,
            };
        }

        static long? BytesFromLba(JsonElement a, long block)
        {
            string name = Str(Get(a, "name")).ToLowerInvariant();
            if (Num(Get(Get(a, "raw"), "value")) is not long raw) return null;
            if (name.Contains("32mib")) return raw * 32 * 1024 * 1024;
            if (name.Contains("gib")) return raw * 1024L * 1024 * 1024;
            if (name.Contains("lba")) return raw * block;
            return null;
        }

        static string TestKind(string s)
        {
            string l = s.ToLowerInvariant();
            if (l.Contains("short")) return "Kurztest";
            if (l.Contains("extended") || l.Contains("long")) return "Langtest";
            if (l.Contains("conveyance")) return "Transporttest";
            if (l.Contains("selective")) return "Selektiver Test";
            return s == "" ? "–" : s;
        }

        static string TestResult(string s)
        {
            string l = s.ToLowerInvariant();
            if (l.Contains("without error")) return "Ohne Fehler abgeschlossen";
            if (l.Contains("progress")) return "Läuft";
            if (l.Contains("aborted") || l.Contains("interrupted")) return "Abgebrochen";
            if (l.Contains("fail") || l.Contains("error")) return $"Fehlgeschlagen ({s})";
            return s == "" ? "–" : s;
        }

        public static void Rate(Health h)
        {
            var bad = new List<string>();
            var caution = new List<string>();
            var good = new List<string>();
            if (h.SmartPassed == false) bad.Add("Das Laufwerk meldet selbst einen S.M.A.R.T.-Fehler.");
            if (h.Uncorrectable > 0) bad.Add($"{h.Uncorrectable} unkorrigierbare Sektoren/Medienfehler.");
            if (h.NvmeValue("critical_warning") is long cw && cw != 0) bad.Add("Die SSD meldet eine kritische Warnung.");
            foreach (var a in h.Attributes)
                if (a.Failed == "now" || a.Failed == "FAILING_NOW" || (a.Thresh > 0 && a.Value <= a.Thresh))
                    bad.Add($"Attribut {a.Id} ({a.Name}) unter dem Grenzwert.");
            if (h.LifePercent <= 5) bad.Add($"Restlebensdauer nur noch {h.LifePercent} %.");
            if (h.Pending > 0) caution.Add($"{h.Pending} ausstehende (instabile) Sektoren.");
            if (h.Reallocated >= 50) caution.Add($"{h.Reallocated} ersetzte Sektoren.");
            if (h.LifePercent > 5 && h.LifePercent <= 20) caution.Add($"Restlebensdauer {h.LifePercent} %.");
            if (h.NvmeValue("available_spare") is long spare && h.NvmeValue("available_spare_threshold") is long th && th > 0 && spare < th)
                caution.Add("Reservespeicher der SSD aufgebraucht.");
            if (h.Reallocated > 0 && h.Reallocated < 50) good.Add($"{h.Reallocated} ersetzte Sektoren (unkritisch).");
            if (h.LifePercent > 20 && h.LifePercent < 90) good.Add($"Restlebensdauer {h.LifePercent} %.");
            if (h.Attributes.Any(a => a.Failed == "past" || a.Failed == "In_the_past"))
                good.Add("Ein Attribut lag in der Vergangenheit unter dem Grenzwert.");
            if (h.Temperature >= 60) good.Add($"Hohe Temperatur ({h.Temperature} °C).");

            if (bad.Count > 0) { h.Rating = Rating.Bad; h.Reasons = bad.Concat(caution).ToList(); }
            else if (caution.Count > 0) { h.Rating = Rating.Caution; h.Reasons = caution.Concat(good).ToList(); }
            else if (good.Count > 0) { h.Rating = Rating.Good; h.Reasons = good; }
            else { h.Rating = Rating.VeryGood; h.Reasons = new List<string> { "Keine Auffälligkeiten." }; }
        }
    }

    // --- Anzeige ---------------------------------------------------------------------------

    public static class HFmt
    {
        static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

        public static string Count(long? v) => v.HasValue ? v.Value.ToString("N0", De) : "–";

        public static string Hours(long? v)
        {
            if (!v.HasValue) return "–";
            long days = v.Value / 24;
            return days >= 2 ? $"{Count(v)} Std. (ca. {Count(days)} Tage)" : $"{Count(v)} Std.";
        }

        public static string Rpm(Health h) => h.Rpm.HasValue ? $"{Count(h.Rpm)} U/min" : h.IsSsd == true ? "– (SSD, keine Drehzahl)" : "–";

        public static string Life(Health h) => h.LifePercent.HasValue ? $"{h.LifePercent} %" : h.IsSsd == true ? "– (nicht gemeldet)" : "– (nur bei SSDs)";

        public static string Temp(long? v) => v.HasValue ? $"{v} °C" : "–";

        public static string Bytes(long b) => Fmt.Bytes(b);

        public static string BytesLong(long? b) => b.HasValue ? $"{Fmt.Bytes(b.Value)} ({Count(b)} Bytes)" : "–";

        public static List<(string, string)> KeyValues(Health h)
        {
            bool nv = h.Protocol == "NVMe";
            const string na = "– (bei NVMe nicht vorhanden)";
            return new List<(string, string)>
            {
                ("Gesamtzustand", HealthParser.RatingText(h.Rating)),
                ("Restlebensdauer", Life(h)),
                ("Temperatur", Temp(h.Temperature)),
                ("Stunden eingeschaltet", Hours(h.PowerOnHours)),
                ("Einschaltvorgänge (Power-on-Zyklen)", Count(h.PowerCycles)),
                ("Bytes geschrieben", BytesLong(h.BytesWritten)),
                ("Bytes gelesen", BytesLong(h.BytesRead)),
                ("Ausstehende Sektoren", nv && h.Pending == null ? na : Count(h.Pending)),
                ("Unkorrigierbare Sektoren" + (nv ? " (Medienfehler)" : ""), Count(h.Uncorrectable)),
                ("Ersetzte Sektoren", nv && h.Reallocated == null ? na : Count(h.Reallocated)),
                ("CRC-Fehler (Kabel/Adapter)", nv && h.CrcErrors == null ? na : Count(h.CrcErrors)),
                ("Drehzahl", Rpm(h)),
                ("Schnittstelle", h.Interface == "" ? "–" : h.Interface),
            };
        }

        public static List<(string, string)> DriveDetails(Health h)
        {
            var rows = new List<(string, string)>
            {
                ("Modellreihe", h.Family == "" ? "–" : h.Family),
                ("Modellnummer", h.Model == "" ? "–" : h.Model),
                ("Firmware", h.Firmware == "" ? "–" : h.Firmware),
                ("Kapazität", h.Capacity > 0 ? BytesLong(h.Capacity) : "–"),
                ("Bauart", h.IsSsd == true ? "SSD" : h.IsSsd == false ? "Festplatte (HDD)" : "–"),
                ("Protokoll", h.Protocol == "" ? "–" : h.Protocol),
            };
            if (h.FormFactor != "") rows.Add(("Formfaktor", h.FormFactor));
            if (h.AtaVersion != "") rows.Add(("ATA-Version", h.AtaVersion));
            if (h.SataVersion != "") rows.Add(("SATA-Version", h.SataVersion));
            if (h.Trim.HasValue) rows.Add(("TRIM", h.Trim.Value ? "unterstützt" : "nicht unterstützt"));
            if (h.Security != "")
            {
                string s = h.Security.ToUpperInvariant();
                var parts = new List<string> { s.Contains("ENABLED") && !s.Contains("DISABLED") ? "aktiviert" : "nicht aktiviert" };
                if (h.SecurityFrozen == true) parts.Add("eingefroren (frozen)");
                rows.Add(("ATA-Sicherheit", string.Join(", ", parts)));
            }
            rows.Add(("S.M.A.R.T.-Selbstbewertung", h.SmartPassed == true ? "Bestanden" : h.SmartPassed == false ? "NICHT bestanden" : "–"));
            return rows;
        }

        static readonly (string Key, string Label)[] NvmeLabels =
        {
            ("critical_warning", "Kritische Warnung"), ("available_spare", "Verfügbare Reserve (%)"),
            ("available_spare_threshold", "Reserve-Grenzwert (%)"), ("percentage_used", "Abnutzung (%)"),
            ("data_units_read", "Gelesene Dateneinheiten (je 512 000 Bytes)"),
            ("data_units_written", "Geschriebene Dateneinheiten (je 512 000 Bytes)"),
            ("host_reads", "Lesebefehle"), ("host_writes", "Schreibbefehle"), ("controller_busy_time", "Controller aktiv (Min.)"),
            ("power_cycles", "Einschaltvorgänge"), ("power_on_hours", "Betriebsstunden"),
            ("unsafe_shutdowns", "Unerwartete Abschaltungen"), ("media_errors", "Medien- und Datenfehler"),
            ("num_err_log_entries", "Einträge im Fehlerprotokoll"), ("warning_temp_time", "Zeit über Warntemperatur (Min.)"),
            ("critical_comp_time", "Zeit über kritischer Temperatur (Min.)"),
        };

        public static List<(string, string)> NvmeRows(Health h)
        {
            var rows = new List<(string, string)>();
            foreach (var (key, label) in NvmeLabels)
            {
                var v = h.NvmeValue(key);
                if (v.HasValue) rows.Add((label, Count(v)));
            }
            foreach (var kv in h.Nvme)
                if (kv.Key == "temperature_sensors" && kv.Value.ValueKind == JsonValueKind.Array)
                {
                    int i = 1;
                    foreach (var t in kv.Value.EnumerateArray())
                        if (t.TryGetInt64(out long tv)) rows.Add(($"Temperatursensor {i++}", Temp(tv)));
                }
            return rows;
        }
    }
}
