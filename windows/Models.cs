using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Media;

namespace WipeKit
{
    public enum Pattern { Random, Seeded, Zeros }

    public enum JobState { Starting, Running, Done, Failed, Cancelled }

    public enum VerifyKind { None, Samples, Full }

    public sealed class WipeMode
    {
        public string Key { get; init; }
        public string Title { get; init; }
        public string Glyph { get; init; }
        public string Tagline { get; init; }
        public Pattern[] Passes { get; init; }
        public VerifyKind Verify { get; init; }
        public bool Trims { get; init; }
        public bool Purge { get; init; }
        public bool ReadOnly { get; init; }
        public string Explanation { get; init; }
        public string BestFor { get; init; }
        public int SecurityLevel { get; init; }
        public string SecurityNote { get; init; }
        public string MethodDescription { get; init; }
        public string Standard { get; init; }
        public Color Tint { get; init; }

        public bool Verifies => Verify == VerifyKind.Full;
        public int WorkUnits => Passes.Length + (Verifies || ReadOnly ? 1 : 0);
        public int StepCount => Purge ? 3 : Math.Max(1, Passes.Length + (Verify != VerifyKind.None || ReadOnly ? 1 : 0) + (Trims ? 1 : 0));
        public string SecurityLabel => SecurityLevel switch { 0 => "Keine Löschung", 5 => "Höchste Stufe", 4 => "Sehr hoch", _ => "Hoch" };

        public static string PatternLabel(Pattern p) => p == Pattern.Zeros ? "Nullen schreiben" : "Zufallsdaten schreiben";

        public const string NotCertified = "WipeKit ist nicht zertifiziert und steht in keiner Verbindung zu NIST oder anderen Normungsstellen.";

        const string Clear = "NIST SP 800-88 Rev. 2 – »Clear« (Überschreiben)";

