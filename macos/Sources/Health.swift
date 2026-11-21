import Foundation

// Auswertung der S.M.A.R.T.-Daten (smartctl -x -j) – gleiche Logik wie in der Linux- und Windows-Version.

enum Rating: String, Codable {
    case veryGood = "Sehr gut"
    case good = "Gut"
    case caution = "Bedenklich"
    case bad = "Schlecht"
    case unknown = "Unbekannt"

    /// RGB 0…1 (auch für die PDF-Berichte)
    var rgb: (Double, Double, Double) {
        switch self {
        case .veryGood: return (0.12, 0.56, 0.24)
        case .good:     return (0.10, 0.45, 0.91)
        case .caution:  return (0.86, 0.55, 0.00)
        case .bad:      return (0.85, 0.19, 0.15)
        case .unknown:  return (0.50, 0.53, 0.55)
        }
    }
}

struct SmartAttribute: Identifiable {
    let id: Int
    let name: String
    let value: Int
    let worst: Int
    let thresh: Int
    let raw: Int64?
    let rawString: String
    let failed: String
    let status: String     // OK, Warnung, Fehler
}

struct SelfTestEntry: Identifiable {
    let id = UUID()
    let kind: String
    let result: String
    let hours: Int?
    let passed: Bool?
}

struct Health {
    var available = false
    var reason = ""
    var proto = ""
    var vendor = ""
    var family = ""
    var model = ""
    var modelNumber = ""
    var serial = ""
    var firmware = ""
    var capacity: UInt64 = 0
    var isSSD: Bool? = nil
    var rpm: Int? = nil
    var formFactor = ""
    var interface = ""
    var interfaceMax = ""
    var interfaceCurrent = ""
    var sataVersion = ""
    var ataVersion = ""
    var trim: Bool? = nil
    var security = ""
    var securityFrozen: Bool? = nil
    var smartPassed: Bool? = nil
    var temperature: Int? = nil
    var powerOnHours: Int? = nil
    var powerCycles: Int? = nil
    var bytesWritten: UInt64? = nil
    var bytesRead: UInt64? = nil
    var pending: Int64? = nil
    var uncorrectable: Int64? = nil
    var reallocated: Int64? = nil
    var crcErrors: Int64? = nil
    var lifePercent: Int? = nil
    var nvme: [String: Any] = [:]
    var attributes: [SmartAttribute] = []
    var selfTests: [SelfTestEntry] = []
    var selfTestRemaining: Int? = nil
    var supportsSelfTests = false
    var supportsConveyance = false
    var shortMinutes: Int? = nil
    var longMinutes: Int? = nil
    var rating: Rating = .unknown
    var reasons: [String] = []
}

enum HealthParser {
    static let attributeNames: [Int: String] = [
        1: "Lesefehlerrate", 2: "Durchsatzleistung", 3: "Anlaufzeit", 4: "Start/Stopp-Zähler",
        5: "Ersetzte Sektoren", 7: "Suchfehlerrate", 8: "Suchzeit-Leistung", 9: "Betriebsstunden",
        10: "Anlauf-Wiederholungen", 11: "Kalibrierungs-Wiederholungen", 12: "Einschaltvorgänge",
        13: "Soft-Lesefehlerrate", 160: "Unkorrigierbare Sektoren (Lesen/Schreiben)", 161: "Verfügbare Reserveblöcke",
        163: "Anfänglich defekte Blöcke", 164: "Gesamte Löschzyklen", 165: "Maximale Löschzyklen",
        166: "Minimale Löschzyklen", 167: "Durchschnittliche Löschzyklen", 168: "Maximale Löschzyklen (Spezifikation)",
        169: "Restlebensdauer", 170: "Verfügbare Reserveblöcke", 171: "Programmierfehler", 172: "Löschfehler",
        173: "Verschleißausgleich (Löschzyklen)", 174: "Unerwartete Stromausfälle", 175: "Stromausfallschutz",
        177: "Verschleißausgleich", 179: "Belegte Reserveblöcke", 180: "Freie Reserveblöcke",
        181: "Programmierfehler", 182: "Löschfehler", 183: "SATA-Herabstufungen", 184: "Ende-zu-Ende-Fehler",
        187: "Gemeldete unkorrigierbare Fehler", 188: "Befehls-Zeitüberschreitungen", 189: "Hohe Flughöhe",
        190: "Luftstromtemperatur", 191: "Erschütterungen", 192: "Notparkvorgänge", 193: "Lade-/Entladezyklen",
        194: "Temperatur", 195: "Hardware-ECC-Korrekturen", 196: "Umlagerungsereignisse",
        197: "Ausstehende Sektoren", 198: "Unkorrigierbare Sektoren", 199: "CRC-Fehler (Kabel/Adapter)",
        200: "Schreibfehlerrate", 201: "Soft-Lesefehler", 202: "Restlebensdauer", 206: "Flughöhe",
        220: "Plattenverschiebung", 222: "Belastete Stunden", 223: "Lade-/Entlade-Wiederholungen",
        224: "Lade-Reibung", 225: "Lade-/Entladezyklen", 226: "Ladezeit", 230: "Kopfamplitude",
        231: "Restlebensdauer", 232: "Verfügbare Reserve", 233: "Verschleißindikator", 234: "Durchschnittliche Löschzyklen",
        235: "Gute Blöcke / Systemblöcke", 240: "Kopf-Flugstunden", 241: "Geschriebene Daten", 242: "Gelesene Daten",
        246: "Geschriebene Daten (gesamt)", 247: "Geschriebene Seiten (Host)", 248: "Geschriebene Seiten (FTL)",
        249: "Geschriebene Daten (NAND, GiB)", 254: "Freifall-Schutz",
    ]

