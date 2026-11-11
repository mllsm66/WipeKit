using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Media;

namespace WipeKit
{
    public enum Pattern { Random, Seeded, Zeros }

    public enum JobState { Starting, Running, Done, Failed, Cancelled }

    public sealed class WipeMode
    {
        public string Key { get; init; }
        public string Title { get; init; }
        public string Glyph { get; init; }
        public string Tagline { get; init; }
        public Pattern[] Passes { get; init; }
        public bool Verifies { get; init; }
        public bool Trims { get; init; }
        public string Explanation { get; init; }
        public string BestFor { get; init; }
        public int SecurityLevel { get; init; }
        public string SecurityNote { get; init; }
        public string MethodDescription { get; init; }
        public Color Tint { get; init; }

        public int WorkUnits => Passes.Length + (Verifies ? 1 : 0);
        public int StepCount => WorkUnits + (Trims ? 1 : 0);
        public string SecurityLabel => SecurityLevel >= 4 ? "Sehr hoch" : "Hoch";

        public static string PatternLabel(Pattern p) => p == Pattern.Zeros ? "Nullen schreiben" : "Zufallsdaten schreiben";

        public static readonly WipeMode[] All =
        {
            new WipeMode
            {
                Key = "quick", Title = "Schnell", Glyph = "\uE945", Tagline = "1 Durchgang · ohne Prüfung",
                Passes = new[] { Pattern.Random }, Verifies = false, Trims = false,
                Explanation = "Überschreibt jeden Sektor des Laufwerks einmal mit kryptografisch erzeugten Zufallsdaten. Die bisherigen Daten lassen sich danach weder mit Software noch mit Labormethoden wiederherstellen.",
                BestFor = "Festplatten (HDD), wenn es schnell gehen soll",
                SecurityLevel = 3,
                SecurityNote = "Entspricht für Festplatten dem Standard NIST SP 800-88 »Clear«. Ob wirklich jeder Sektor beschrieben wurde, wird nicht kontrolliert.",
                MethodDescription = "1 Durchgang Zufallsdaten",
                Tint = Color.FromRgb(0xF7, 0x63, 0x0C),
            },
            new WipeMode
            {
                Key = "secure", Title = "Sicher", Glyph = "\uEA18", Tagline = "2 Durchgänge · mit Prüfung",
                Passes = new[] { Pattern.Random, Pattern.Zeros }, Verifies = true, Trims = false,
                Explanation = "Überschreibt das Laufwerk zuerst mit Zufallsdaten und anschließend mit Nullen. Danach wird jeder Sektor gelesen und geprüft, ob er tatsächlich überschrieben wurde.",
                BestFor = "Festplatten mit sensiblen Daten – die empfohlene Standardwahl",
                SecurityLevel = 4,
                SecurityNote = "Die Prüfung stellt sicher, dass kein Bereich ausgelassen wurde, und erkennt defekte Sektoren sowie unzuverlässige USB-Adapter.",
                MethodDescription = "2 Durchgänge (Zufallsdaten, Nullen) + vollständige Lese-Verifikation",
                Tint = Color.FromRgb(0x10, 0x7C, 0x10),
            },
            new WipeMode
            {
                Key = "maximum", Title = "Maximal", Glyph = "\uE72E", Tagline = "3 Durchgänge · mit Prüfung",
                Passes = new[] { Pattern.Random, Pattern.Random, Pattern.Zeros }, Verifies = true, Trims = false,
                Explanation = "Überschreibt das Laufwerk zweimal mit Zufallsdaten und einmal mit Nullen. Danach wird jeder Sektor gelesen und geprüft.",
                BestFor = "Aufträge, die mehrfaches Überschreiben ausdrücklich verlangen",
                SecurityLevel = 4,
                SecurityNote = "Bei modernen Festplatten nicht messbar sicherer als »Sicher«, erfüllt aber Vorgaben mit mehreren Durchgängen (angelehnt an DoD 5220.22-M).",
                MethodDescription = "3 Durchgänge (Zufallsdaten, Zufallsdaten, Nullen) + vollständige Lese-Verifikation",
                Tint = Color.FromRgb(0x5B, 0x4F, 0xC7),
            },
            new WipeMode
            {
                Key = "ssd", Title = "SSD", Glyph = "\uEDA2", Tagline = "2 Durchgänge · Prüfung · TRIM",
                Passes = new[] { Pattern.Random, Pattern.Seeded }, Verifies = true, Trims = true,
                Explanation = "Füllt die SSD zweimal vollständig mit Zufallsdaten und prüft den zweiten Durchgang Sektor für Sektor. Zum Schluss werden alle Speicherblöcke per TRIM an den SSD-Controller freigegeben, damit er sie intern löscht.",
                BestFor = "SSDs, USB-Sticks und Speicherkarten",
                SecurityLevel = 3,
                SecurityNote = "Zweifaches Vollschreiben erreicht bei den meisten SSDs auch die versteckten Reservebereiche. Eine vollständige Garantie bietet nur das Firmware-Kommando »Secure Erase« des Herstellers.",
                MethodDescription = "2 Durchgänge Zufallsdaten + vollständige Lese-Verifikation + TRIM aller Blöcke",
                Tint = Color.FromRgb(0x00, 0x83, 0x8F),
            },
        };

        public static WipeMode ByKey(string key) => All.First(m => m.Key == key);

        public static WipeMode Recommended(DiskInfo d) => d.IsSsd == true ? ByKey("ssd") : ByKey("secure");
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

        public string Id => "Datenträger " + Number;
        public string DevicePath => @"\\.\PhysicalDrive" + Number;

        public string TypeLabel => IsSsd switch
        {
            true => "SSD",
            false => "Festplatte",
            _ => "Typ unbekannt",
        };

        public string Glyph => Bus == "USB" ? "\uE88E" : "\uEDA2";

        public string Signature => $"{Number}|{Name}|{Size}|{Bus}|{IsSsd}|{IsProtected}|{string.Join(",", Volumes)}";
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
