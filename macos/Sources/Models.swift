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

enum WipeMode: String, Codable, CaseIterable, Identifiable {
    case quick, secure, maximum, ssd

    var id: String { rawValue }

    var title: String {
        switch self {
        case .quick:   return "Schnell"
        case .secure:  return "Sicher"
        case .maximum: return "Maximal"
        case .ssd:     return "SSD"
        }
    }

    var symbol: String {
        switch self {
        case .quick:   return "hare.fill"
        case .secure:  return "checkmark.shield.fill"
        case .maximum: return "lock.shield.fill"
        case .ssd:     return "memorychip"
        }
    }

    var tagline: String {
        switch self {
        case .quick:   return "1 Durchgang · ohne Prüfung"
        case .secure:  return "2 Durchgänge · mit Prüfung"
        case .maximum: return "3 Durchgänge · mit Prüfung"
        case .ssd:     return "2 Durchgänge · Prüfung · TRIM"
        }
    }

    /// Schreibdurchgänge in Reihenfolge
    var passes: [WipePattern] {
        switch self {
        case .quick:   return [.random]
        case .secure:  return [.random, .zeros]
        case .maximum: return [.random, .random, .zeros]
        case .ssd:     return [.random, .seeded]
        }
    }

    /// Nach dem letzten Durchgang wird die gesamte Platte gelesen und verglichen.
    var verifies: Bool { self != .quick }

    /// Am Ende alle Blöcke per TRIM/UNMAP an den Controller freigeben.
    var trims: Bool { self == .ssd }

    /// Lese-/Schreibarbeit in Vielfachen der Plattengröße (für Fortschritt und Zeitschätzung)
    var workUnits: Int { passes.count + (verifies ? 1 : 0) }

    /// Angezeigte Schritte (inkl. TRIM)
    var stepCount: Int { workUnits + (trims ? 1 : 0) }

    var explanation: String {
        switch self {
        case .quick:
            return "Überschreibt jeden Sektor des Laufwerks einmal mit kryptografisch erzeugten Zufallsdaten. Die bisherigen Daten lassen sich danach weder mit Software noch mit Labormethoden wiederherstellen."
        case .secure:
            return "Überschreibt das Laufwerk zuerst mit Zufallsdaten und anschließend mit Nullen. Danach wird jeder Sektor gelesen und geprüft, ob er tatsächlich überschrieben wurde."
        case .maximum:
            return "Überschreibt das Laufwerk zweimal mit Zufallsdaten und einmal mit Nullen. Danach wird jeder Sektor gelesen und geprüft."
        case .ssd:
            return "Füllt die SSD zweimal vollständig mit Zufallsdaten und prüft den zweiten Durchgang Sektor für Sektor. Zum Schluss werden alle Speicherblöcke per TRIM an den SSD-Controller freigegeben, damit er sie intern löscht."
        }
    }

    var bestFor: String {
        switch self {
        case .quick:   return "Festplatten (HDD), wenn es schnell gehen soll"
        case .secure:  return "Festplatten mit sensiblen Daten – die empfohlene Standardwahl"
        case .maximum: return "Aufträge, die mehrfaches Überschreiben ausdrücklich verlangen"
        case .ssd:     return "SSDs, USB-Sticks und Speicherkarten"
        }
    }

    /// 1 … 4
    var securityLevel: Int {
        switch self {
        case .quick:   return 3
        case .secure:  return 4
        case .maximum: return 4
        case .ssd:     return 3
        }
    }

    var securityLabel: String {
        securityLevel >= 4 ? "Sehr hoch" : "Hoch"
    }

    var securityNote: String {
        switch self {
        case .quick:
            return "Entspricht für Festplatten dem Standard NIST SP 800-88 »Clear«. Ob wirklich jeder Sektor beschrieben wurde, wird nicht kontrolliert."
        case .secure:
            return "Die Prüfung stellt sicher, dass kein Bereich ausgelassen wurde, und erkennt defekte Sektoren sowie unzuverlässige USB-Adapter."
        case .maximum:
            return "Bei modernen Festplatten nicht messbar sicherer als »Sicher«, erfüllt aber Vorgaben mit mehreren Durchgängen (angelehnt an DoD 5220.22-M)."
        case .ssd:
            return "Zweifaches Vollschreiben erreicht bei den meisten SSDs auch die versteckten Reservebereiche. Eine vollständige Garantie bietet nur das Firmware-Kommando »Secure Erase« des Herstellers, das unter macOS und über USB-Adapter nicht verfügbar ist."
        }
    }

    var methodDescription: String {
        switch self {
        case .quick:   return "1 Durchgang Zufallsdaten"
        case .secure:  return "2 Durchgänge (Zufallsdaten, Nullen) + vollständige Lese-Verifikation"
        case .maximum: return "3 Durchgänge (Zufallsdaten, Zufallsdaten, Nullen) + vollständige Lese-Verifikation"
        case .ssd:     return "2 Durchgänge Zufallsdaten + vollständige Lese-Verifikation + TRIM aller Blöcke"
        }
    }

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
}

struct ExternalDisk: Identifiable, Hashable, Codable {
    let id: String          // z. B. "disk4"
    let name: String
    let size: UInt64
    let bus: String
    let isSSD: Bool?
    let volumes: [String]
    let isProtected: Bool

    var typeLabel: String {
        switch isSSD {
        case .some(true):  return "SSD"
        case .some(false): return "Festplatte"
        case .none:        return "Typ unbekannt"
        }
    }

    var symbol: String {
        isSSD == true ? "externaldrive.fill" : "externaldrive"
    }
}

struct JobInfo: Codable {
    let id: UUID
    let disk: ExternalDisk
    let mode: WipeMode
    let createdAt: Date
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