    static let vendors: [(String, String)] = [
        ("^WDC\\b", "Western Digital"), ("^WD_?BLACK", "Western Digital"), ("^WD\\d", "Western Digital"),
        ("^WD\\b", "Western Digital"), ("^Western Digital", "Western Digital"), ("^ST\\d", "Seagate"),
        ("^Seagate", "Seagate"), ("^TOSHIBA", "Toshiba"), ("^KIOXIA", "Kioxia"), ("^HGST", "HGST"),
        ("^(HUS|HUH|HUA)", "HGST"), ("^(Hitachi|HDS\\d|HTS\\d|HDT\\d)", "Hitachi"), ("^(SAMSUNG|Samsung)", "Samsung"),
        ("^(Crucial|CT\\d)", "Crucial"), ("^(Micron|MTFD)", "Micron"), ("^(INTEL|Intel|SSDSC|SSDPE)", "Intel"),
        ("^(KINGSTON|Kingston|SA400|SKC|SNV|SUV)", "Kingston"), ("^(SanDisk|SDSSD)", "SanDisk"),
        ("^(SK ?hynix|HFS\\d|HFM\\d)", "SK hynix"), ("^(ADATA|XPG)", "ADATA"), ("^(Transcend|TS\\d+G)", "Transcend"),
        ("^(APPLE|Apple)", "Apple"), ("^Maxtor", "Maxtor"), ("^(FUJITSU|Fujitsu|MHV|MHW|MHZ|MJA)", "Fujitsu"),
        ("^(Corsair|CSSD)", "Corsair"), ("^Patriot", "Patriot"), ("^PNY", "PNY"), ("^Lexar", "Lexar"),
        ("^Intenso", "Intenso"), ("^Verbatim", "Verbatim"), ("^Phison", "Phison"), ("^(TEAM|Team)", "Teamgroup"),
        ("^Netac", "Netac"), ("^(Silicon Power|SPCC)", "Silicon Power"), ("^(Gigabyte|GIGABYTE|GP-)", "Gigabyte"),
        ("^Sabrent", "Sabrent"), ("^(LITEON|LITE-ON|Lite-On)", "Lite-On"), ("^(Plextor|PLEXTOR)", "Plextor"),
        ("^OCZ", "OCZ"), ("^Lenovo", "Lenovo"), ("^(Dell|DELL)", "Dell"), ("^(HP\\b|HPE)", "HP"),
        ("^(Hikvision|HS-SSD)", "Hikvision"), ("^Mushkin", "Mushkin"), ("^(Goodram|GOODRAM|IR-SSD)", "Goodram"),
        ("^Inland", "Inland"), ("^Kingdian", "Kingdian"), ("^Fanxiang", "Fanxiang"), ("^(Biwin|BIWIN)", "Biwin"),
    ]
    static let vendorPrefixTokens: Set<String> = ["WDC", "WD", "TOSHIBA", "HGST", "Hitachi", "SAMSUNG", "Samsung", "INTEL",
        "KINGSTON", "Kingston", "SanDisk", "Crucial", "Micron", "APPLE", "Apple", "FUJITSU", "ADATA", "Transcend",
        "Seagate", "Corsair", "PNY", "Lexar", "Intenso", "Verbatim", "Patriot"]

