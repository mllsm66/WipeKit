import Foundation

// WipeKit-Berichte: Gesundheitsbericht, Löschprotokoll (kurz/ausführlich), Prüfprotokoll – als PDF und Text.
// Gleicher Aufbau wie in der Linux- und Windows-Version.
// PDF: eigener, minimaler Schreiber mit der Standardschrift Helvetica (nicht eingebettet).
// Zeichenbreiten aus den Adobe Core-14-AFM-Dateien:
// Copyright (c) 1985, 1987, 1989, 1990, 1997 Adobe Systems Incorporated. All Rights Reserved.

typealias RGB = (Double, Double, Double)

struct ReportTable {
    let headers: [String]
    let rows: [[String]]
    let widths: [Double]
    let align: [Character]   // "l" | "r"
}

enum ReportBlock {
    case kv([(String, String)])
    case table(ReportTable)
    case text(String)
    case bullets([String])
}

struct ReportSection {
    var title: String
    var blocks: [ReportBlock]
}

struct ReportIdentity {
    var vendor: String
    var model: String
    var size: UInt64
    var serial: String
}

struct ReportKind {
    let title: String
    let fileSuffix: String
    static let health = ReportKind(title: "Gesundheitsbericht", fileSuffix: "Gesundheitsbericht")
    static let wipe = ReportKind(title: "Löschprotokoll", fileSuffix: "Loeschprotokoll")
    static let wipeFull = ReportKind(title: "Löschprotokoll (ausführlich)", fileSuffix: "Loeschprotokoll-ausfuehrlich")
    static let check = ReportKind(title: "Prüfprotokoll", fileSuffix: "Pruefprotokoll")
}

struct Report {
    var kind: ReportKind
    var created: Date
    var identity: ReportIdentity
    var rating: Rating
    var ratingNote: String
    var banner: (String, RGB)?
    var sections: [ReportSection] = []
    var footer: String = ""
}

struct WipeRecord {
    var modeTitle: String
    var method: String
    var standard: String
    var state: JobState
    var started: Date
    var ended: Date
    var bytesWritten: UInt64 = 0
    var message: String = ""
    var verification: String = ""
    var trim: String = ""
    var device: String = ""
    var bus: String = ""
    var typeLabel: String = ""
    var volumes: [String] = []
    var exactSize: UInt64 = 0
    var hiddenArea: String = ""
    var host: String = ""
    var platform: String = ""
    var appVersion: String = ""
    var readOnly = false
    var badBlocks: Int? = nil
    var nonZero: UInt64? = nil
    var purgeMethod: String = ""
}

enum Reports {
    static let green: RGB = (0.12, 0.56, 0.24)
    static let orange: RGB = (0.86, 0.55, 0.00)
    static let red: RGB = (0.85, 0.19, 0.15)

    static let notCertified = "Dieses Protokoll dokumentiert den Ablauf. Es ist keine Zertifizierung nach NIST, DoD oder einer anderen Norm; WipeKit steht in keiner Verbindung zu diesen Stellen."

    static let unavailableHelp = "Hinweis: Viele USB-Gehäuse und -Adapter geben die Gesundheitsdaten nicht weiter. Abhilfe: das Laufwerk direkt (SATA/NVMe intern), über Thunderbolt/USB4 oder über einen Adapter mit ASMedia-, JMicron- oder Realtek-Chip anschließen."

    // MARK: Identität und Dateinamen

    static func identity(health h: Health?, model: String, size: UInt64, serial: String) -> ReportIdentity {
        if let h = h, !h.model.isEmpty {
            let v = h.vendor.isEmpty ? HealthParser.detectVendor(model) : h.vendor
            return ReportIdentity(vendor: v.isEmpty ? "Unbekannt" : v,
                                  model: h.modelNumber.isEmpty ? h.model : h.modelNumber,
                                  size: h.capacity > 0 ? h.capacity : size,
                                  serial: h.serial.isEmpty ? serial : h.serial)
        }
        let m = model.trimmingCharacters(in: .whitespaces)
        let v = HealthParser.detectVendor(m)
        let mn = HealthParser.modelNumber(m)
        return ReportIdentity(vendor: v.isEmpty ? "Unbekannt" : v, model: mn.isEmpty ? (m.isEmpty ? "Unbekannt" : m) : mn,
                              size: size, serial: serial)
    }

    static func slug(_ s: String) -> String {
        var t = s.replacingOccurrences(of: "ß", with: "SS")
        for (a, b) in [("ä", "AE"), ("ö", "OE"), ("ü", "UE"), ("Ä", "AE"), ("Ö", "OE"), ("Ü", "UE")] {
            t = t.replacingOccurrences(of: a, with: b)
        }
        t = t.applyingTransform(.toLatin, reverse: false) ?? t
        t = t.folding(options: [.diacriticInsensitive], locale: nil)
        var out = ""
        var lastDash = false
        for ch in t.unicodeScalars {
            if ch.isASCII && (CharacterSet.alphanumerics.contains(ch)) {
                out.unicodeScalars.append(ch)
                lastDash = false
            } else if !lastDash && !out.isEmpty {
                out.append("-")
                lastDash = true
            }
        }
        while out.hasSuffix("-") { out.removeLast() }
        return out.uppercased()
    }