        public static readonly WipeMode[] All =
        {
            new WipeMode
            {
                Key = "quick", Title = "Schnell", Glyph = "\uE945", Tagline = "1 Durchgang · Stichproben-Prüfung",
                Passes = new[] { Pattern.Random }, Verify = VerifyKind.Samples,
                Explanation = "Überschreibt jeden Sektor des Laufwerks einmal mit kryptografisch erzeugten Zufallsdaten. Anschließend werden Anfang, Ende und rund 2.000 zufällig verteilte Stellen gelesen und mit den geschriebenen Daten verglichen.",
                BestFor = "Festplatten (HDD), wenn es schnell gehen soll",
                SecurityLevel = 3,
                SecurityNote = "Orientiert sich für Festplatten am Vorgehen »Clear« (NIST SP 800-88). Die Stichproben erkennen typische Fehler wie einen defekten USB-Adapter – eine Lücke an einer einzelnen Stelle kann aber nur die vollständige Prüfung der Modi »Sicher« und »Maximal« ausschließen.",
                MethodDescription = "1 Durchgang Zufallsdaten + Stichproben-Prüfung (Anfang, Ende, ca. 2.000 Stellen)",
                Standard = Clear,
                Tint = Color.FromRgb(0xF7, 0x63, 0x0C),
            },
            new WipeMode
            {
                Key = "secure", Title = "Sicher", Glyph = "\uEA18", Tagline = "2 Durchgänge · vollständige Prüfung",
                Passes = new[] { Pattern.Random, Pattern.Zeros }, Verify = VerifyKind.Full,
                Explanation = "Überschreibt das Laufwerk zuerst mit Zufallsdaten und anschließend mit Nullen. Danach wird jeder Sektor gelesen und geprüft, ob er tatsächlich überschrieben wurde.",
                BestFor = "Festplatten mit sensiblen Daten – die empfohlene Standardwahl",
                SecurityLevel = 4,
                SecurityNote = "Die Prüfung stellt sicher, dass kein Bereich ausgelassen wurde, und erkennt defekte Sektoren sowie unzuverlässige USB-Adapter.",
                MethodDescription = "2 Durchgänge (Zufallsdaten, Nullen) + vollständige Lese-Verifikation",
                Standard = Clear,
                Tint = Color.FromRgb(0x10, 0x7C, 0x10),
            },
            new WipeMode
            {
                Key = "maximum", Title = "Maximal", Glyph = "\uE72E", Tagline = "3 Durchgänge · vollständige Prüfung",
                Passes = new[] { Pattern.Random, Pattern.Random, Pattern.Zeros }, Verify = VerifyKind.Full,
                Explanation = "Überschreibt das Laufwerk zweimal mit Zufallsdaten und einmal mit Nullen. Danach wird jeder Sektor gelesen und geprüft.",
                BestFor = "Aufträge, die mehrfaches Überschreiben ausdrücklich verlangen",
                SecurityLevel = 4,
                SecurityNote = "Bei modernen Festplatten nicht messbar sicherer als »Sicher«, erfüllt aber Vorgaben mit mehreren Durchgängen (angelehnt an DoD 5220.22-M).",
                MethodDescription = "3 Durchgänge (Zufallsdaten, Zufallsdaten, Nullen) + vollständige Lese-Verifikation",
                Standard = Clear + ", mehrfach (angelehnt an DoD 5220.22-M)",
                Tint = Color.FromRgb(0x5B, 0x4F, 0xC7),
            },
            new WipeMode
            {
                Key = "ssd", Title = "SSD", Glyph = "\uEDA2", Tagline = "2 Durchgänge · Prüfung · TRIM",
                Passes = new[] { Pattern.Random, Pattern.Seeded }, Verify = VerifyKind.Full, Trims = true,
                Explanation = "Füllt die SSD zweimal vollständig mit Zufallsdaten und prüft den zweiten Durchgang Sektor für Sektor. Zum Schluss werden alle Speicherblöcke per TRIM an den SSD-Controller freigegeben, damit er sie intern löscht.",
                BestFor = "SSDs, USB-Sticks und Speicherkarten (auch über USB)",
                SecurityLevel = 3,
                SecurityNote = "Zweifaches Vollschreiben erreicht bei den meisten SSDs auch die versteckten Reservebereiche. Eine Garantie für diese Bereiche gibt nur die »Firmware-Löschung«.",
                MethodDescription = "2 Durchgänge Zufallsdaten + vollständige Lese-Verifikation + TRIM aller Blöcke",
                Standard = Clear + " – für SSDs nach bestem Bemühen",
                Tint = Color.FromRgb(0x00, 0x83, 0x8F),
            },
            new WipeMode
            {
                Key = "purge", Title = "Firmware-Löschung", Glyph = "\uE950", Tagline = "Löschbefehl der SSD · Kontrolle",
                Passes = new Pattern[0], Verify = VerifyKind.Samples, Purge = true,
                Explanation = "Lässt die NVMe-SSD sich selbst löschen (»Sanitize – Crypto Erase«). Dabei werden auch Reserve- und Verwaltungsbereiche gelöscht, die kein Programm von außen erreicht. Vorher werden Prüfmarken geschrieben, anschließend wird kontrolliert, dass sie verschwunden sind.",
                BestFor = "Interne NVMe-SSDs – die gründlichste Methode für SSDs",
                SecurityLevel = 5,
                SecurityNote = "Orientiert sich am Vorgehen »Purge« nach NIST SP 800-88 / IEEE 2883. Unter Windows nur für direkt angeschlossene NVMe-SSDs möglich, die den Befehl unterstützen (Windows 10 21H1 oder neuer). SATA-SSDs: unter Linux oder mit dem Werkzeug des Herstellers.",
                MethodDescription = "Firmware-Löschbefehl der SSD (Sanitize – Crypto Erase) + Kontrolle von Prüfmarken",
                Standard = "NIST SP 800-88 Rev. 2 / IEEE 2883 – »Purge« (Firmware-Löschung)",
                Tint = Color.FromRgb(0x88, 0x17, 0x98),
            },
            new WipeMode
            {
                Key = "check", Title = "Nur prüfen", Glyph = "\uE721", Tagline = "Liest alles · ändert nichts",
                Passes = new Pattern[0], Verify = VerifyKind.None, ReadOnly = true,
                Explanation = "Liest das gesamte Laufwerk und prüft, ob es leer ist (nur Nullen). Es werden keine Daten verändert. So lässt sich zum Beispiel eine Löschung mit einem anderen Programm kontrollieren. Nebenbei werden unlesbare Stellen gezählt und die Lesegeschwindigkeit aufgezeichnet.",
                BestFor = "Kontrolle eines bereits gelöschten oder neuen Laufwerks",
                SecurityLevel = 0,
                SecurityNote = "Nach »Sicher« und »Maximal« ist ein Laufwerk leer (nur Nullen). Nach »Schnell« und »SSD« enthält es Zufallsdaten und gilt deshalb hier als »nicht leer« – das ist dann kein Fehler.",
                MethodDescription = "Vollständiges Lesen aller Sektoren, keine Schreibzugriffe",
                Standard = "– (keine Löschung, nur Kontrolle)",
                Tint = Color.FromRgb(0x00, 0x67, 0xC0),
            },
        };