    static func detectVendor(_ names: String...) -> String {
        for n in names where !n.isEmpty {
            let t = n.trimmingCharacters(in: .whitespaces)
            for (pattern, vendor) in vendors where t.range(of: pattern, options: .regularExpression) != nil {
                return vendor
            }
        }
        return ""
    }

    static func modelNumber(_ name: String) -> String {
        var parts = name.split(separator: " ").map(String.init)
        if parts.count > 1, vendorPrefixTokens.contains(parts[0]) { parts.removeFirst() }
        return parts.joined(separator: " ")
    }

    // MARK: JSON-Hilfen

    private static func dict(_ v: Any?) -> [String: Any] { v as? [String: Any] ?? [:] }
    private static func arr(_ v: Any?) -> [[String: Any]] { v as? [[String: Any]] ?? [] }
    private static func int(_ v: Any?) -> Int? { (v as? NSNumber).map { $0.intValue } }
    private static func i64(_ v: Any?) -> Int64? { (v as? NSNumber).map { $0.int64Value } }
    private static func str(_ v: Any?) -> String { ((v as? String) ?? "").trimmingCharacters(in: .whitespaces) }
    private static func bool(_ v: Any?) -> Bool? { (v as? NSNumber).map { $0.boolValue } }

    static func friendlyReason(_ msgs: [String]) -> String {
        let text = msgs.joined(separator: " ").lowercased()
        if text.contains("smartctl fehlt") {
            return "Das Diagnoseprogramm smartctl fehlt in dieser WipeKit-Version."
        }
        if text.contains("usb bridge") || text.contains("unsupported usb") || text.contains("please specify device type")
            || text.contains("unknown usb") {
            return "Das USB-Gehäuse bzw. der Adapter gibt die Gesundheitsdaten (S.M.A.R.T.) nicht weiter."
        }
        if text.contains("permission") || text.contains("operation not permitted") {
            return "Für das Auslesen der Gesundheitsdaten fehlen die nötigen Rechte."
        }
        if text.contains("smart support is: unavailable") || text.contains("smart support is: disabled")
            || text.contains("smart not supported") || text.contains("smart support is: ambiguous") {
            return "Das Laufwerk (bzw. die Verbindung) unterstützt keine Gesundheitsdaten (S.M.A.R.T.)."
        }
        if text.contains("no such device") || text.contains("open device") {
            return "Das Laufwerk konnte nicht geöffnet werden – ist es noch angeschlossen?"
        }
        return "Für dieses Laufwerk sind keine Gesundheitsdaten (S.M.A.R.T.) verfügbar."
    }

