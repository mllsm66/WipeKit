import Foundation

// MARK: - Schreibmuster

enum WipePattern: String, Codable {
    /// Kryptografisch erzeugte Zufallsdaten (nicht reproduzierbar)
    case random
    /// Reproduzierbare Zufallsdaten – können bei der Prüfung exakt verglichen werden
    case seeded
    /// Nullen
    case zeros

    var label: String {
        switch self {
        case .random, .seeded: return "Zufallsdaten schreiben"
        case .zeros:           return "Nullen schreiben"
        }
    }
}

// MARK: - Löschmodi

enum VerifyKind: String, Codable {
    case none, samples, full
}

enum WipeMode: String, Codable, CaseIterable, Identifiable {
    case quick, secure, maximum, ssd, purge, check

    var id: String { rawValue }

    var title: String {
        switch self {
        case .quick:   return "Schnell"
        case .secure:  return "Sicher"
        case .maximum: return "Maximal"
        case .ssd:     return "SSD"
        case .purge:   return "Firmware-Löschung"
        case .check:   return "Nur prüfen"
        }
    }

    var symbol: String {
        switch self {
        case .quick:   return "hare.fill"
        case .secure:  return "checkmark.shield.fill"
        case .maximum: return "lock.shield.fill"
        case .ssd:     return "memorychip"
        case .purge:   return "cpu"
        case .check:   return "doc.text.magnifyingglass"
        }
    }

    var tagline: String {
        switch self {
        case .quick:   return "1 Durchgang · Stichproben-Prüfung"
        case .secure:  return "2 Durchgänge · vollständige Prüfung"
        case .maximum: return "3 Durchgänge · vollständige Prüfung"
        case .ssd:     return "2 Durchgänge · Prüfung · TRIM"
        case .purge:   return "Löschbefehl des Laufwerks"
        case .check:   return "Liest alles · ändert nichts"
        }
    }

    /// Schreibdurchgänge in Reihenfolge
    var passes: [WipePattern] {
        switch self {
        case .quick:   return [.random]
        case .secure:  return [.random, .zeros]
        case .maximum: return [.random, .random, .zeros]
        case .ssd:     return [.random, .seeded]
        case .purge, .check: return []
        }
    }

    var verify: VerifyKind {
        switch self {
        case .quick, .purge: return .samples
        case .check: return .none
        default: return .full
        }
    }

    /// Nach dem letzten Durchgang wird die gesamte Platte gelesen und verglichen.
    var verifies: Bool { verify == .full }

    /// Ändert keine Daten
    var readOnly: Bool { self == .check }

    /// Am Ende alle Blöcke per TRIM/UNMAP an den Controller freigeben.
    var trims: Bool { self == .ssd }

    /// Unter macOS nicht verfügbar (Apple gibt die Befehle nicht frei)
    var availableOnMac: Bool { self != .purge }

    /// Lese-/Schreibarbeit in Vielfachen der Plattengröße (für Fortschritt und Zeitschätzung)
    var workUnits: Int { passes.count + (verifies || readOnly ? 1 : 0) }

    /// Angezeigte Schritte (inkl. Stichproben und TRIM)
    var stepCount: Int { max(1, passes.count + (verify != .none || readOnly ? 1 : 0) + (trims ? 1 : 0)) }

    var explanation: String {
        switch self {
        case .quick:
            return "Überschreibt jeden Sektor des Laufwerks einmal mit kryptografisch erzeugten Zufallsdaten. Anschließend werden Anfang, Ende und rund 2.000 zufällig verteilte Stellen gelesen und mit den geschriebenen Daten verglichen."
        case .secure:
            return "Überschreibt das Laufwerk zuerst mit Zufallsdaten und anschließend mit Nullen. Danach wird jeder Sektor gelesen und geprüft, ob er tatsächlich überschrieben wurde."
        case .maximum:
            return "Überschreibt das Laufwerk zweimal mit Zufallsdaten und einmal mit Nullen. Danach wird jeder Sektor gelesen und geprüft."
        case .ssd:
            return "Füllt die SSD zweimal vollständig mit Zufallsdaten und prüft den zweiten Durchgang Sektor für Sektor. Zum Schluss werden alle Speicherblöcke per TRIM an den SSD-Controller freigegeben, damit er sie intern löscht."
        case .purge:
            return "Lässt das Laufwerk sich selbst löschen (NVMe/ATA »Sanitize« oder »Secure Erase«). Dabei werden auch Reserve- und Verwaltungsbereiche gelöscht, die kein Programm von außen erreicht."
        case .check:
            return "Liest das gesamte Laufwerk und prüft, ob es leer ist (nur Nullen). Es werden keine Daten verändert. So lässt sich zum Beispiel eine Löschung mit einem anderen Programm kontrollieren. Nebenbei werden unlesbare Stellen gezählt und die Lesegeschwindigkeit aufgezeichnet."
        }
    }

