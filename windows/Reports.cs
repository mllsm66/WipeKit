using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace WipeKit
{
    // WipeKit-Berichte: Gesundheitsbericht, Löschprotokoll (kurz/ausführlich), Prüfprotokoll – als PDF und Text.
    // Gleicher Aufbau wie in der Linux- und Mac-Version.
    // PDF: eigener, minimaler Schreiber mit der Standardschrift Helvetica (nicht eingebettet).
    // Zeichenbreiten aus den Adobe Core-14-AFM-Dateien:
    // Copyright (c) 1985, 1987, 1989, 1990, 1997 Adobe Systems Incorporated. All Rights Reserved.

    public sealed class ReportTable
    {
        public string[] Headers;
        public List<string[]> Rows;
        public double[] Widths;
        public char[] Align;
    }

    public sealed class ReportBlock
    {
        public List<(string, string)> Kv;
        public ReportTable Table;
        public string Text;
        public List<string> Bullets;
    }

    public sealed class ReportSection
    {
        public string Title;
        public List<ReportBlock> Blocks = new List<ReportBlock>();
        public ReportSection(string title, params ReportBlock[] blocks) { Title = title; Blocks.AddRange(blocks); }
    }

    public sealed class ReportIdentity
    {
        public string Vendor = "", Model = "", Serial = "";
        public long Size;
    }

    public sealed class ReportKind
    {
        public string Title, Suffix;
        public static readonly ReportKind HealthReport = new ReportKind { Title = "Gesundheitsbericht", Suffix = "Gesundheitsbericht" };
        public static readonly ReportKind Wipe = new ReportKind { Title = "Löschprotokoll", Suffix = "Loeschprotokoll" };
        public static readonly ReportKind WipeFull = new ReportKind { Title = "Löschprotokoll (ausführlich)", Suffix = "Loeschprotokoll-ausfuehrlich" };
        public static readonly ReportKind Check = new ReportKind { Title = "Prüfprotokoll", Suffix = "Pruefprotokoll" };
    }

    public sealed class Report
    {
        public ReportKind Kind;
        public DateTime Created;
        public ReportIdentity Identity;
        public Rating Rating;
        public string RatingNote = "";
        public (string Text, (double, double, double) Color)? Banner;
        public List<ReportSection> Sections = new List<ReportSection>();
        public string Footer = "";
    }

    public sealed class WipeRecord
    {
        public string ModeTitle, Method, Standard;
        public JobState State;
        public DateTime Started, Ended;
        public long BytesWritten;
        public string Message = "", Verification = "", Trim = "", Device = "", Bus = "", TypeLabel = "";
        public List<string> Volumes = new List<string>();
        public long ExactSize;
        public string HiddenArea = "", Host = "", Platform = "", AppVersion = "";
        public bool ReadOnly;
        public int? BadBlocks;
        public long? NonZero;
        public string PurgeMethod = "";
    }

    public static class Reports
    {
        static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");
        public static readonly (double, double, double) Green = (0.12, 0.56, 0.24);
        public static readonly (double, double, double) Orange = (0.86, 0.55, 0.00);
        public static readonly (double, double, double) Red = (0.85, 0.19, 0.15);
        const string UnavailableHelp = "Hinweis: Viele USB-Gehäuse und -Adapter geben die Gesundheitsdaten nicht weiter. Abhilfe: das Laufwerk direkt (SATA/NVMe intern), über Thunderbolt/USB4 oder über einen Adapter mit ASMedia-, JMicron- oder Realtek-Chip anschließen.";

        static ReportBlock Kv(List<(string, string)> rows) => new ReportBlock { Kv = rows };
        static ReportBlock Txt(string t) => new ReportBlock { Text = t };
        static ReportBlock Bul(List<string> b) => new ReportBlock { Bullets = b };
        static ReportBlock Tab(ReportTable t) => new ReportBlock { Table = t };

        // --- Identität und Dateinamen ----------------------------------------------------------

        public static ReportIdentity Identity(Health h, string model, long size, string serial)
        {
            if (h != null && h.Model != "")
            {
                string v = h.Vendor != "" ? h.Vendor : HealthParser.DetectVendor(model);
                return new ReportIdentity
                {
                    Vendor = v == "" ? "Unbekannt" : v,
                    Model = h.ModelNumber != "" ? h.ModelNumber : h.Model,
                    Size = h.Capacity > 0 ? h.Capacity : size,
                    Serial = h.Serial != "" ? h.Serial : serial ?? "",
                };
            }
            string m = (model ?? "").Trim();
            string vendor = HealthParser.DetectVendor(m);
            string mn = HealthParser.ModelNumber(m);
            return new ReportIdentity
            {
                Vendor = vendor == "" ? "Unbekannt" : vendor,
                Model = mn != "" ? mn : (m != "" ? m : "Unbekannt"),
                Size = size,
                Serial = serial ?? "",
            };
        }

        public static string Slug(string s)
        {
            s = (s ?? "").Replace("ß", "SS").Replace("ä", "AE").Replace("ö", "OE").Replace("ü", "UE")
                .Replace("Ä", "AE").Replace("Ö", "OE").Replace("Ü", "UE");
            var norm = s.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder();
            bool dash = false;
            foreach (char c in norm)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
                if (c < 128 && char.IsLetterOrDigit(c)) { sb.Append(char.ToUpperInvariant(c)); dash = false; }
                else if (!dash && sb.Length > 0) { sb.Append('-'); dash = true; }
            }
            return sb.ToString().TrimEnd('-');
        }

        public static string BaseName(ReportIdentity i, DateTime when)
        {
            var parts = new[]
            {
                Slug(i.Vendor == "Unbekannt" ? "" : i.Vendor), Slug(i.Model),
                i.Size > 0 ? Math.Round(i.Size / 1e9).ToString("0", CultureInfo.InvariantCulture) + "GB" : "", Slug(i.Serial),
            };
            string name = string.Join("-", parts.Where(p => p != ""));
            if (name == "") name = "LAUFWERK";
            if (name.Length > 150) name = name.Substring(0, 150);
            return when.ToString("yyyy-MM-dd_HH-mm", CultureInfo.InvariantCulture) + "_" + name;
        }

        public static string Folder
        {
            get
            {
                string env = Environment.GetEnvironmentVariable("WIPEKIT_REPORTS");
                if (!string.IsNullOrEmpty(env)) return env;
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "WipeKit-Protokolle");
            }
        }

        /// <summary>Speichert PDF und Text. Gibt (PDF, Text) zurück.</summary>
        public static (string Pdf, string Txt) Save(Report r)
        {
            string pdfDir = Path.Combine(Folder, "PDF"), txtDir = Path.Combine(Folder, "Text");
            Directory.CreateDirectory(pdfDir);
            Directory.CreateDirectory(txtDir);
            string name = BaseName(r.Identity, r.Created) + "_" + r.Kind.Suffix;
            string pdf = Unique(Path.Combine(pdfDir, name + ".pdf"));
            string txt = Unique(Path.Combine(txtDir, name + ".txt"));
            File.WriteAllBytes(pdf, RenderPdf(r));
            File.WriteAllText(txt, RenderText(r).Replace("\n", "\r\n"), new UTF8Encoding(true));
            return (pdf, txt);
        }

        static string Unique(string p)
        {
            if (!File.Exists(p)) return p;
            string dir = Path.GetDirectoryName(p), stem = Path.GetFileNameWithoutExtension(p), ext = Path.GetExtension(p);
            for (int i = 2; i < 100; i++)
            {
                string c = Path.Combine(dir, $"{stem}_{i}{ext}");
                if (!File.Exists(c)) return c;
            }
            return p;
        }

        // --- Inhalte -------------------------------------------------------------------------------

        public static string Dt(DateTime d, bool seconds = false) =>
            d.ToString(seconds ? "dd.MM.yyyy, HH:mm:ss" : "dd.MM.yyyy, HH:mm", De) + " Uhr";

        public static List<ReportSection> HealthSections(Health h, string suffix = "", bool tables = true)
        {
            if (h == null || !h.Available)
            {
                string reason = h != null && h.Reason != "" ? h.Reason : "Keine S.M.A.R.T.-Daten verfügbar.";
                return new List<ReportSection> { new ReportSection("Gesundheit" + suffix, Txt(reason), Txt(UnavailableHelp)) };
            }
            var secs = new List<ReportSection>
            {
                new ReportSection("Kennwerte" + suffix, Kv(HFmt.KeyValues(h))),
                new ReportSection("Bewertung" + suffix, Bul(h.Reasons)),
                new ReportSection("Laufwerk" + suffix, Kv(HFmt.DriveDetails(h))),
            };
            if (!tables) return secs;
            if (h.Attributes.Count > 0)
                secs.Add(new ReportSection("S.M.A.R.T.-Attribute" + suffix, Tab(new ReportTable
                {
                    Headers = new[] { "ID", "Attribut", "Wert", "Schlechtester", "Grenzwert", "Rohwert", "Status" },
                    Rows = h.Attributes.Select(a => new[] { a.Id.ToString(), a.Name, a.Value.ToString(), a.Worst.ToString(), a.Thresh.ToString(), a.RawString, a.Status }).ToList(),
                    Widths = new[] { 0.06, 0.36, 0.08, 0.13, 0.11, 0.16, 0.10 },
                    Align = new[] { 'r', 'l', 'r', 'r', 'r', 'r', 'l' },
                })));
            if (h.Nvme.Count > 0)
                secs.Add(new ReportSection("NVMe-Gesundheitswerte" + suffix, Kv(HFmt.NvmeRows(h))));
            if (h.SelfTests.Count > 0)
                secs.Add(new ReportSection("Selbsttest-Protokoll des Laufwerks" + suffix, Tab(new ReportTable
                {
                    Headers = new[] { "Test", "Ergebnis", "Bei Betriebsstunde" },
                    Rows = h.SelfTests.Select(t => new[] { t.Kind, t.Result, t.Hours.HasValue ? HFmt.Count(t.Hours) : "–" }).ToList(),
                    Widths = new[] { 0.25, 0.53, 0.22 },
                    Align = new[] { 'l', 'l', 'r' },
                })));
            return secs;
        }

        static string Note(Health h) => h == null ? "" : h.Available ? h.Reasons.FirstOrDefault() ?? "" : h.Reason;

        public static Report HealthReport(Health h, ReportIdentity ident, string appVersion, string platform, string host)
        {
            var r = new Report
            {
                Kind = ReportKind.HealthReport, Created = DateTime.Now, Identity = ident,
                Rating = h?.Rating ?? Rating.Unknown, RatingNote = Note(h),
            };
            r.Sections = HealthSections(h);
            r.Sections.Add(new ReportSection("Bericht", Kv(new List<(string, string)>
            {
                ("Erstellt", Dt(r.Created, true)), ("Rechner", host == "" ? "–" : host),
                ("System", platform == "" ? "–" : platform), ("Programm", "WipeKit " + appVersion),
            })));
            r.Footer = "WipeKit " + appVersion;
            return r;
        }

        public static (string, (double, double, double)) Result(WipeRecord w)
        {
            if (w.ReadOnly)
            {
                if (w.State == JobState.Done)
                {
                    if (w.BadBlocks > 0) return ("Unlesbare Bereiche gefunden", Red);
                    if (w.NonZero == 0) return ("Laufwerk ist leer (nur Nullen)", Green);
                    return ("Laufwerk enthält Daten", Orange);
                }
                return w.State == JobState.Cancelled ? ("Prüfung abgebrochen", Orange) : ("Prüfung fehlgeschlagen", Red);
            }
            if (w.State == JobState.Done) return ("Erfolgreich gelöscht", Green);
            if (w.State == JobState.Cancelled) return ("Abgebrochen – das Laufwerk wurde nur teilweise überschrieben", Orange);
            return ("Fehlgeschlagen", Red);
        }

        public static Report WipeReport(WipeRecord w, ReportIdentity ident, Health before, Health after, bool detailed)
        {
            var h = after != null && after.Available ? after : before;
            var (res, color) = Result(w);
            var r = new Report
            {
                Kind = w.ReadOnly ? ReportKind.Check : detailed ? ReportKind.WipeFull : ReportKind.Wipe,
                Created = w.Ended, Identity = ident, Rating = h?.Rating ?? Rating.Unknown, RatingNote = Note(h),
                Banner = (res, color),
            };
            double duration = Math.Max(0, (w.Ended - w.Started).TotalSeconds);
            double avg = duration > 0 ? w.BytesWritten / duration : 0;
            var rows = new List<(string, string)> { ("Ergebnis", res), ("Methode", w.ModeTitle), ("Ablauf", w.Method), ("Einordnung", w.Standard) };
            if (w.PurgeMethod != "") rows.Add(("Firmware-Befehl", w.PurgeMethod));
            rows.Add(("Beginn", Dt(w.Started, true)));
            rows.Add(("Ende", Dt(w.Ended, true)));
            rows.Add(("Dauer", Fmt.Duration(TimeSpan.FromSeconds(duration))));
            rows.Add(("Ø Geschwindigkeit", avg > 0 && w.PurgeMethod == "" ? Fmt.Speed(avg) : "–"));
            if (w.ReadOnly)
            {
                if (w.NonZero.HasValue)
                    rows.Add(("Inhalt", w.NonZero == 0 ? "leer – alle Sektoren enthalten Nullen" : $"{HFmt.BytesLong(w.NonZero)} enthalten Daten"));
                if (w.BadBlocks.HasValue) rows.Add(("Unlesbare Bereiche", w.BadBlocks == 0 ? "keine" : $"{w.BadBlocks} (je 64 KiB)"));
            }
            else rows.Add(("Verifikation", w.Verification == "" ? "–" : w.Verification));
            if (w.Trim != "") rows.Add(("TRIM", w.Trim));
            if (w.HiddenArea != "") rows.Add(("Versteckte Bereiche", w.HiddenArea));
            if (w.Message != "") rows.Add(("Meldung", w.Message));
            r.Sections.Add(new ReportSection(w.ReadOnly ? "Prüfung" : "Löschvorgang", Kv(rows)));
            r.Sections.Add(new ReportSection("Laufwerk", Kv(new List<(string, string)>
            {
                ("Kennung", w.Device == "" ? "–" : w.Device), ("Anschluss", w.Bus == "" ? "–" : w.Bus),
                ("Typ", w.TypeLabel == "" ? "–" : w.TypeLabel), ("Kapazität", HFmt.BytesLong(w.ExactSize > 0 ? w.ExactSize : ident.Size)),
                ("Frühere Volumes", w.Volumes.Count == 0 ? "–" : string.Join(", ", w.Volumes)),
            })));
            if (detailed || w.ReadOnly)
            {
                if (!w.ReadOnly && before != null && after != null && before.Available && after.Available)
                {
                    var kb = HFmt.KeyValues(before);
                    var ka = HFmt.KeyValues(after);
                    r.Sections.Add(new ReportSection("Kennwerte vor und nach dem Löschen", Tab(new ReportTable
                    {
                        Headers = new[] { "Wert", "Vor dem Löschen", "Nach dem Löschen" },
                        Rows = kb.Zip(ka, (b, a) => new[] { b.Item1, b.Item2, a.Item2 }).ToList(),
                        Widths = new[] { 0.36, 0.32, 0.32 },
                        Align = new[] { 'l', 'l', 'l' },
                    })));
                    r.Sections.Add(new ReportSection("Bewertung", Bul(after.Reasons)));
                    r.Sections.Add(new ReportSection("Laufwerksdaten", Kv(HFmt.DriveDetails(after))));
                    r.Sections.AddRange(HealthSections(after, " (nach dem Löschen)").Skip(3));
                }
                else
                {
                    foreach (var s in HealthSections(h))
                    {
                        if (s.Title == "Laufwerk") s.Title = "Laufwerksdaten";
                        r.Sections.Add(s);
                    }
                }
            }
            r.Sections.Add(new ReportSection("Protokoll", Kv(new List<(string, string)>
            {
                ("Erstellt", Dt(r.Created, true)), ("Rechner", w.Host == "" ? "–" : w.Host),
                ("System", w.Platform == "" ? "–" : w.Platform), ("Programm", "WipeKit " + w.AppVersion),
            })));
            r.Footer = "WipeKit " + w.AppVersion;
            return r;
        }

        // --- Text ------------------------------------------------------------------------------------

        const int TxtW = 78;

        static List<string> Wrap(string text, int width)
        {
            var lines = new List<string>();
            string cur = "";
            foreach (var w0 in (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                string w = w0;
                while (w.Length > width)
                {
                    if (cur != "") { lines.Add(cur); cur = ""; }
                    lines.Add(w.Substring(0, width));
                    w = w.Substring(width);
                }
                if (cur == "") cur = w;
                else if (cur.Length + 1 + w.Length <= width) cur += " " + w;
                else { lines.Add(cur); cur = w; }
            }
            if (cur != "" || lines.Count == 0) lines.Add(cur);
            return lines;
        }

        static IEnumerable<string> KvText(List<(string, string)> rows, int? labelW = null)
        {
            int lw = labelW ?? Math.Min(38, rows.Max(r => r.Item1.Length) + 2);
            foreach (var (k, v) in rows)
            {
                var vl = Wrap(v, TxtW - lw);
                yield return (k + ":").PadRight(lw) + vl[0];
                foreach (var x in vl.Skip(1)) yield return new string(' ', lw) + x;
            }
        }

        public static string RenderText(Report r)
        {
            string line = new string('-', TxtW);
            var L = new List<string> { "WIPEKIT-BERICHT", r.Kind.Title, "Erstellt am " + Dt(r.Created), new string('=', TxtW), "" };
            if (r.Banner.HasValue) { L.Add("ERGEBNIS: " + r.Banner.Value.Text.ToUpper(De)); L.Add(""); }
            var i = r.Identity;
            L.Add("ÜBERSICHT");
            L.Add(line);
            L.AddRange(KvText(new List<(string, string)>
            {
                ("Hersteller", i.Vendor == "" ? "Unbekannt" : i.Vendor), ("Modell", i.Model == "" ? "Unbekannt" : i.Model),
                ("Größe", i.Size > 0 ? Fmt.Bytes(i.Size) : "–"), ("Seriennummer", i.Serial == "" ? "–" : i.Serial),
                ("Gesamtzustand", HealthParser.RatingText(r.Rating).ToUpper(De) + (r.RatingNote == "" ? "" : " – " + r.RatingNote)),
            }, 18));
            L.Add("");
            foreach (var s in r.Sections)
            {
                L.Add(s.Title.ToUpper(De));
                L.Add(line);
                foreach (var b in s.Blocks)
                {
                    if (b.Kv != null) L.AddRange(KvText(b.Kv));
                    else if (b.Text != null) L.AddRange(Wrap(b.Text, TxtW));
                    else if (b.Bullets != null)
                        foreach (var it in b.Bullets)
                        {
                            var wl = Wrap(it, TxtW - 2);
                            L.Add("- " + wl[0]);
                            L.AddRange(wl.Skip(1).Select(x => "  " + x));
                        }
                    else if (b.Table != null)
                    {
                        var t = b.Table;
                        var widths = Enumerable.Range(0, t.Headers.Length)
                            .Select(ci => Math.Min(60, new[] { t.Headers }.Concat(t.Rows).Max(row => row[ci].Length))).ToArray();
                        string FmtRow(string[] cells)
                        {
                            var o = new List<string>();
                            for (int ci = 0; ci < cells.Length; ci++)
                            {
                                string c = cells[ci];
                                if (c.Length > widths[ci]) c = c.Substring(0, widths[ci] - 1) + "…";
                                o.Add(t.Align[ci] == 'r' ? c.PadLeft(widths[ci]) : c.PadRight(widths[ci]));
                            }
                            return string.Join("  ", o).TrimEnd();
                        }
                        L.Add(FmtRow(t.Headers));
                        L.Add(string.Join("  ", widths.Select(w => new string('-', w))));
                        L.AddRange(t.Rows.Select(FmtRow));
                    }
                    L.Add("");
                }
            }
            L.Add(line);
            L.Add("Erstellt mit " + (r.Footer == "" ? "WipeKit" : r.Footer));
            return string.Join("\n", L) + "\n";
        }

        // --- PDF -----------------------------------------------------------------------------------------

        public static byte[] Logo
        {
            get
            {
                try
                {
                    using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("WipeKit.report-logo.jpg");
                    if (s == null) return null;
                    using var ms = new MemoryStream();
                    s.CopyTo(ms);
                    return ms.ToArray();
                }
                catch { return null; }
            }
        }

        public static byte[] RenderPdf(Report r) => new PdfLayout(r, Logo).Render();
    }

    // --- Minimaler PDF-Schreiber -------------------------------------------------------------------------

    internal static class PdfMetrics
    {
    static readonly int[] Helvetica = {
        278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278, 556, 556, 556, 556, 556, 556,
        556, 556, 556, 556, 278, 278, 584, 584, 584, 556, 1015, 667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667,
        556, 833, 722, 778, 667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 278, 278, 278, 469, 556, 333, 556,
        556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556, 556, 556, 333, 500, 278, 556, 500, 722,
        500, 500, 500, 334, 260, 334, 584, 761, 556, 0, 222, 556, 333, 1000, 556, 556, 333, 1000, 667, 333, 1000, 0,
        611, 0, 0, 222, 222, 333, 333, 350, 556, 1000, 333, 1000, 500, 333, 944, 0, 500, 667, 278, 333, 556, 556, 556,
        556, 260, 556, 333, 737, 370, 556, 584, 333, 737, 333, 400, 584, 333, 333, 333, 556, 537, 278, 333, 333, 365,
        556, 834, 834, 834, 611, 667, 667, 667, 667, 667, 667, 1000, 722, 667, 667, 667, 667, 278, 278, 278, 278, 722,
        722, 778, 778, 778, 778, 778, 584, 778, 722, 722, 722, 722, 667, 667, 611, 556, 556, 556, 556, 556, 556, 889,
        500, 556, 556, 556, 556, 278, 278, 278, 278, 556, 556, 556, 556, 556, 556, 556, 584, 611, 556, 556, 556, 556,
        500, 556, 500
    };
    static readonly int[] HelveticaBold = {
        278, 333, 474, 556, 556, 889, 722, 238, 333, 333, 389, 584, 278, 333, 278, 278, 556, 556, 556, 556, 556, 556,
        556, 556, 556, 556, 333, 333, 584, 584, 584, 611, 975, 722, 722, 722, 722, 667, 611, 778, 722, 278, 556, 722,
        611, 833, 722, 778, 667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 333, 278, 333, 584, 556, 333, 556,
        611, 556, 611, 556, 333, 611, 611, 278, 278, 556, 278, 889, 611, 611, 611, 611, 389, 556, 333, 611, 556, 778,
        556, 556, 500, 389, 280, 389, 584, 761, 556, 0, 278, 556, 500, 1000, 556, 556, 333, 1000, 667, 333, 1000, 0,
        611, 0, 0, 278, 278, 500, 500, 350, 556, 1000, 333, 1000, 556, 333, 944, 0, 500, 667, 278, 333, 556, 556, 556,
        556, 280, 556, 333, 737, 370, 556, 584, 333, 737, 333, 400, 584, 333, 333, 333, 611, 556, 278, 333, 333, 365,
        556, 834, 834, 834, 611, 722, 722, 722, 722, 722, 722, 1000, 722, 667, 667, 667, 667, 278, 278, 278, 278, 722,
        722, 778, 778, 778, 778, 778, 584, 778, 722, 722, 722, 722, 667, 667, 611, 556, 556, 556, 556, 556, 556, 889,
        556, 556, 556, 556, 556, 278, 278, 278, 278, 611, 611, 611, 611, 611, 611, 611, 584, 611, 611, 611, 611, 611,
        556, 611, 556
    };
        static readonly Encoding Ansi = CodePagesEncodingProvider.Instance.GetEncoding(1252,
            new EncoderReplacementFallback("?"), DecoderFallback.ReplacementFallback);

        static readonly Dictionary<char, string> Repl = new Dictionary<char, string>
        {
            ['≈'] = "ca.", ['→'] = "->", ['≥'] = ">=", ['≤'] = "<=", ['✓'] = "OK", ['✗'] = "X",
            [' '] = " ", [' '] = " ", [' '] = " ", ['−'] = "-", ['×'] = "x",
        };

        public static byte[] ToAnsi(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s ?? "") sb.Append(Repl.TryGetValue(c, out var r) ? r : c.ToString());
            return Ansi.GetBytes(sb.ToString());
        }

        public static double Width(string s, double size, bool bold)
        {
            var t = bold ? HelveticaBold : Helvetica;
            int w = 0;
            foreach (byte b in ToAnsi(s)) if (b >= 32) w += t[b - 32];
            return w * size / 1000.0;
        }
    }

    internal sealed class PdfCanvas
    {
        public const double W = 595.28, H = 841.89;
        public readonly List<MemoryStream> Pages = new List<MemoryStream>();
        public MemoryStream Cur;
        public byte[] Image;

        public void NewPage() { Cur = new MemoryStream(); Pages.Add(Cur); }

        public static string N(double v)
        {
            string s = v.ToString("0.##", CultureInfo.InvariantCulture);
            return s == "-0" ? "0" : s;
        }

        static string Rgb((double, double, double) c) => $"{N(c.Item1)} {N(c.Item2)} {N(c.Item3)}";

        void Op(string s) { var b = Encoding.ASCII.GetBytes(s + "\n"); Cur.Write(b, 0, b.Length); }

        public void Text(double x, double y, string s, double size, bool bold, (double, double, double) color)
        {
            Op($"BT /{(bold ? "F2" : "F1")} {N(size)} Tf {Rgb(color)} rg {N(x)} {N(H - y)} Td");
            var bytes = new List<byte> { (byte)'(' };
            foreach (byte b in PdfMetrics.ToAnsi(s))
            {
                if (b == '(' || b == ')' || b == '\\') bytes.Add((byte)'\\');
                bytes.Add(b);
            }
            bytes.AddRange(Encoding.ASCII.GetBytes(") Tj ET\n"));
            Cur.Write(bytes.ToArray(), 0, bytes.Count);
        }

        public void TextRight(double xr, double y, string s, double size, bool bold, (double, double, double) color) =>
            Text(xr - PdfMetrics.Width(s, size, bold), y, s, size, bold, color);

        public void Rect(double x, double y, double w, double h, (double, double, double)? fill = null,
            (double, double, double)? stroke = null, double radius = 0, double lineWidth = 0.5)
        {
            double x0 = x, y0 = H - y - h, r = Math.Min(radius, Math.Min(w / 2, h / 2));
            string p;
            if (r > 0)
            {
                double k = 0.5523 * r;
                p = $"{N(x0 + r)} {N(y0)} m {N(x0 + w - r)} {N(y0)} l " +
                    $"{N(x0 + w - r + k)} {N(y0)} {N(x0 + w)} {N(y0 + r - k)} {N(x0 + w)} {N(y0 + r)} c " +
                    $"{N(x0 + w)} {N(y0 + h - r)} l " +
                    $"{N(x0 + w)} {N(y0 + h - r + k)} {N(x0 + w - r + k)} {N(y0 + h)} {N(x0 + w - r)} {N(y0 + h)} c " +
                    $"{N(x0 + r)} {N(y0 + h)} l " +
                    $"{N(x0 + r - k)} {N(y0 + h)} {N(x0)} {N(y0 + h - r + k)} {N(x0)} {N(y0 + h - r)} c " +
                    $"{N(x0)} {N(y0 + r)} l " +
                    $"{N(x0)} {N(y0 + r - k)} {N(x0 + r - k)} {N(y0)} {N(x0 + r)} {N(y0)} c h";
            }
            else p = $"{N(x0)} {N(y0)} {N(w)} {N(h)} re";
            var ops = new List<string>();
            if (fill.HasValue) ops.Add(Rgb(fill.Value) + " rg");
            if (stroke.HasValue) ops.Add($"{Rgb(stroke.Value)} RG {N(lineWidth)} w");
            string paint = fill.HasValue && stroke.HasValue ? "B" : fill.HasValue ? "f" : "S";
            Op(string.Join(" ", ops) + " " + p + " " + paint);
        }

        public void Line(double x1, double y1, double x2, double y2, (double, double, double) color, double width) =>
            Op($"{Rgb(color)} RG {N(width)} w {N(x1)} {N(H - y1)} m {N(x2)} {N(H - y2)} l S");

        public void Jpeg(byte[] data, double x, double y, double w, double h)
        {
            Image = data;
            Op($"q {N(w)} 0 0 {N(h)} {N(x)} {N(H - y - h)} cm /Im1 Do Q");
        }

        static (int, int) JpegSize(byte[] d)
        {
            int i = 2;
            while (i < d.Length - 9)
            {
                if (d[i] != 0xFF) { i++; continue; }
                byte marker = d[i + 1];
                int len = (d[i + 2] << 8) | d[i + 3];
                if (marker == 0xC0 || marker == 0xC1 || marker == 0xC2)
                    return ((d[i + 7] << 8) | d[i + 8], (d[i + 5] << 8) | d[i + 6]);
                i += 2 + len;
            }
            return (0, 0);
        }

        static string U16(string s)
        {
            var sb = new StringBuilder("<FEFF");
            foreach (char c in s) sb.Append(((int)c).ToString("X4"));
            return sb.Append('>').ToString();
        }

        public byte[] Build(string title, string subject)
        {
            var objs = new Dictionary<int, byte[]>();
            var pageIds = Enumerable.Range(0, Pages.Count).Select(i => 7 + 2 * i).ToList();
            byte[] A(string s) => Encoding.ASCII.GetBytes(s);
            objs[1] = A("<< /Type /Catalog /Pages 2 0 R /Lang (de-DE) /ViewerPreferences << /DisplayDocTitle true >> >>");
            objs[2] = A($"<< /Type /Pages /Count {Pages.Count} /Kids [{string.Join(" ", pageIds.Select(i => $"{i} 0 R"))}] >>");
            objs[3] = A("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
            objs[4] = A("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");
            string res = "<< /Font << /F1 3 0 R /F2 4 0 R >>";
            if (Image != null)
            {
                var (iw, ih) = JpegSize(Image);
                var ms = new MemoryStream();
                var head = A($"<< /Type /XObject /Subtype /Image /Width {iw} /Height {ih} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {Image.Length} >>\nstream\n");
                ms.Write(head, 0, head.Length);
                ms.Write(Image, 0, Image.Length);
                var tail = A("\nendstream");
                ms.Write(tail, 0, tail.Length);
                objs[5] = ms.ToArray();
                res += " /XObject << /Im1 5 0 R >>";
            }
            res += " >>";
            objs[6] = A($"<< /Title {U16(title)} /Subject {U16(subject)} /Creator (WipeKit) /Producer (WipeKit) /CreationDate (D:{DateTime.Now:yyyyMMddHHmmss}) >>");
            for (int i = 0; i < Pages.Count; i++)
            {
                int pid = pageIds[i];
                objs[pid] = A($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {N(W)} {N(H)}] /Contents {pid + 1} 0 R /Resources {res} >>");
                var content = Pages[i].ToArray();
                var ms = new MemoryStream();
                var head = A($"<< /Length {content.Length} >>\nstream\n");
                ms.Write(head, 0, head.Length);
                ms.Write(content, 0, content.Length);
                var tail = A("\nendstream");
                ms.Write(tail, 0, tail.Length);
                objs[pid + 1] = ms.ToArray();
            }
            var outp = new MemoryStream();
            void W(byte[] b) => outp.Write(b, 0, b.Length);
            W(A("%PDF-1.4\n"));
            W(new byte[] { 0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A });
            int maxId = objs.Keys.Max();
            var offsets = new long[maxId + 1];
            for (int oid = 1; oid <= maxId; oid++)
            {
                offsets[oid] = outp.Length;
                W(A($"{oid} 0 obj\n"));
                W(objs.TryGetValue(oid, out var b) ? b : A("null"));
                W(A("\nendobj\n"));
            }
            long xref = outp.Length;
            var sb = new StringBuilder($"xref\n0 {maxId + 1}\n0000000000 65535 f \n");
            for (int oid = 1; oid <= maxId; oid++) sb.Append(offsets[oid].ToString("D10")).Append(" 00000 n \n");
            sb.Append($"trailer\n<< /Size {maxId + 1} /Root 1 0 R /Info 6 0 R >>\nstartxref\n{xref}\n%%EOF\n");
            W(A(sb.ToString()));
            return outp.ToArray();
        }
    }

    internal sealed class PdfLayout
    {
        static readonly (double, double, double) Ink = (0.11, 0.11, 0.12), Secondary = (0.43, 0.43, 0.45),
            Separator = (0.84, 0.84, 0.86), CardBg = (0.96, 0.96, 0.97), White = (1, 1, 1);
        const double M = 48, Foot = 40;
        readonly Report _r;
        readonly byte[] _logo;
        readonly PdfCanvas _c = new PdfCanvas();
        double _y;
        double Cw => PdfCanvas.W - 2 * M;
        double Bottom => PdfCanvas.H - Foot - 14;

        public PdfLayout(Report r, byte[] logo) { _r = r; _logo = logo; }

        static (double, double, double) Tint((double, double, double) c, double a) =>
            (1 - a * (1 - c.Item1), 1 - a * (1 - c.Item2), 1 - a * (1 - c.Item3));

        static List<string> WrapW(string text, double size, bool bold, double width)
        {
            var lines = new List<string>();
            string cur = "";
            foreach (var w in (text ?? "").Split(' '))
            {
                string cand = cur == "" ? w : cur + " " + w;
                if (PdfMetrics.Width(cand, size, bold) <= width) { cur = cand; continue; }
                if (cur != "") lines.Add(cur);
                cur = w;
                while (PdfMetrics.Width(cur, size, bold) > width && cur.Length > 1)
                {
                    int k = cur.Length;
                    while (k > 1 && PdfMetrics.Width(cur.Substring(0, k), size, bold) > width) k--;
                    lines.Add(cur.Substring(0, k));
                    cur = cur.Substring(k);
                }
            }
            lines.Add(cur);
            return lines;
        }

        static string Fit(string text, double size, bool bold, double width)
        {
            if (PdfMetrics.Width(text, size, bold) <= width) return text;
            while (text.Length > 0 && PdfMetrics.Width(text + "…", size, bold) > width) text = text.Substring(0, text.Length - 1);
            return text + "…";
        }

        void NewPage() { _c.NewPage(); _y = M; }
        void Ensure(double h) { if (_y + h > Bottom) NewPage(); }

        public byte[] Render()
        {
            NewPage();
            if (_logo != null) _c.Jpeg(_logo, M, M - 4, 46, 46);
            double tx = M + (_logo != null ? 58 : 0);
            _c.Text(tx, M + 17, "WipeKit-Bericht", 21, true, Ink);
            _c.Text(tx, M + 36, _r.Kind.Title, 12, false, Secondary);
            _c.TextRight(PdfCanvas.W - M, M + 14, "Erstellt am", 8.5, false, Secondary);
            _c.TextRight(PdfCanvas.W - M, M + 28, Reports.Dt(_r.Created), 10, true, Ink);
            _y = M + 62;

            if (_r.Banner.HasValue)
            {
                var (text, color) = _r.Banner.Value;
                _c.Rect(M, _y, Cw, 34, Tint(color, 0.13), null, 9);
                _c.Rect(M + 14, _y + 12, 10, 10, color, null, 5);
                _c.Text(M + 32, _y + 21.5, text, 12, true, color);
                _y += 46;
            }

            var i = _r.Identity;
            const double pad = 16;
            double col = (Cw - 2 * pad) / 2;
            var cells = new[]
            {
                ("HERSTELLER", i.Vendor == "" ? "Unbekannt" : i.Vendor), ("MODELL", i.Model == "" ? "Unbekannt" : i.Model),
                ("GRÖSSE", i.Size > 0 ? Fmt.Bytes(i.Size) : "–"), ("SERIENNUMMER", i.Serial == "" ? "–" : i.Serial),
            };
            var wrapped = cells.Select(c => WrapW(c.Item2, 12.5, true, col - 10).Take(2).ToList()).ToList();
            double[] rowH =
            {
                8 + 13 + 15 * Math.Max(wrapped[0].Count, wrapped[1].Count),
                8 + 13 + 15 * Math.Max(wrapped[2].Count, wrapped[3].Count),
            };
            var noteLines = _r.RatingNote == "" ? new List<string>() : WrapW(_r.RatingNote, 9.5, false, Cw - 2 * pad - 120).Take(3).ToList();
            double ratingH = 14 + Math.Max(22, 13 * noteLines.Count + 4);
            double cardH = pad + rowH[0] + rowH[1] + 6 + ratingH + pad - 6;
            _c.Rect(M, _y, Cw, cardH, CardBg, null, 12);
            double yy = _y + pad;
            for (int rr = 0; rr < 2; rr++)
            {
                for (int cc = 0; cc < 2; cc++)
                {
                    int k = rr * 2 + cc;
                    double x = M + pad + cc * col;
                    _c.Text(x, yy + 8, cells[k].Item1, 7.5, true, Secondary);
                    for (int li = 0; li < wrapped[k].Count; li++) _c.Text(x, yy + 24 + li * 15, wrapped[k][li], 12.5, true, Ink);
                }
                yy += rowH[rr];
            }
            _c.Line(M + pad, yy, M + Cw - pad, yy, Separator, 0.6);
            yy += 12;
            _c.Text(M + pad, yy + 8, "GESAMTZUSTAND", 7.5, true, Secondary);
            var rc = HealthParser.RatingRgb(_r.Rating);
            string rt = HealthParser.RatingText(_r.Rating);
            double pw = PdfMetrics.Width(rt, 11, true) + 22;
            _c.Rect(M + pad, yy + 14, pw, 20, rc, null, 10);
            _c.Text(M + pad + 11, yy + 28, rt, 11, true, White);
            for (int li = 0; li < noteLines.Count; li++)
                _c.Text(M + pad + 120, noteLines.Count > 1 ? yy + 15 + li * 13 : yy + 28, noteLines[li], 9.5, false, Secondary);
            _y += cardH + 22;

            foreach (var s in _r.Sections) Section(s);

            int total = _c.Pages.Count;
            for (int n = 0; n < total; n++)
            {
                _c.Cur = _c.Pages[n];
                _c.Cur.Seek(0, SeekOrigin.End);
                _c.Line(M, PdfCanvas.H - Foot, PdfCanvas.W - M, PdfCanvas.H - Foot, Separator, 0.5);
                _c.Text(M, PdfCanvas.H - Foot + 14, $"{(_r.Footer == "" ? "WipeKit" : _r.Footer)} · {_r.Kind.Title}", 8, false, Secondary);
                _c.TextRight(PdfCanvas.W - M, PdfCanvas.H - Foot + 14, $"Seite {n + 1} von {total}", 8, false, Secondary);
            }
            return _c.Build("WipeKit-Bericht – " + _r.Kind.Title, $"{i.Vendor} {i.Model} {i.Serial}".Trim());
        }

        void Section(ReportSection s)
        {
            Ensure(18 + 40);
            _c.Text(M, _y + 11, s.Title, 12.5, true, Ink);
            _y += 20;
            foreach (var b in s.Blocks)
            {
                if (b.Kv != null) Kv(b.Kv);
                else if (b.Table != null) Table(b.Table);
                else if (b.Text != null)
                {
                    foreach (var line in WrapW(b.Text, 9.5, false, Cw))
                    {
                        Ensure(13);
                        _c.Text(M, _y + 10, line, 9.5, false, Secondary);
                        _y += 13;
                    }
                    _y += 4;
                }
                else if (b.Bullets != null)
                {
                    foreach (var it in b.Bullets)
                    {
                        var lines = WrapW(it, 9.5, false, Cw - 14);
                        Ensure(13 * lines.Count);
                        _c.Rect(M + 2, _y + 5.5, 4, 4, Secondary, null, 2);
                        foreach (var line in lines)
                        {
                            _c.Text(M + 14, _y + 10, line, 9.5, false, Ink);
                            _y += 13;
                        }
                    }
                    _y += 4;
                }
            }
            _y += 14;
        }

        // Karte, die über Seitenumbrüche fortgesetzt wird
        double _cardStart;
        long _cardMark;
        int _cardCount;

        void CardBegin() { _cardStart = _y; _cardMark = _c.Cur.Length; _cardCount = 0; }

        void CardFlush()
        {
            if (_cardCount > 0)
            {
                var saved = _c.Cur;
                var tmp = new MemoryStream();
                _c.Cur = tmp;
                _c.Rect(M, _cardStart, Cw, _y - _cardStart, White, Separator, 9, 0.7);
                _c.Cur = saved;
                var all = saved.ToArray();
                var bg = tmp.ToArray();
                saved.SetLength(0);
                saved.Write(all, 0, (int)_cardMark);
                saved.Write(bg, 0, bg.Length);
                saved.Write(all, (int)_cardMark, all.Length - (int)_cardMark);
            }
            _cardCount = 0;
        }

        void CardRow(double h, Action header, Action<double> draw)
        {
            if (_y + h > Bottom)
            {
                CardFlush();
                NewPage();
                CardBegin();
                header?.Invoke();
            }
            if (_cardCount > 0) _c.Line(M + 12, _y, M + Cw - 12, _y, Separator, 0.5);
            draw(_y);
            _y += h;
            _cardCount++;
        }

        void Kv(List<(string, string)> rows)
        {
            const double labelW = 205;
            CardBegin();
            foreach (var (k, v) in rows)
            {
                var ll = WrapW(k, 9.5, false, labelW - 20);
                var vl = WrapW(v, 9.5, false, Cw - labelW - 14);
                int n = Math.Max(ll.Count, vl.Count);
                CardRow(10 + 13 * n, null, yy =>
                {
                    for (int li = 0; li < ll.Count; li++) _c.Text(M + 12, yy + 15 + li * 13, ll[li], 9.5, false, Secondary);
                    var rating = new[] { Rating.VeryGood, Rating.Good, Rating.Caution, Rating.Bad, Rating.Unknown }
                        .Cast<Rating?>().FirstOrDefault(x => HealthParser.RatingText(x.Value) == v);
                    if (k == "Gesamtzustand" && rating.HasValue)
                    {
                        var col = HealthParser.RatingRgb(rating.Value);
                        _c.Rect(M + labelW, yy + 7.5, 7, 7, col, null, 3.5);
                        _c.Text(M + labelW + 12, yy + 15, v, 9.5, true, col);
                        return;
                    }
                    for (int li = 0; li < vl.Count; li++) _c.Text(M + labelW, yy + 15 + li * 13, vl[li], 9.5, k == "Ergebnis", Ink);
                });
            }
            CardFlush();
            _y += 2;
        }

        void Table(ReportTable t)
        {
            double inner = Cw - 24;
            var xs = new List<double>();
            double acc = M + 12;
            foreach (var w in t.Widths) { xs.Add(acc); acc += w * inner; }
            var widths = t.Widths.Select(w => w * inner).ToArray();
            const double size = 8.5;
            void Header()
            {
                for (int ci = 0; ci < t.Headers.Length; ci++)
                {
                    string s = Fit(t.Headers[ci], 7.5, true, widths[ci] - 6);
                    if (t.Align[ci] == 'r') _c.TextRight(xs[ci] + widths[ci] - 6, _y + 14, s, 7.5, true, Secondary);
                    else _c.Text(xs[ci], _y + 14, s, 7.5, true, Secondary);
                }
                _y += 21;
                _cardCount++;
            }
            Ensure(21 + 22);
            CardBegin();
            Header();
            foreach (var row in t.Rows)
            {
                var cells = row.Select((v, ci) => WrapW(v, size, false, widths[ci] - 6).Take(3).ToList()).ToList();
                int n = cells.Max(c => c.Count);
                CardRow(9 + 12 * n, Header, yy =>
                {
                    for (int ci = 0; ci < cells.Count; ci++)
                    {
                        var color = Ink;
                        bool bold = false;
                        if (row[ci] == "Warnung") { color = Reports.Orange; bold = true; }
                        if (row[ci] == "Fehler" || row[ci].StartsWith("Fehlgeschlagen")) { color = Reports.Red; bold = true; }
                        for (int li = 0; li < cells[ci].Count; li++)
                        {
                            if (t.Align[ci] == 'r') _c.TextRight(xs[ci] + widths[ci] - 6, yy + 14 + li * 12, cells[ci][li], size, bold, color);
                            else _c.Text(xs[ci], yy + 14 + li * 12, cells[ci][li], size, bold, color);
                        }
                    }
                });
            }
            CardFlush();
            _y += 2;
        }
    }
}