    static func parse(data: Data) -> Health {
        guard let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            var h = Health()
            h.reason = "Die Diagnosedaten konnten nicht gelesen werden."
            return h
        }
        return parse(obj)
    }

    static func parse(_ j: [String: Any]) -> Health {
        var h = Health()
        let dev = dict(j["device"])
        h.proto = str(dev["protocol"])
        h.model = str(j["model_name"]).isEmpty ? str(j["scsi_model_name"]) : str(j["model_name"])
        h.family = str(j["model_family"])
        h.vendor = detectVendor(h.family, h.model, str(j["scsi_vendor"]))
        h.modelNumber = modelNumber(h.model)
        h.serial = str(j["serial_number"])
        h.firmware = str(j["firmware_version"])
        h.capacity = UInt64(i64(dict(j["user_capacity"])["bytes"]) ?? i64(j["nvme_total_capacity"]) ?? 0)

        let nvme = dict(j["nvme_smart_health_information_log"])
        let table = arr(dict(j["ata_smart_attributes"])["table"])
        h.available = !nvme.isEmpty || !table.isEmpty || j["smart_status"] != nil
        if !h.available {
            let msgs = arr(dict(j["smartctl"])["messages"]).map { str($0["string"]) }
            h.reason = friendlyReason(msgs)
            return h
        }
        let block = UInt64(int(j["logical_block_size"]) ?? 512)
        if h.proto == "NVMe" {
            h.isSSD = true
        } else if let rr = int(j["rotation_rate"]) {
            h.isSSD = rr == 0
            h.rpm = rr > 0 ? rr : nil
        }
        h.formFactor = str(dict(j["form_factor"])["name"])
        h.sataVersion = str(dict(j["sata_version"])["string"])
        h.ataVersion = str(dict(j["ata_version"])["string"])
        let sp = dict(j["interface_speed"])
        h.interfaceMax = sataLabel(dict(sp["max"]))
        h.interfaceCurrent = sataLabel(dict(sp["current"]))
        if h.proto == "NVMe" {
            h.interface = "NVMe (PCIe)"
        } else if !h.interfaceMax.isEmpty {
            h.interface = h.interfaceMax
            if !h.interfaceCurrent.isEmpty && h.interfaceCurrent != h.interfaceMax {
                h.interface += " (aktuell \(h.interfaceCurrent))"
            }
        }
        if let t = j["trim"] as? [String: Any] { h.trim = bool(t["supported"]) }
        if let sec = j["ata_security"] as? [String: Any] {
            h.security = str(sec["string"])
            h.securityFrozen = bool(sec["frozen"])
        }
        h.smartPassed = bool(dict(j["smart_status"])["passed"])
        h.temperature = int(dict(j["temperature"])["current"])
        h.powerOnHours = int(dict(j["power_on_time"])["hours"])
        h.powerCycles = int(j["power_cycle_count"])

        var attrs: [Int: [String: Any]] = [:]
        for a in table {
            guard let id = int(a["id"]) else { continue }
            attrs[id] = a
            let raw = dict(a["raw"])
            let failed = str(a["when_failed"])
            let thresh = int(a["thresh"]) ?? 0
            let value = int(a["value"]) ?? 0
            var status = "OK"
            if failed == "now" || failed == "FAILING_NOW" {
                status = "Fehler"
            } else if failed == "past" || failed == "In_the_past" {
                status = "Warnung"
            } else if [5, 196, 197, 198].contains(id), let rv = i64(raw["value"]), rv > 0 {
                status = "Warnung"
            } else if thresh > 0 && value <= thresh {
                status = "Fehler"
            }
            h.attributes.append(SmartAttribute(
                id: id, name: attributeNames[id] ?? str(a["name"]).replacingOccurrences(of: "_", with: " "),
                value: value, worst: int(a["worst"]) ?? 0, thresh: thresh, raw: i64(raw["value"]),
                rawString: (raw["string"] as? String) ?? (i64(raw["value"]).map { String($0) } ?? ""),
                failed: failed, status: status))
        }
        func rawOf(_ id: Int) -> Int64? { i64(dict(attrs[id]?["raw"])["value"]) }
        func stat(_ name: String) -> Int64? {
            for page in arr(dict(j["ata_device_statistics"])["pages"]) {
                for e in arr(page["table"]) where str(e["name"]) == name {
                    if let v = i64(e["value"]) { return v }
                }
            }
            return nil
        }

        if !nvme.isEmpty {
            h.nvme = nvme
            if h.temperature == nil { h.temperature = int(nvme["temperature"]) }
            h.powerOnHours = int(nvme["power_on_hours"]) ?? h.powerOnHours
            h.powerCycles = int(nvme["power_cycles"]) ?? h.powerCycles
            if let w = i64(nvme["data_units_written"]) { h.bytesWritten = UInt64(w) &* 512_000 }
            if let r = i64(nvme["data_units_read"]) { h.bytesRead = UInt64(r) &* 512_000 }
            if let pu = int(nvme["percentage_used"]) { h.lifePercent = max(0, 100 - pu) }
            h.uncorrectable = i64(nvme["media_errors"])
        } else {
            h.pending = rawOf(197)
            h.uncorrectable = rawOf(198)
            h.reallocated = rawOf(5)
            h.crcErrors = rawOf(199)
            if let w = stat("Logical Sectors Written") { h.bytesWritten = UInt64(w) &* block }
            if let r = stat("Logical Sectors Read") { h.bytesRead = UInt64(r) &* block }
            if h.bytesWritten == nil {
                for id in [241, 246] {
                    if let a = attrs[id], let b = bytesFromLBA(a, block) { h.bytesWritten = b; break }
                }
            }
            if h.bytesRead == nil, let a = attrs[242] { h.bytesRead = bytesFromLBA(a, block) }
        }

        if h.lifePercent == nil && h.isSSD == true {
            if let eu = int(dict(j["endurance_used"])["current_percent"]) {
                h.lifePercent = max(0, 100 - eu)
            } else if let pu = stat("Percentage Used Endurance Indicator") {
                h.lifePercent = max(0, 100 - Int(pu))
            } else {
                for id in [231, 169, 202, 233, 177] {
                    if let v = int(attrs[id]?["value"]), v > 0, v <= 100 { h.lifePercent = v; break }
                }
            }
        }

        let data = dict(j["ata_smart_data"])
        let st = dict(data["self_test"])
        let caps = dict(data["capabilities"])
        h.supportsSelfTests = bool(caps["self_tests_supported"]) ?? false
        h.supportsConveyance = bool(caps["conveyance_self_test_supported"]) ?? false
        let pm = dict(st["polling_minutes"])
        h.shortMinutes = int(pm["short"])
        h.longMinutes = int(pm["extended"])
        let status = dict(st["status"])
        if let v = int(status["value"]), (v >> 4) == 0xF {
            h.selfTestRemaining = int(status["remaining_percent"]) ?? 100
        }
        for e in arr(dict(dict(j["ata_smart_self_test_log"])["standard"])["table"]).prefix(20) {
            let s = dict(e["status"])
            h.selfTests.append(SelfTestEntry(kind: testKind(str(dict(e["type"])["string"])),
                                             result: testResult(str(s["string"])),
                                             hours: int(e["lifetime_hours"]), passed: bool(s["passed"])))
        }
        let nlog = dict(j["nvme_self_test_log"])
        if let op = int(dict(nlog["current_self_test_operation"])["value"]), op != 0 {
            h.selfTestRemaining = 100 - (int(nlog["current_self_test_completion_percent"]) ?? 0)
        }
        for e in arr(nlog["table"]).prefix(20) {
            let r = dict(e["self_test_result"])
            h.selfTests.append(SelfTestEntry(kind: testKind(str(dict(e["self_test_code"])["string"])),
                                             result: testResult(str(r["string"])),
                                             hours: int(e["power_on_hours"]), passed: int(r["value"]) == 0))
        }
        rate(&h)
        return h
    }

    private static func sataLabel(_ speed: [String: Any]) -> String {
        if let ups = int(speed["units_per_second"]), let bpu = int(speed["bits_per_unit"]) {
            let gbit = Double(ups) * Double(bpu) / 1e9
            if abs(gbit - 1.5) < 0.2 { return "SATA 150" }
            if abs(gbit - 3.0) < 0.2 { return "SATA 300" }
            if abs(gbit - 6.0) < 0.2 { return "SATA 600" }
            return String(format: "%g Gbit/s", gbit)
        }
        switch str(speed["string"]) {
        case "1.5 Gb/s": return "SATA 150"
        case "3.0 Gb/s": return "SATA 300"
        case "6.0 Gb/s": return "SATA 600"
        default: return str(speed["string"])
        }
    }

    private static func bytesFromLBA(_ a: [String: Any], _ block: UInt64) -> UInt64? {
        let name = str(a["name"]).lowercased()
        guard let raw = i64(dict(a["raw"])["value"]), raw >= 0 else { return nil }
        let r = UInt64(raw)
        if name.contains("32mib") { return r &* 32 &* 1024 &* 1024 }
        if name.contains("gib") { return r &* 1024 &* 1024 &* 1024 }
        if name.contains("lba") { return r &* block }
        return nil
    }

    private static func testKind(_ s: String) -> String {
        let l = s.lowercased()
        if l.contains("short") { return "Kurztest" }
        if l.contains("extended") || l.contains("long") { return "Langtest" }
        if l.contains("conveyance") { return "Transporttest" }
        if l.contains("selective") { return "Selektiver Test" }
        return s.isEmpty ? "–" : s
    }

    private static func testResult(_ s: String) -> String {
        let l = s.lowercased()
        if l.contains("without error") { return "Ohne Fehler abgeschlossen" }
        if l.contains("progress") { return "Läuft" }
        if l.contains("aborted") || l.contains("interrupted") { return "Abgebrochen" }
        if l.contains("fail") || l.contains("error") { return "Fehlgeschlagen (\(s))" }
        return s.isEmpty ? "–" : s
    }

    static func rate(_ h: inout Health) {
        var bad: [String] = [], caution: [String] = [], good: [String] = []
        if h.smartPassed == false { bad.append("Das Laufwerk meldet selbst einen S.M.A.R.T.-Fehler.") }
        if let u = h.uncorrectable, u > 0 { bad.append("\(u) unkorrigierbare Sektoren/Medienfehler.") }
        if let cw = int(h.nvme["critical_warning"]), cw != 0 { bad.append("Die SSD meldet eine kritische Warnung.") }
        for a in h.attributes where a.failed == "now" || a.failed == "FAILING_NOW" || (a.thresh > 0 && a.value <= a.thresh) {
            bad.append("Attribut \(a.id) (\(a.name)) unter dem Grenzwert.")
        }
        if let l = h.lifePercent, l <= 5 { bad.append("Restlebensdauer nur noch \(l) %.") }
        if let p = h.pending, p > 0 { caution.append("\(p) ausstehende (instabile) Sektoren.") }
        if let r = h.reallocated, r >= 50 { caution.append("\(r) ersetzte Sektoren.") }
        if let l = h.lifePercent, l > 5, l <= 20 { caution.append("Restlebensdauer \(l) %.") }
        if let sp = int(h.nvme["available_spare"]), let th = int(h.nvme["available_spare_threshold"]), th > 0, sp < th {
            caution.append("Reservespeicher der SSD aufgebraucht.")
        }
        if let r = h.reallocated, r > 0, r < 50 { good.append("\(r) ersetzte Sektoren (unkritisch).") }
        if let l = h.lifePercent, l > 20, l < 90 { good.append("Restlebensdauer \(l) %.") }
        if h.attributes.contains(where: { $0.failed == "past" || $0.failed == "In_the_past" }) {
            good.append("Ein Attribut lag in der Vergangenheit unter dem Grenzwert.")
        }
        if let t = h.temperature, t >= 60 { good.append("Hohe Temperatur (\(t) °C).") }

        if !bad.isEmpty { h.rating = .bad; h.reasons = bad + caution }
        else if !caution.isEmpty { h.rating = .caution; h.reasons = caution + good }
        else if !good.isEmpty { h.rating = .good; h.reasons = good }
        else { h.rating = .veryGood; h.reasons = ["Keine Auffälligkeiten."] }
    }
}