    static func baseName(_ i: ReportIdentity, _ when: Date) -> String {
        let parts = [slug(i.vendor == "Unbekannt" ? "" : i.vendor), slug(i.model),
                     i.size > 0 ? "\(Int((Double(i.size) / 1e9).rounded()))GB" : "", slug(i.serial)]
        var name = parts.filter { !$0.isEmpty }.joined(separator: "-")
        if name.isEmpty { name = "LAUFWERK" }
        let f = DateFormatter()
        f.locale = Locale(identifier: "de_DE")
        f.dateFormat = "yyyy-MM-dd_HH-mm"
        return f.string(from: when) + "_" + String(name.prefix(150))
    }

    static var folder: URL? {
        if let env = ProcessInfo.processInfo.environment["WIPEKIT_REPORTS"], !env.isEmpty {
            return URL(fileURLWithPath: env, isDirectory: true)
        }
        return FileManager.default.urls(for: .documentDirectory, in: .userDomainMask).first?
            .appendingPathComponent("WipeKit-Protokolle", isDirectory: true)
    }

    /// Speichert PDF und Text. Gibt (PDF, Text) zurück.
    static func save(_ r: Report, to base: URL? = nil) throws -> (URL, URL) {
        guard let root = base ?? folder else { throw CocoaError(.fileNoSuchFile) }
        let pdfDir = root.appendingPathComponent("PDF", isDirectory: true)
        let txtDir = root.appendingPathComponent("Text", isDirectory: true)
        try FileManager.default.createDirectory(at: pdfDir, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: txtDir, withIntermediateDirectories: true)
        let name = baseName(r.identity, r.created) + "_" + r.kind.fileSuffix
        let pdf = unique(pdfDir.appendingPathComponent(name + ".pdf"))
        let txt = unique(txtDir.appendingPathComponent(name + ".txt"))
        try renderPDF(r).write(to: pdf)
        try renderText(r).write(to: txt, atomically: true, encoding: .utf8)
        return (pdf, txt)
    }

    private static func unique(_ u: URL) -> URL {
        guard FileManager.default.fileExists(atPath: u.path) else { return u }
        let stem = u.deletingPathExtension().lastPathComponent, ext = u.pathExtension
        for i in 2..<100 {
            let c = u.deletingLastPathComponent().appendingPathComponent("\(stem)_\(i).\(ext)")
            if !FileManager.default.fileExists(atPath: c.path) { return c }
        }
        return u
    }

    // MARK: Inhalte

    static func dt(_ d: Date, seconds: Bool = false) -> String {
        let f = DateFormatter()
        f.locale = Locale(identifier: "de_DE")
        f.dateFormat = seconds ? "dd.MM.yyyy, HH:mm:ss 'Uhr'" : "dd.MM.yyyy, HH:mm 'Uhr'"
        return f.string(from: d)
    }

    static func healthSections(_ h: Health?, suffix: String = "", tables: Bool = true) -> [ReportSection] {
        guard let h = h, h.available else {
            let reason = (h?.reason.isEmpty == false) ? h!.reason : "Keine S.M.A.R.T.-Daten verfügbar."
            return [ReportSection(title: "Gesundheit" + suffix, blocks: [.text(reason), .text(unavailableHelp)])]
        }
        var secs = [ReportSection(title: "Kennwerte" + suffix, blocks: [.kv(HFmt.keyValues(h))]),
                    ReportSection(title: "Bewertung" + suffix, blocks: [.bullets(h.reasons)]),
                    ReportSection(title: "Laufwerk" + suffix, blocks: [.kv(HFmt.driveDetails(h))])]
        guard tables else { return secs }
        if !h.attributes.isEmpty {
            let rows = h.attributes.map { ["\($0.id)", $0.name, "\($0.value)", "\($0.worst)", "\($0.thresh)", $0.rawString, $0.status] }
            secs.append(ReportSection(title: "S.M.A.R.T.-Attribute" + suffix, blocks: [.table(ReportTable(
                headers: ["ID", "Attribut", "Wert", "Schlechtester", "Grenzwert", "Rohwert", "Status"], rows: rows,
                widths: [0.06, 0.36, 0.08, 0.13, 0.11, 0.16, 0.10], align: ["r", "l", "r", "r", "r", "r", "l"]))]))
        }
        if !h.nvme.isEmpty {
            var rows = HFmt.nvmeRows(h)
            for (i, t) in ((h.nvme["temperature_sensors"] as? [NSNumber]) ?? []).enumerated() {
                rows.append(("Temperatursensor \(i + 1)", HFmt.temp(t.intValue)))
            }
            secs.append(ReportSection(title: "NVMe-Gesundheitswerte" + suffix, blocks: [.kv(rows)]))
        }
        if !h.selfTests.isEmpty {
            let rows = h.selfTests.map { [$0.kind, $0.result, $0.hours.map { HFmt.count($0) } ?? "–"] }
            secs.append(ReportSection(title: "Selbsttest-Protokoll des Laufwerks" + suffix, blocks: [.table(ReportTable(
                headers: ["Test", "Ergebnis", "Bei Betriebsstunde"], rows: rows, widths: [0.25, 0.53, 0.22],
                align: ["l", "l", "r"]))]))
        }
        return secs
    }