        public static WipeMode ByKey(string key) => All.FirstOrDefault(m => m.Key == key) ?? All.First(m => m.Key == "secure");

        public static WipeMode Recommended(DiskInfo d) => d.IsSsd == true ? ByKey("ssd") : ByKey("secure");

        /// <summary>Firmware-Löschung unter Windows nur für direkt angeschlossene NVMe-SSDs.</summary>
        public bool AvailableFor(DiskInfo d) => !Purge || d.Bus.Equals("NVMe", StringComparison.OrdinalIgnoreCase);
    }

    public sealed class DiskInfo
    {
        public int Number { get; set; }
        public string Name { get; set; } = "Unbekanntes Laufwerk";
        public long Size { get; set; }
        public string Bus { get; set; } = "?";
        public bool? IsSsd { get; set; }
        public List<string> Volumes { get; set; } = new List<string>();
        public bool IsProtected { get; set; }
        public bool IsInternal { get; set; }
        public bool IsReadOnly { get; set; }
        public string Serial { get; set; } = "";

        public string Id => "Datenträger " + Number;
        public string DevicePath => @"\\.\PhysicalDrive" + Number;

        public string TypeLabel => IsSsd switch
        {
            true => "SSD",
            false => "Festplatte",
            _ => "Typ unbekannt",
        };

        public string Glyph => Bus == "USB" ? "\uE88E" : "\uEDA2";

        public bool CanWipe => !IsProtected && !IsReadOnly;

        public string Signature => $"{Number}|{Name}|{Size}|{Bus}|{IsSsd}|{IsProtected}|{IsInternal}|{string.Join(",", Volumes)}";
    }

    public static class Fmt
    {
        static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

        public static string Bytes(double b)
        {
            b = Math.Max(0, b);
            string[] units = { "Bytes", "KB", "MB", "GB", "TB", "PB" };
            int i = 0;
            while (b >= 1000 && i < units.Length - 1) { b /= 1000; i++; }
            if (i == 0) return ((long)b).ToString(De) + " Bytes";
            return (b < 100 ? b.ToString("0.#", De) : b.ToString("0", De)) + " " + units[i];
        }

        public static string ExactBytes(long b) => b.ToString("N0", De) + " Bytes";

        public static string Speed(double s) => Bytes(s) + "/s";

        public static string Duration(TimeSpan t)
        {
            long s = (long)Math.Round(Math.Max(0, t.TotalSeconds));
            long h = s / 3600, m = (s % 3600) / 60, sec = s % 60;
            if (h > 0) return $"{h} Std. {m} Min.";
            if (m > 0) return $"{m} Min. {sec} Sek.";
            return $"{sec} Sek.";
        }

        public static string ShortDuration(TimeSpan t)
        {
            long s = (long)Math.Round(Math.Max(0, t.TotalSeconds));
            long h = s / 3600, m = (s % 3600) / 60;
            if (h > 0) return $"{h} Std. {m} Min.";
            if (m > 0) return $"{m} Min.";
            return "< 1 Min.";
        }

        public static string Clock(DateTime d)
        {
            string prefix;
            if (d.Date == DateTime.Today) prefix = "heute";
            else if (d.Date == DateTime.Today.AddDays(1)) prefix = "morgen";
            else prefix = De.DateTimeFormat.GetDayName(d.DayOfWeek);
            return $"{prefix}, {d.ToString("HH:mm", De)} Uhr";
        }

        public static string DateTimeText(DateTime d) => d.ToString("dd.MM.yyyy, HH:mm:ss", De) + " Uhr";

        public static string Percent(double fraction)
        {
            double p = Math.Max(0, Math.Min(1, fraction)) * 100;
            if (p >= 10 || p == 0) return ((int)p).ToString(De) + " %";
            return p.ToString("0.0", De) + " %";
        }
    }

    public sealed class WipeException : Exception
    {
        public WipeException(string message) : base(message) { }
    }
}