// MARK: - Anzeige

enum HFmt {
    static func count<T: BinaryInteger>(_ v: T?) -> String {
        guard let v = v else { return "–" }
        let f = NumberFormatter()
        f.numberStyle = .decimal
        f.locale = Locale(identifier: "de_DE")
        return f.string(from: NSNumber(value: Int64(v))) ?? "\(v)"
    }

    static func hours(_ v: Int?) -> String {
        guard let v = v else { return "–" }
        let days = v / 24
        return days >= 2 ? "\(count(v)) Std. (ca. \(count(days)) Tage)" : "\(count(v)) Std."
    }

    static func rpm(_ h: Health) -> String {
        if let r = h.rpm { return "\(count(r)) U/min" }
        if h.isSSD == true { return "– (SSD, keine Drehzahl)" }
        return "–"
    }

    static func life(_ h: Health) -> String {
        guard let l = h.lifePercent else { return h.isSSD == true ? "– (nicht gemeldet)" : "– (nur bei SSDs)" }
        return "\(l) %"
    }

    static func temp(_ v: Int?) -> String { v.map { "\($0) °C" } ?? "–" }

    /// Dezimale Einheiten wie in der Linux-/Windows-Version (z. B. „31,4 TB“)
    static func bytes(_ b: UInt64) -> String {
        var v = Double(b)
        for unit in ["Bytes", "KB", "MB", "GB", "TB", "PB"] {
            if v < 1000 || unit == "PB" {
                if unit == "Bytes" { return "\(Int(v)) Bytes" }
                return v < 100 ? String(format: "%.1f %@", v, unit).replacingOccurrences(of: ".", with: ",")
                               : String(format: "%.0f %@", v, unit)
            }
            v /= 1000
        }
        return ""
    }