    private static func note(_ h: Health?) -> String {
        guard let h = h else { return "" }
        if h.available { return h.reasons.first ?? "" }
        return h.reason
    }

    static func healthReport(_ h: Health?, identity: ReportIdentity, appVersion: String, platform: String,
                             host: String, created: Date = Date()) -> Report {
        var r = Report(kind: .health, created: created, identity: identity, rating: h?.rating ?? .unknown,
                       ratingNote: note(h))
        r.sections = healthSections(h)
        r.sections.append(ReportSection(title: "Bericht", blocks: [.kv([
            ("Erstellt", dt(created, seconds: true)), ("Rechner", host.isEmpty ? "–" : host),
            ("System", platform.isEmpty ? "–" : platform), ("Programm", "WipeKit \(appVersion)")])]))
        r.footer = "WipeKit \(appVersion)"
        return r
    }

    static func result(_ w: WipeRecord) -> (String, RGB) {
        if w.readOnly {
            switch w.state {
            case .done:
                if let b = w.badBlocks, b > 0 { return ("Unlesbare Bereiche gefunden", red) }
                if w.nonZero == 0 { return ("Laufwerk ist leer (nur Nullen)", green) }
                return ("Laufwerk enthält Daten", orange)
            case .cancelled: return ("Prüfung abgebrochen", orange)
            default: return ("Prüfung fehlgeschlagen", red)
            }
        }
        switch w.state {
        case .done: return ("Erfolgreich gelöscht", green)
        case .cancelled: return ("Abgebrochen – das Laufwerk wurde nur teilweise überschrieben", orange)
        default: return ("Fehlgeschlagen", red)
        }
    }

    static func wipeReport(_ w: WipeRecord, identity: ReportIdentity, before: Health?, after: Health?,
                           detailed: Bool, created: Date? = nil) -> Report {
        let h = (after?.available == true) ? after : before
        let (res, color) = result(w)
        let kind: ReportKind = w.readOnly ? .check : (detailed ? .wipeFull : .wipe)
        var r = Report(kind: kind, created: created ?? w.ended, identity: identity, rating: h?.rating ?? .unknown,
                       ratingNote: note(h), banner: (res, color))
        let duration = max(0, w.ended.timeIntervalSince(w.started))
        let avg = duration > 0 ? Double(w.bytesWritten) / duration : 0
        var rows: [(String, String)] = [("Ergebnis", res), ("Methode", w.modeTitle), ("Ablauf", w.method),
                                        ("Vorgehen orientiert an", w.standard)]
        if !w.purgeMethod.isEmpty { rows.append(("Firmware-Befehl", w.purgeMethod)) }
        rows += [("Beginn", dt(w.started, seconds: true)), ("Ende", dt(w.ended, seconds: true)),
                 ("Dauer", Fmt.duration(duration)),
                 ("Ø Geschwindigkeit", avg > 0 && w.purgeMethod.isEmpty ? HFmt.bytes(UInt64(avg)) + "/s" : "–")]
        if w.readOnly {
            if let nz = w.nonZero {
                rows.append(("Inhalt", nz == 0 ? "leer – alle Sektoren enthalten Nullen" : "\(HFmt.bytesLong(nz)) enthalten Daten"))
            }
            if let b = w.badBlocks { rows.append(("Unlesbare Bereiche", b == 0 ? "keine" : "\(b) (je 64 KiB)")) }
        } else {
            rows.append(("Verifikation", w.verification.isEmpty ? "–" : w.verification))
        }
        if !w.trim.isEmpty { rows.append(("TRIM", w.trim)) }
        if !w.hiddenArea.isEmpty { rows.append(("Versteckte Bereiche", w.hiddenArea)) }
        if !w.message.isEmpty { rows.append(("Meldung", w.message)) }
        r.sections.append(ReportSection(title: w.readOnly ? "Prüfung" : "Löschvorgang", blocks: [.kv(rows)]))
        r.sections.append(ReportSection(title: "Laufwerk", blocks: [.kv([
            ("Kennung", w.device.isEmpty ? "–" : w.device), ("Anschluss", w.bus.isEmpty ? "–" : w.bus),
            ("Typ", w.typeLabel.isEmpty ? "–" : w.typeLabel),
            ("Kapazität", HFmt.bytesLong(w.exactSize > 0 ? w.exactSize : identity.size)),
            ("Frühere Volumes", w.volumes.isEmpty ? "–" : w.volumes.joined(separator: ", "))])]))
        if detailed || w.readOnly {
            if !w.readOnly, let b = before, let a = after, b.available, a.available {
                let kb = HFmt.keyValues(b), ka = HFmt.keyValues(a)
                let rows = zip(kb, ka).map { [$0.0.0, $0.0.1, $0.1.1] }
                r.sections.append(ReportSection(title: "Kennwerte vor und nach dem Löschen", blocks: [.table(ReportTable(
                    headers: ["Wert", "Vor dem Löschen", "Nach dem Löschen"], rows: rows, widths: [0.36, 0.32, 0.32],
                    align: ["l", "l", "l"]))]))
                r.sections.append(ReportSection(title: "Bewertung", blocks: [.bullets(a.reasons)]))
                r.sections.append(ReportSection(title: "Laufwerksdaten", blocks: [.kv(HFmt.driveDetails(a))]))
                r.sections += Array(healthSections(a, suffix: " (nach dem Löschen)").dropFirst(3))
            } else {
                r.sections += healthSections(h).map { s in
                    var s = s
                    if s.title == "Laufwerk" { s.title = "Laufwerksdaten" }
                    return s
                }
            }
        }
        r.sections.append(ReportSection(title: "Protokoll", blocks: [.kv([
            ("Erstellt", dt(r.created, seconds: true)), ("Rechner", w.host.isEmpty ? "–" : w.host),
            ("System", w.platform.isEmpty ? "–" : w.platform), ("Programm", "WipeKit \(w.appVersion)")]), .text(notCertified)]))
        r.footer = "WipeKit \(w.appVersion)"
        return r
    }