    var bestFor: String {
        switch self {
        case .quick:   return "Festplatten (HDD), wenn es schnell gehen soll"
        case .secure:  return "Festplatten mit sensiblen Daten – die empfohlene Standardwahl"
        case .maximum: return "Aufträge, die mehrfaches Überschreiben ausdrücklich verlangen"
        case .ssd:     return "SSDs, USB-Sticks und Speicherkarten"
        case .purge:   return "Interne SATA- und NVMe-SSDs (unter Windows und Linux)"
        case .check:   return "Kontrolle eines bereits gelöschten oder neuen Laufwerks"
        }
    }

    /// 0 … 5
    var securityLevel: Int {
        switch self {
        case .quick:   return 3
        case .secure:  return 4
        case .maximum: return 4
        case .ssd:     return 3
        case .purge:   return 5
        case .check:   return 0
        }
    }

    var securityLabel: String {
        switch securityLevel {
        case 0: return "Keine Löschung"
        case 5: return "Höchste Stufe"
        case 4: return "Sehr hoch"
        default: return "Hoch"
        }
    }

    var securityNote: String {
        switch self {
        case .quick:
            return "Orientiert sich für Festplatten am Vorgehen »Clear« (NIST SP 800-88). Die Stichproben erkennen typische Fehler wie einen defekten USB-Adapter – eine Lücke an einer einzelnen Stelle kann aber nur die vollständige Prüfung der Modi »Sicher« und »Maximal« ausschließen."
        case .secure:
            return "Die Prüfung stellt sicher, dass kein Bereich ausgelassen wurde, und erkennt defekte Sektoren sowie unzuverlässige USB-Adapter."
        case .maximum:
            return "Bei modernen Festplatten nicht messbar sicherer als »Sicher«, erfüllt aber Vorgaben mit mehreren Durchgängen (angelehnt an DoD 5220.22-M)."
        case .ssd:
            return "Zweifaches Vollschreiben erreicht bei den meisten SSDs auch die versteckten Reservebereiche. Eine Garantie für diese Bereiche gibt nur die Firmware-Löschung, die macOS für Programme nicht freigibt."
        case .purge:
            return "macOS lässt diese Befehle für Programme nicht zu – weder für interne Laufwerke noch über USB oder Thunderbolt. Für SSDs ist hier »SSD« die beste Wahl. Die Firmware-Löschung bietet WipeKit unter Windows (NVMe) und Linux (SATA und NVMe) an."
        case .check:
            return "Nach »Sicher« und »Maximal« ist ein Laufwerk leer (nur Nullen). Nach »Schnell« und »SSD« enthält es Zufallsdaten und gilt deshalb hier als »nicht leer« – das ist dann kein Fehler."
        }
    }

    var methodDescription: String {
        switch self {
        case .quick:   return "1 Durchgang Zufallsdaten + Stichproben-Prüfung (Anfang, Ende, ca. 2.000 Stellen)"
        case .secure:  return "2 Durchgänge (Zufallsdaten, Nullen) + vollständige Lese-Verifikation"
        case .maximum: return "3 Durchgänge (Zufallsdaten, Zufallsdaten, Nullen) + vollständige Lese-Verifikation"
        case .ssd:     return "2 Durchgänge Zufallsdaten + vollständige Lese-Verifikation + TRIM aller Blöcke"
        case .purge:   return "Firmware-Löschbefehl des Laufwerks + Kontrolle von Prüfmarken"
        case .check:   return "Vollständiges Lesen aller Sektoren, keine Schreibzugriffe"
        }
    }

    var standard: String {
        let clear = "NIST SP 800-88 Rev. 2 – »Clear« (Überschreiben)"
        switch self {
        case .quick, .secure: return clear
        case .maximum: return clear + ", mehrfach (angelehnt an DoD 5220.22-M)"
        case .ssd:     return clear + " – für SSDs nach bestem Bemühen"
        case .purge:   return "NIST SP 800-88 Rev. 2 / IEEE 2883 – »Purge« (Firmware-Löschung)"
        case .check:   return "– (keine Löschung, nur Kontrolle)"
        }
    }

    static let notCertified = "WipeKit ist nicht zertifiziert und steht in keiner Verbindung zu NIST oder anderen Normungsstellen."

    /// Empfohlener Modus für ein Laufwerk
    static func recommended(for disk: ExternalDisk) -> WipeMode {
        disk.isSSD == true ? .ssd : .secure
    }
}

// MARK: - Status

enum JobState: String, Codable {
    case starting, running, done, failed, cancelled

    var isActive: Bool { self == .starting || self == .running }
}

/// Wird vom Lösch-Prozess (root) als status.json geschrieben und von der App gelesen.
struct HelperStatus: Codable {
    var state: JobState
    var pid: Int32
    var phase: String
    var step: Int
    var stepCount: Int
    var diskSize: UInt64
    var bytesDoneTotal: UInt64
    var bytesTotal: UInt64
    var speed: Double
    var startedAt: Date
    var updatedAt: Date
    var finishedAt: Date?
    var message: String?
    var verified: Bool?
    var trimResult: String?
    var sleepPrevented: Bool
    var sampleResult: String? = nil
    var hiddenArea: String? = nil
    var purgeMethod: String? = nil
    var nonZeroBytes: UInt64? = nil
    var badBlocks: Int? = nil
    var curve: [Double]? = nil
    /// Kennung, falls sich das Laufwerk neu angemeldet hat (USB-Direktzugriff)
    var diskNow: String? = nil
}