    static func bytesLong(_ b: UInt64?) -> String {
        guard let b = b else { return "–" }
        return "\(bytes(b)) (\(count(Int64(clamping: b))) Bytes)"
    }

    static func keyValues(_ h: Health) -> [(String, String)] {
        let nv = h.proto == "NVMe"
        let na = "– (bei NVMe nicht vorhanden)"
        return [
            ("Gesamtzustand", h.rating.rawValue),
            ("Restlebensdauer", life(h)),
            ("Temperatur", temp(h.temperature)),
            ("Stunden eingeschaltet", hours(h.powerOnHours)),
            ("Einschaltvorgänge (Power-on-Zyklen)", count(h.powerCycles)),
            ("Bytes geschrieben", bytesLong(h.bytesWritten)),
            ("Bytes gelesen", bytesLong(h.bytesRead)),
            ("Ausstehende Sektoren", nv && h.pending == nil ? na : count(h.pending)),
            ("Unkorrigierbare Sektoren" + (nv ? " (Medienfehler)" : ""), count(h.uncorrectable)),
            ("Ersetzte Sektoren", nv && h.reallocated == nil ? na : count(h.reallocated)),
            ("CRC-Fehler (Kabel/Adapter)", nv && h.crcErrors == nil ? na : count(h.crcErrors)),
            ("Drehzahl", rpm(h)),
            ("Schnittstelle", h.interface.isEmpty ? "–" : h.interface),
        ]
    }