    // MARK: Text

    static let txtWidth = 78

    static func wrap(_ text: String, _ width: Int) -> [String] {
        var lines: [String] = [], cur = ""
        for var w in text.split(separator: " ", omittingEmptySubsequences: true).map(String.init) {
            while w.count > width {
                if !cur.isEmpty { lines.append(cur); cur = "" }
                lines.append(String(w.prefix(width)))
                w = String(w.dropFirst(width))
            }
            if cur.isEmpty { cur = w }
            else if cur.count + 1 + w.count <= width { cur += " " + w }
            else { lines.append(cur); cur = w }
        }
        if !cur.isEmpty || lines.isEmpty { lines.append(cur) }
        return lines
    }

    private static func kvText(_ rows: [(String, String)], _ labelW: Int? = nil) -> [String] {
        let lw = labelW ?? min(38, (rows.map { $0.0.count }.max() ?? 10) + 2)
        var out: [String] = []
        for (k, v) in rows {
            let vl = wrap(v, txtWidth - lw)
            out.append((k + ":").padding(toLength: lw, withPad: " ", startingAt: 0) + vl[0])
            out += vl.dropFirst().map { String(repeating: " ", count: lw) + $0 }
        }
        return out
    }

    static func renderText(_ r: Report) -> String {
        let line = String(repeating: "-", count: txtWidth)
        var L = ["WIPEKIT-BERICHT", r.kind.title, "Erstellt am \(dt(r.created))", String(repeating: "=", count: txtWidth), ""]
        if let b = r.banner { L += ["ERGEBNIS: \(b.0.uppercased())", ""] }
        let i = r.identity
        L += ["ÜBERSICHT", line]
        L += kvText([("Hersteller", i.vendor.isEmpty ? "Unbekannt" : i.vendor), ("Modell", i.model.isEmpty ? "Unbekannt" : i.model),
                     ("Größe", i.size > 0 ? HFmt.bytes(i.size) : "–"), ("Seriennummer", i.serial.isEmpty ? "–" : i.serial),
                     ("Gesamtzustand", r.rating.rawValue.uppercased() + (r.ratingNote.isEmpty ? "" : " – \(r.ratingNote)"))], 18)
        L.append("")
        for s in r.sections {
            L += [s.title.uppercased(), line]
            for b in s.blocks {
                switch b {
                case .kv(let rows): L += kvText(rows)
                case .text(let t): L += wrap(t, txtWidth)
                case .bullets(let items):
                    for it in items {
                        let wl = wrap(it, txtWidth - 2)
                        L.append("- " + wl[0])
                        L += wl.dropFirst().map { "  " + $0 }
                    }
                case .table(let t):
                    let widths = (0..<t.headers.count).map { ci in
                        min(60, ([t.headers] + t.rows).map { $0[ci].count }.max() ?? 3)
                    }
                    func fmtRow(_ cells: [String]) -> String {
                        var out: [String] = []
                        for (ci, c) in cells.enumerated() {
                            let w = widths[ci]
                            var c = c
                            if c.count > w { c = String(c.prefix(w - 1)) + "…" }
                            let pad = String(repeating: " ", count: max(0, w - c.count))
                            out.append(t.align[ci] == "r" ? pad + c : c + pad)
                        }
                        var s = out.joined(separator: "  ")
                        while s.hasSuffix(" ") { s.removeLast() }
                        return s
                    }
                    L.append(fmtRow(t.headers))
                    L.append(widths.map { String(repeating: "-", count: $0) }.joined(separator: "  "))
                    L += t.rows.map(fmtRow)
                }
                L.append("")
            }
        }
        L += [line, "Erstellt mit \(r.footer.isEmpty ? "WipeKit" : r.footer)"]
        return L.joined(separator: "\n") + "\n"
    }

    // MARK: PDF

    static var logo: Data? {
        if let u = Bundle.main.url(forResource: "report-logo", withExtension: "jpg") { return try? Data(contentsOf: u) }
        return nil
    }

    static func renderPDF(_ r: Report) -> Data {
        PDFLayout(report: r, logo: logo).render()
    }
}

// MARK: - Minimaler PDF-Schreiber

enum PDFMetrics {
    static let helvetica: [Int] = [
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
    ]
    static let helveticaBold: [Int] = [
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
    ]
    static func ansi(_ s: String) -> [UInt8] {
        var out: [UInt8] = []
        let repl: [Character: String] = ["≈": "ca.", "→": "->", "≥": ">=", "≤": "<=", "✓": "OK", "✗": "X",
                                         "\u{2009}": " ", "\u{202F}": " ", "\u{00A0}": " ", "−": "-", "×": "x"]
        for ch in s {
            let t = repl[ch] ?? String(ch)
            for c in t {
                if let d = String(c).data(using: .windowsCP1252), d.count == 1 { out.append(d[0]) } else { out.append(63) }
            }
        }
        return out
    }