struct ExternalDisk: Identifiable, Hashable, Codable {
    let id: String          // z. B. "disk4"
    let name: String
    let size: UInt64
    let bus: String
    let isSSD: Bool?
    let volumes: [String]
    let isProtected: Bool
    var isInternal: Bool = false

    var typeLabel: String {
        switch isSSD {
        case .some(true):  return "SSD"
        case .some(false): return "Festplatte"
        case .none:        return "Typ unbekannt"
        }
    }

    var symbol: String {
        if isInternal { return "internaldrive" }
        return isSSD == true ? "externaldrive.fill" : "externaldrive"
    }

    /// Darf überschrieben werden (interne Laufwerke unter macOS nie – nur Diagnose)
    var canWipe: Bool { !isProtected && !isInternal }
}

extension ExternalDisk {
    enum CodingKeys: String, CodingKey { case id, name, size, bus, isSSD, volumes, isProtected, isInternal }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        id = try c.decode(String.self, forKey: .id)
        name = try c.decode(String.self, forKey: .name)
        size = try c.decode(UInt64.self, forKey: .size)
        bus = try c.decode(String.self, forKey: .bus)
        isSSD = try c.decodeIfPresent(Bool.self, forKey: .isSSD)
        volumes = try c.decode([String].self, forKey: .volumes)
        isProtected = try c.decode(Bool.self, forKey: .isProtected)
        isInternal = try c.decodeIfPresent(Bool.self, forKey: .isInternal) ?? false
    }
}

struct JobInfo: Codable {
    let id: UUID
    let disk: ExternalDisk
    let mode: WipeMode
    let createdAt: Date
    var purpose: String? = nil     // "scan" = Oberflächentest aus der Diagnose
}

enum SidebarItem: Hashable {
    case disk(String)
    case job(UUID)
}

// MARK: - Formatierung

enum Fmt {
    static func bytes(_ b: UInt64) -> String {
        ByteCountFormatter.string(fromByteCount: Int64(clamping: b), countStyle: .decimal)
    }

    static func exactBytes(_ b: UInt64) -> String {
        let f = NumberFormatter()
        f.numberStyle = .decimal
        f.locale = Locale(identifier: "de_DE")
        return (f.string(from: NSNumber(value: b)) ?? "\(b)") + " Bytes"
    }

    static func speed(_ s: Double) -> String {
        bytes(UInt64(max(0, s))) + "/s"
    }

    static func duration(_ t: TimeInterval) -> String {
        let s = Int(max(0, t.rounded()))
        let h = s / 3600, m = (s % 3600) / 60, sec = s % 60
        if h > 0 { return "\(h) Std. \(m) Min." }
        if m > 0 { return "\(m) Min. \(sec) Sek." }
        return "\(sec) Sek."
    }

    static func shortDuration(_ t: TimeInterval) -> String {
        let s = Int(max(0, t.rounded()))
        let h = s / 3600, m = (s % 3600) / 60
        if h > 0 { return "\(h) Std. \(m) Min." }
        if m > 0 { return "\(m) Min." }
        return "< 1 Min."
    }

    /// "14:05" (heute), "gestern", "Mo., 23.11."
    static func relativeDay(_ d: Date) -> String {
        let f = DateFormatter()
        f.locale = Locale(identifier: "de_DE")
        if Calendar.current.isDateInToday(d) { f.dateFormat = "HH:mm"; return f.string(from: d) }
        if Calendar.current.isDateInYesterday(d) { f.dateFormat = "'gestern,' HH:mm"; return f.string(from: d) }
        f.dateFormat = "EE, dd.MM."
        return f.string(from: d)
    }

    static func clock(_ d: Date) -> String {
        let f = DateFormatter()
        f.locale = Locale(identifier: "de_DE")
        if Calendar.current.isDateInToday(d) {
            f.dateFormat = "'heute,' HH:mm 'Uhr'"
        } else if Calendar.current.isDateInTomorrow(d) {
            f.dateFormat = "'morgen,' HH:mm 'Uhr'"
        } else {
            f.dateFormat = "EEEE, HH:mm 'Uhr'"
        }
        return f.string(from: d)
    }

    static func dateTime(_ d: Date) -> String {
        let f = DateFormatter()
        f.locale = Locale(identifier: "de_DE")
        f.dateFormat = "dd.MM.yyyy, HH:mm:ss 'Uhr'"
        return f.string(from: d)
    }

    static func percent(_ fraction: Double) -> String {
        let p = max(0, min(1, fraction)) * 100
        return p >= 10 || p == 0 ? "\(Int(p)) %" : String(format: "%.1f %%", p)
    }
}