    static func driveDetails(_ h: Health) -> [(String, String)] {
        var rows: [(String, String)] = [
            ("Modellreihe", h.family.isEmpty ? "–" : h.family),
            ("Modellnummer", h.model.isEmpty ? "–" : h.model),
            ("Firmware", h.firmware.isEmpty ? "–" : h.firmware),
            ("Kapazität", h.capacity > 0 ? bytesLong(h.capacity) : "–"),
            ("Bauart", h.isSSD == true ? "SSD" : (h.isSSD == false ? "Festplatte (HDD)" : "–")),
            ("Protokoll", h.proto.isEmpty ? "–" : h.proto),
        ]
        if !h.formFactor.isEmpty { rows.append(("Formfaktor", h.formFactor)) }
        if !h.ataVersion.isEmpty { rows.append(("ATA-Version", h.ataVersion)) }
        if !h.sataVersion.isEmpty { rows.append(("SATA-Version", h.sataVersion)) }
        if let t = h.trim { rows.append(("TRIM", t ? "unterstützt" : "nicht unterstützt")) }
        if !h.security.isEmpty {
            let s = h.security.uppercased()
            var parts = [s.contains("ENABLED") && !s.contains("DISABLED") ? "aktiviert" : "nicht aktiviert"]
            if h.securityFrozen == true { parts.append("eingefroren (frozen)") }
            rows.append(("ATA-Sicherheit", parts.joined(separator: ", ")))
        }
        rows.append(("S.M.A.R.T.-Selbstbewertung", h.smartPassed == true ? "Bestanden" : (h.smartPassed == false ? "NICHT bestanden" : "–")))
        return rows
    }

    static let nvmeLabels: [(String, String)] = [
        ("critical_warning", "Kritische Warnung"), ("available_spare", "Verfügbare Reserve (%)"),
        ("available_spare_threshold", "Reserve-Grenzwert (%)"), ("percentage_used", "Abnutzung (%)"),
        ("data_units_read", "Gelesene Dateneinheiten (je 512 000 Bytes)"),
        ("data_units_written", "Geschriebene Dateneinheiten (je 512 000 Bytes)"),
        ("host_reads", "Lesebefehle"), ("host_writes", "Schreibbefehle"), ("controller_busy_time", "Controller aktiv (Min.)"),
        ("power_cycles", "Einschaltvorgänge"), ("power_on_hours", "Betriebsstunden"),
        ("unsafe_shutdowns", "Unerwartete Abschaltungen"), ("media_errors", "Medien- und Datenfehler"),
        ("num_err_log_entries", "Einträge im Fehlerprotokoll"), ("warning_temp_time", "Zeit über Warntemperatur (Min.)"),
        ("critical_comp_time", "Zeit über kritischer Temperatur (Min.)"),
    ]

    static func nvmeRows(_ h: Health) -> [(String, String)] {
        nvmeLabels.compactMap { key, label in
            guard let v = h.nvme[key] else { return nil }
            if let n = v as? NSNumber { return (label, count(n.int64Value)) }
            return (label, "\(v)")
        }
    }
}