    static func width(_ s: String, _ size: Double, _ bold: Bool) -> Double {
        let t = bold ? helveticaBold : helvetica
        var w = 0
        for c in ansi(s) where c >= 32 { w += t[Int(c) - 32] }
        return Double(w) * size / 1000
    }
}

final class PDFCanvas {
    let w = 595.28, h = 841.89
    var pages: [Data] = []
    private var cur = Data()
    private var curIndex = -1
    var image: Data?

    func newPage() {
        if curIndex >= 0 { pages[curIndex] = cur }
        pages.append(Data())
        curIndex = pages.count - 1
        cur = Data()
    }

    func select(_ i: Int) {
        if curIndex >= 0 { pages[curIndex] = cur }
        curIndex = i
        cur = pages[i]
    }

    var mark: Int { cur.count }
    func insert(_ d: Data, at: Int) { cur.insert(contentsOf: d, at: cur.startIndex + at) }
    func capture(_ body: () -> Void) -> Data {
        let saved = cur
        cur = Data()
        body()
        let out = cur
        cur = saved
        return out
    }

    private func op(_ s: String) { cur.append(s.data(using: .isoLatin1)!); cur.append(10) }
    static func n(_ v: Double) -> String {
        var s = String(format: "%.2f", v)
        while s.contains(".") && (s.hasSuffix("0") || s.hasSuffix(".")) { s.removeLast() }
        return s == "-0" || s.isEmpty ? "0" : s
    }
    private func rgb(_ c: RGB) -> String { "\(PDFCanvas.n(c.0)) \(PDFCanvas.n(c.1)) \(PDFCanvas.n(c.2))" }

    func text(_ x: Double, _ y: Double, _ s: String, _ size: Double = 10, _ bold: Bool = false, _ color: RGB = (0, 0, 0)) {
        op("BT /\(bold ? "F2" : "F1") \(PDFCanvas.n(size)) Tf \(rgb(color)) rg \(PDFCanvas.n(x)) \(PDFCanvas.n(h - y)) Td")
        var bytes: [UInt8] = [40]
        for b in PDFMetrics.ansi(s) {
            if b == 40 || b == 41 || b == 92 { bytes.append(92) }
            bytes.append(b)
        }
        bytes.append(contentsOf: Array(") Tj ET\n".utf8))
        cur.append(contentsOf: bytes)
    }

    func textRight(_ xr: Double, _ y: Double, _ s: String, _ size: Double = 10, _ bold: Bool = false, _ color: RGB = (0, 0, 0)) {
        text(xr - PDFMetrics.width(s, size, bold), y, s, size, bold, color)
    }

    func rect(_ x: Double, _ y: Double, _ w: Double, _ hh: Double, fill: RGB? = nil, stroke: RGB? = nil,
              radius: Double = 0, lineWidth: Double = 0.5) {
        let x0 = x, y0 = h - y - hh
        let r = min(radius, w / 2, hh / 2)
        let N = PDFCanvas.n
        var p: String
        if r > 0 {
            let k = 0.5523 * r
            p = "\(N(x0 + r)) \(N(y0)) m \(N(x0 + w - r)) \(N(y0)) l "
            p += "\(N(x0 + w - r + k)) \(N(y0)) \(N(x0 + w)) \(N(y0 + r - k)) \(N(x0 + w)) \(N(y0 + r)) c "
            p += "\(N(x0 + w)) \(N(y0 + hh - r)) l "
            p += "\(N(x0 + w)) \(N(y0 + hh - r + k)) \(N(x0 + w - r + k)) \(N(y0 + hh)) \(N(x0 + w - r)) \(N(y0 + hh)) c "
            p += "\(N(x0 + r)) \(N(y0 + hh)) l "
            p += "\(N(x0 + r - k)) \(N(y0 + hh)) \(N(x0)) \(N(y0 + hh - r + k)) \(N(x0)) \(N(y0 + hh - r)) c "
            p += "\(N(x0)) \(N(y0 + r)) l "
            p += "\(N(x0)) \(N(y0 + r - k)) \(N(x0 + r - k)) \(N(y0)) \(N(x0 + r)) \(N(y0)) c h"
        } else {
            p = "\(N(x0)) \(N(y0)) \(N(w)) \(N(hh)) re"
        }
        var ops: [String] = []
        if let f = fill { ops.append("\(rgb(f)) rg") }
        if let s = stroke { ops.append("\(rgb(s)) RG \(N(lineWidth)) w") }
        let paint = fill != nil && stroke != nil ? "B" : (fill != nil ? "f" : "S")
        op(ops.joined(separator: " ") + " \(p) \(paint)")
    }

    func line(_ x1: Double, _ y1: Double, _ x2: Double, _ y2: Double, _ color: RGB = (0.8, 0.8, 0.8), _ width: Double = 0.5) {
        let N = PDFCanvas.n
        op("\(rgb(color)) RG \(N(width)) w \(N(x1)) \(N(h - y1)) m \(N(x2)) \(N(h - y2)) l S")
    }

    func jpeg(_ data: Data, _ x: Double, _ y: Double, _ w: Double, _ hh: Double) {
        image = data
        let N = PDFCanvas.n
        op("q \(N(w)) 0 0 \(N(hh)) \(N(x)) \(N(h - y - hh)) cm /Im1 Do Q")
    }

    static func jpegSize(_ d: [UInt8]) -> (Int, Int) {
        var i = 2
        while i < d.count - 9 {
            if d[i] != 0xFF { i += 1; continue }
            let marker = d[i + 1]
            let len = (Int(d[i + 2]) << 8) | Int(d[i + 3])
            if marker == 0xC0 || marker == 0xC1 || marker == 0xC2 {
                return ((Int(d[i + 7]) << 8) | Int(d[i + 8]), (Int(d[i + 5]) << 8) | Int(d[i + 6]))
            }
            i += 2 + len
        }
        return (0, 0)
    }

    func build(title: String, subject: String) -> Data {
        if curIndex >= 0 { pages[curIndex] = cur }
        var objs: [Int: Data] = [:]
        let pageIDs = (0..<pages.count).map { 7 + 2 * $0 }
        objs[1] = Data("<< /Type /Catalog /Pages 2 0 R /Lang (de-DE) /ViewerPreferences << /DisplayDocTitle true >> >>".utf8)
        objs[2] = Data("<< /Type /Pages /Count \(pages.count) /Kids [\(pageIDs.map { "\($0) 0 R" }.joined(separator: " "))] >>".utf8)
        objs[3] = Data("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>".utf8)
        objs[4] = Data("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>".utf8)
        var res = "<< /Font << /F1 3 0 R /F2 4 0 R >>"
        if let img = image {
            let (iw, ih) = PDFCanvas.jpegSize([UInt8](img))
            var d = Data("<< /Type /XObject /Subtype /Image /Width \(iw) /Height \(ih) /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length \(img.count) >>\nstream\n".utf8)
            d.append(img)
            d.append(Data("\nendstream".utf8))
            objs[5] = d
            res += " /XObject << /Im1 5 0 R >>"
        }
        res += " >>"
        let f = DateFormatter()
        f.dateFormat = "yyyyMMddHHmmss"
        f.locale = Locale(identifier: "en_US_POSIX")
        func u16(_ s: String) -> String {
            var hex = "<FEFF"
            for u in s.utf16 { hex += String(format: "%04X", u) }
            return hex + ">"
        }
        objs[6] = Data("<< /Title \(u16(title)) /Subject \(u16(subject)) /Creator (WipeKit) /Producer (WipeKit) /CreationDate (D:\(f.string(from: Date()))) >>".utf8)
        for (i, content) in pages.enumerated() {
            let pid = pageIDs[i]
            objs[pid] = Data("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 \(PDFCanvas.n(w)) \(PDFCanvas.n(h))] /Contents \(pid + 1) 0 R /Resources \(res) >>".utf8)
            var d = Data("<< /Length \(content.count) >>\nstream\n".utf8)
            d.append(content)
            d.append(Data("\nendstream".utf8))
            objs[pid + 1] = d
        }
        var out = Data("%PDF-1.4\n".utf8)
        out.append(contentsOf: [0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A])
        let maxID = objs.keys.max() ?? 6
        var offsets = [Int](repeating: 0, count: maxID + 1)
        for oid in 1...maxID {
            offsets[oid] = out.count
            out.append(Data("\(oid) 0 obj\n".utf8))
            out.append(objs[oid] ?? Data("null".utf8))
            out.append(Data("\nendobj\n".utf8))
        }
        let xref = out.count
        var x = "xref\n0 \(maxID + 1)\n0000000000 65535 f \n"
        for oid in 1...maxID { x += String(format: "%010d 00000 n \n", offsets[oid]) }
        x += "trailer\n<< /Size \(maxID + 1) /Root 1 0 R /Info 6 0 R >>\nstartxref\n\(xref)\n%%EOF\n"
        out.append(Data(x.utf8))
        return out
    }
}

// MARK: - Seitengestaltung

final class PDFLayout {
    static let ink: RGB = (0.11, 0.11, 0.12)
    static let secondary: RGB = (0.43, 0.43, 0.45)
    static let separator: RGB = (0.84, 0.84, 0.86)
    static let card: RGB = (0.96, 0.96, 0.97)
    static let white: RGB = (1, 1, 1)
    static let M = 48.0
    static let foot = 40.0

    let r: Report
    let logo: Data?
    let c = PDFCanvas()
    var y = 0.0
    var cw: Double { c.w - 2 * PDFLayout.M }
    var bottom: Double { c.h - PDFLayout.foot - 14 }

    init(report: Report, logo: Data?) {
        r = report
        self.logo = logo
    }

    static func tint(_ col: RGB, _ a: Double = 0.12) -> RGB {
        (1 - a * (1 - col.0), 1 - a * (1 - col.1), 1 - a * (1 - col.2))
    }

    static func wrapW(_ text: String, _ size: Double, _ bold: Bool, _ width: Double) -> [String] {
        var lines: [String] = [], cur = ""
        for w in text.components(separatedBy: " ") {
            let cand = cur.isEmpty ? w : cur + " " + w
            if PDFMetrics.width(cand, size, bold) <= width { cur = cand; continue }
            if !cur.isEmpty { lines.append(cur) }
            cur = w
            while PDFMetrics.width(cur, size, bold) > width && cur.count > 1 {
                var k = cur.count
                while k > 1 && PDFMetrics.width(String(cur.prefix(k)), size, bold) > width { k -= 1 }
                lines.append(String(cur.prefix(k)))
                cur = String(cur.dropFirst(k))
            }
        }
        lines.append(cur)
        return lines
    }

    static func fit(_ text: String, _ size: Double, _ bold: Bool, _ width: Double) -> String {
        if PDFMetrics.width(text, size, bold) <= width { return text }
        var t = text
        while !t.isEmpty && PDFMetrics.width(t + "…", size, bold) > width { t.removeLast() }
        return t + "…"
    }

    func newPage() { c.newPage(); y = PDFLayout.M }
    func ensure(_ hh: Double) { if y + hh > bottom { newPage() } }

    func render() -> Data {
        let M = PDFLayout.M
        newPage()
        if let l = logo { c.jpeg(l, M, M - 4, 46, 46) }
        let tx = M + (logo != nil ? 58 : 0)
        c.text(tx, M + 17, "WipeKit-Bericht", 21, true, PDFLayout.ink)
        c.text(tx, M + 36, r.kind.title, 12, false, PDFLayout.secondary)
        c.textRight(c.w - M, M + 14, "Erstellt am", 8.5, false, PDFLayout.secondary)
        c.textRight(c.w - M, M + 28, Reports.dt(r.created), 10, true, PDFLayout.ink)
        y = M + 62

        if let banner = r.banner {
            let (text, color) = banner
            c.rect(M, y, cw, 34, fill: PDFLayout.tint(color, 0.13), radius: 9)
            c.rect(M + 14, y + 12, 10, 10, fill: color, radius: 5)
            c.text(M + 32, y + 21.5, text, 12, true, color)
            y += 46
        }

        // Übersicht
        let i = r.identity
        let pad = 16.0
        let col = (cw - 2 * pad) / 2
        let cells = [("HERSTELLER", i.vendor.isEmpty ? "Unbekannt" : i.vendor), ("MODELL", i.model.isEmpty ? "Unbekannt" : i.model),
                     ("GRÖSSE", i.size > 0 ? HFmt.bytes(i.size) : "–"), ("SERIENNUMMER", i.serial.isEmpty ? "–" : i.serial)]
        let wrapped = cells.map { Array(PDFLayout.wrapW($0.1, 12.5, true, col - 10).prefix(2)) }
        let rowH = [8 + 13 + 15 * Double(max(wrapped[0].count, wrapped[1].count)),
                    8 + 13 + 15 * Double(max(wrapped[2].count, wrapped[3].count))]
        let noteLines = r.ratingNote.isEmpty ? [] : Array(PDFLayout.wrapW(r.ratingNote, 9.5, false, cw - 2 * pad - 120).prefix(3))
        let ratingH = 14 + max(22, 13 * Double(noteLines.count) + 4)
        let cardH = pad + rowH[0] + rowH[1] + 6 + ratingH + pad - 6
        c.rect(M, y, cw, cardH, fill: PDFLayout.card, radius: 12)
        var yy = y + pad
        for rr in 0..<2 {
            for cc in 0..<2 {
                let k = rr * 2 + cc
                let x = M + pad + Double(cc) * col
                c.text(x, yy + 8, cells[k].0, 7.5, true, PDFLayout.secondary)
                for (li, line) in wrapped[k].enumerated() {
                    c.text(x, yy + 24 + Double(li) * 15, line, 12.5, true, PDFLayout.ink)
                }
            }
            yy += rowH[rr]
        }
        c.line(M + pad, yy, M + cw - pad, yy, PDFLayout.separator, 0.6)
        yy += 12
        c.text(M + pad, yy + 8, "GESAMTZUSTAND", 7.5, true, PDFLayout.secondary)
        let color = r.rating.rgb
        let pw = PDFMetrics.width(r.rating.rawValue, 11, true) + 22
        c.rect(M + pad, yy + 14, pw, 20, fill: color, radius: 10)
        c.text(M + pad + 11, yy + 28, r.rating.rawValue, 11, true, PDFLayout.white)
        for (li, line) in noteLines.enumerated() {
            c.text(M + pad + 120, noteLines.count > 1 ? yy + 15 + Double(li) * 13 : yy + 28, line, 9.5, false, PDFLayout.secondary)
        }
        y += cardH + 22

        for s in r.sections { section(s) }

        let total = c.pages.count
        for n in 0..<total {
            c.select(n)
            c.line(M, c.h - PDFLayout.foot, c.w - M, c.h - PDFLayout.foot, PDFLayout.separator, 0.5)
            c.text(M, c.h - PDFLayout.foot + 14, "\(r.footer.isEmpty ? "WipeKit" : r.footer) · \(r.kind.title)", 8, false, PDFLayout.secondary)
            c.textRight(c.w - M, c.h - PDFLayout.foot + 14, "Seite \(n + 1) von \(total)", 8, false, PDFLayout.secondary)
        }
        return c.build(title: "WipeKit-Bericht – \(r.kind.title)", subject: "\(i.vendor) \(i.model) \(i.serial)")
    }

    private func section(_ s: ReportSection) {
        let M = PDFLayout.M
        ensure(18 + 40)
        c.text(M, y + 11, s.title, 12.5, true, PDFLayout.ink)
        y += 20
        for b in s.blocks {
            switch b {
            case .kv(let rows): kv(rows)
            case .table(let t): table(t)
            case .text(let t):
                for line in PDFLayout.wrapW(t, 9.5, false, cw) {
                    ensure(13)
                    c.text(M, y + 10, line, 9.5, false, PDFLayout.secondary)
                    y += 13
                }
                y += 4
            case .bullets(let items):
                for it in items {
                    let lines = PDFLayout.wrapW(it, 9.5, false, cw - 14)
                    ensure(13 * Double(lines.count))
                    c.rect(M + 2, y + 5.5, 4, 4, fill: PDFLayout.secondary, radius: 2)
                    for line in lines {
                        c.text(M + 14, y + 10, line, 9.5, false, PDFLayout.ink)
                        y += 13
                    }
                }
                y += 4
            }
        }
        y += 14
    }

    // Karte, die über Seitenumbrüche fortgesetzt wird
    private var cardStart = 0.0
    private var cardMark = 0
    private var cardCount = 0

    private func cardBegin() { cardStart = y; cardMark = c.mark; cardCount = 0 }
    private func cardFlush() {
        if cardCount > 0 {
            let start = cardStart, hh = y - cardStart, w = cw
            let bg = c.capture { c.rect(PDFLayout.M, start, w, hh, fill: PDFLayout.white, stroke: PDFLayout.separator, radius: 9, lineWidth: 0.7) }
            c.insert(bg, at: cardMark)
        }
        cardCount = 0
    }
    private func cardRow(_ hh: Double, header: (() -> Void)? = nil, draw: (Double) -> Void) {
        if y + hh > bottom {
            cardFlush()
            newPage()
            cardBegin()
            header?()
        }
        if cardCount > 0 { c.line(PDFLayout.M + 12, y, PDFLayout.M + cw - 12, y, PDFLayout.separator, 0.5) }
        draw(y)
        y += hh
        cardCount += 1
    }

    private func kv(_ rows: [(String, String)]) {
        let M = PDFLayout.M, labelW = 205.0
        cardBegin()
        for (k, v) in rows {
            let ll = PDFLayout.wrapW(k, 9.5, false, labelW - 20)
            let vl = PDFLayout.wrapW(v, 9.5, false, cw - labelW - 14)
            let n = max(ll.count, vl.count)
            cardRow(10 + 13 * Double(n), draw: { yy in
                for (li, line) in ll.enumerated() { c.text(M + 12, yy + 15 + Double(li) * 13, line, 9.5, false, PDFLayout.secondary) }
                if k == "Gesamtzustand", let rating = Rating(rawValue: v) {
                    c.rect(M + labelW, yy + 7.5, 7, 7, fill: rating.rgb, radius: 3.5)
                    c.text(M + labelW + 12, yy + 15, v, 9.5, true, rating.rgb)
                    return
                }
                for (li, line) in vl.enumerated() {
                    c.text(M + labelW, yy + 15 + Double(li) * 13, line, 9.5, k == "Ergebnis", PDFLayout.ink)
                }
            })
        }
        cardFlush()
        y += 2
    }

    private func table(_ t: ReportTable) {
        let M = PDFLayout.M
        let inner = cw - 24
        var xs: [Double] = [], acc = M + 12
        for w in t.widths { xs.append(acc); acc += w * inner }
        let widths = t.widths.map { $0 * inner }
        let size = 8.5
        let header = { [self] in
            for (ci, text) in t.headers.enumerated() {
                let s = PDFLayout.fit(text, 7.5, true, widths[ci] - 6)
                if t.align[ci] == "r" { c.textRight(xs[ci] + widths[ci] - 6, y + 14, s, 7.5, true, PDFLayout.secondary) }
                else { c.text(xs[ci], y + 14, s, 7.5, true, PDFLayout.secondary) }
            }
            y += 21
            cardCount += 1
        }
        ensure(21 + 22)
        cardBegin()
        header()
        for row in t.rows {
            let cells = zip(row, widths).map { Array(PDFLayout.wrapW($0.0, size, false, $0.1 - 6).prefix(3)) }
            let n = cells.map { $0.count }.max() ?? 1
            cardRow(9 + 12 * Double(n), header: header, draw: { yy in
                for ci in 0..<cells.count {
                    var color = PDFLayout.ink, bold = false
                    if row[ci] == "Warnung" { color = Reports.orange; bold = true }
                    if row[ci] == "Fehler" || row[ci].hasPrefix("Fehlgeschlagen") { color = Reports.red; bold = true }
                    for (li, line) in cells[ci].enumerated() {
                        if t.align[ci] == "r" { c.textRight(xs[ci] + widths[ci] - 6, yy + 14 + Double(li) * 12, line, size, bold, color) }
                        else { c.text(xs[ci], yy + 14 + Double(li) * 12, line, size, bold, color) }
                    }
                }
            })
        }
        cardFlush()
        y += 2
    }
}
