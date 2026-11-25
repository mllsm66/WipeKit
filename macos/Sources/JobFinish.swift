import Foundation

// MARK: - Abschluss eines Vorgangs
//
// Der Lösch-Prozess (root) erstellt die Protokolle selbst im Arbeitsordner, schickt die Benachrichtigung
// aufs Handy und zeigt eine Mitteilung, falls WipeKit geschlossen ist. Die App kopiert die Protokolle
// danach in den Berichte-Ordner. So klappt alles auch, wenn WipeKit während des Vorgangs zu ist.

/// Einstellungen für einen Vorgang – von der App beim Start in den Arbeitsordner geschrieben (nur für den
/// Benutzer lesbar, enthält ggf. Zugangsdaten), vom Lösch-Prozess gelesen und danach gelöscht.
struct JobSettings: Codable {
    var detailed = true
    var serialInFileName = true
    var meta: [[String]] = []
    var notifyLocal = true
    var playSound = true
    var eject = false
    var usbHealth = true
    var push = PushConfig()
    var host = ""
    var platform = ""
    var appVersion = ""
    var uid: UInt32 = 0

    static let fileName = "job-settings.json"

    static func current() -> JobSettings {
        let s = AppSettings.shared
        var j = JobSettings()
        j.detailed = s.detailedReport
        j.serialInFileName = s.serialInFileName
        j.meta = s.reportMeta.map { [$0.0, $0.1] }
        j.notifyLocal = s.notifyLocal
        j.playSound = s.playSound
        j.eject = s.ejectAfter
        j.usbHealth = s.usbHealth
        j.push = NotifySettings.shared.config
        j.host = MacInfo.host
        j.platform = MacInfo.osLabel
        j.appVersion = AppModel.appVersion
        j.uid = getuid()
        return j
    }

    func write(to dir: URL) {
        let url = dir.appendingPathComponent(JobSettings.fileName)
        guard let data = try? JSONEncoder().encode(self) else { return }
        FileManager.default.createFile(atPath: url.path, contents: data, attributes: [.posixPermissions: 0o600])
    }

    static func load(_ dir: URL) -> JobSettings? {
        guard let data = try? Data(contentsOf: dir.appendingPathComponent(fileName)) else { return nil }
        return try? JSONDecoder().decode(JobSettings.self, from: data)
    }

    var metaRows: [(String, String)] { meta.compactMap { $0.count == 2 ? ($0[0], $0[1]) : nil } }
}

/// Was der Lösch-Prozess am Ende erledigt hat
struct FinishInfo: Codable {
    struct File: Codable {
        var title: String
        var pdf: String
        var txt: String
    }
    var files: [File] = []
    var notifiedLocal = false
    var push: String?

    static let fileName = "finish.json"

    static func load(_ dir: URL) -> FinishInfo? {
        guard let data = try? Data(contentsOf: dir.appendingPathComponent(fileName)) else { return nil }
        return try? JSONDecoder().decode(FinishInfo.self, from: data)
    }
}

enum JobFinish {
    static func title(_ info: JobInfo) -> String {
        if info.mode.readOnly { return info.purpose == "scan" ? "Oberflächentest" : "Prüfung" }
        return "Löschen"
    }

    static func verificationText(_ mode: WipeMode, _ s: HelperStatus?) -> String {
        switch mode.verify {
        case .full:
            switch s?.verified {
            case .some(true):
                return mode.passes.last == .seeded
                    ? "bestanden – alle Sektoren gelesen und mit den geschriebenen Zufallsdaten verglichen"
                    : "bestanden – alle Sektoren gelesen, alle enthalten Nullen"
            case .some(false): return "NICHT bestanden"
            case .none: return "nicht durchgeführt"
            }
        case .samples:
            return s?.sampleResult ?? "nicht durchgeführt"
        case .none:
            return "nicht vorgesehen"
        }
    }

    /// Protokolle für einen Vorgang (Titel, Bericht)
    static func reports(info: JobInfo, status s: HelperStatus?, state: JobState, message: String?,
                        before: Health?, after: Health?, settings: JobSettings) -> [(String, Report)] {
        let d = info.disk
        let mode = info.mode
        var w = WipeRecord(modeTitle: mode.readOnly ? title(info) : mode.title, method: mode.methodDescription,
                           standard: mode.standard, state: state, started: s?.startedAt ?? info.createdAt,
                           ended: s?.finishedAt ?? Date())
        w.bytesWritten = s?.bytesDoneTotal ?? 0
        w.message = message ?? ""
        w.verification = verificationText(mode, s)
        w.trim = mode.trims ? (s?.trimResult ?? "nicht durchgeführt") : ""
        w.device = "/dev/\(s?.diskNow ?? d.id)"
        w.bus = d.bus + (d.isInternal ? " (intern)" : "")
        w.typeLabel = (after ?? before).flatMap { $0.isSSD }.map { $0 ? "SSD" : "Festplatte" } ?? d.typeLabel
        w.volumes = d.volumes
        w.exactSize = (s?.diskSize ?? 0) > 0 ? s!.diskSize : d.size
        w.hiddenArea = s?.hiddenArea ?? ""
        w.host = settings.host
        w.platform = settings.platform
        w.appVersion = settings.appVersion
        w.readOnly = mode.readOnly
        w.meta = settings.metaRows
        if mode.readOnly {
            w.badBlocks = s?.badBlocks
            w.nonZero = s?.nonZeroBytes
        }
        w.purgeMethod = s?.purgeMethod ?? ""
        let ident = Reports.identity(health: (before?.available == true ? before : nil) ?? after ?? before,
                                     model: d.name, size: d.size, serial: "")
        if mode.readOnly {
            return [("Prüfprotokoll", Reports.wipeReport(w, identity: ident, before: before, after: after, detailed: true))]
        }
        var out = [("Löschprotokoll", Reports.wipeReport(w, identity: ident, before: before, after: after, detailed: false))]
        if settings.detailed {
            out.append(("Löschprotokoll (ausführlich)", Reports.wipeReport(w, identity: ident, before: before, after: after, detailed: true)))
        }
        return out
    }

    /// Kurze Mitteilung (für Handy und Mitteilungszentrale)
    static func notice(info: JobInfo, state: JobState, message: String?, status s: HelperStatus?,
                       health: Health?) -> (title: String, body: String, warning: Bool) {
        let d = info.disk
        let name = Reports.identity(health: health, model: d.name, size: d.size, serial: "")
        let label = [name.vendor == "Unbekannt" ? "" : name.vendor, name.model].filter { !$0.isEmpty }.joined(separator: " ")
        let what = "\(label.isEmpty ? d.name : label) (\(Fmt.bytes(d.size)))"
        let t = title(info)
        let warning = health.map { $0.available && ($0.rating == .bad || $0.rating == .caution) } ?? false
        var body: String
        switch state {
        case .done:
            if info.mode.readOnly {
                if let b = s?.badBlocks, b > 0 { body = "\(what): \(b) unlesbare Bereiche gefunden" }
                else if info.purpose == "scan" { body = "\(what): Oberflächentest bestanden" }
                else { body = "\(what): " + (s?.nonZeroBytes == 0 ? "Laufwerk ist leer" : "Laufwerk enthält Daten") }
            } else {
                body = "\(what): erfolgreich gelöscht"
            }
        case .cancelled: body = "\(what): \(t) abgebrochen"
        default: body = "\(what): \(t) fehlgeschlagen" + ((message ?? "").isEmpty ? "" : " – \(message!.prefix(120))")
        }
        if warning, let h = health { body += " · Gesundheit: \(h.rating.rawValue)" }
        let title = state == .done ? (info.mode.readOnly ? "\(t) abgeschlossen" : "Löschen abgeschlossen")
                                   : (state == .cancelled ? "\(t) abgebrochen" : "\(t) fehlgeschlagen")
        return (title, body, warning)
    }

    /// Verschlüsselte Laufwerksdaten für den Hub
    static func hubMeta(info: JobInfo, state: JobState, status s: HelperStatus?, health: Health?, settings: JobSettings) -> [String: Any] {
        let d = info.disk
        let ident = Reports.identity(health: health, model: d.name, size: d.size, serial: "")
        let iso = ISO8601DateFormatter()
        var type = "unknown"
        if let h = health, h.available {
            type = h.proto == "NVMe" ? "nvme" : (h.isSSD == true ? "ssd" : (h.isSSD == false ? "hdd" : "unknown"))
        } else if d.isSSD == true { type = "ssd" } else if d.isSSD == false { type = "hdd" }
        if type == "unknown" && d.bus == "USB" && d.size < 600_000_000_000 { type = "usb" }
        let started = s?.startedAt ?? info.createdAt
        let ended = s?.finishedAt ?? Date()
        var m: [String: Any] = [
            "vendor": ident.vendor, "model": ident.model, "serial": ident.serial, "size": d.size, "type": type,
            "mode": info.mode.readOnly ? title(info) : info.mode.title, "method": info.mode.methodDescription,
            "result": state == .done ? (info.mode.readOnly ? "Abgeschlossen" : "Erfolgreich gelöscht")
                                     : (state == .cancelled ? "Abgebrochen" : "Fehlgeschlagen"),
            "rating": health?.rating.rawValue ?? "Unbekannt",
            "started": iso.string(from: started), "ended": iso.string(from: ended),
            "duration": Int(max(0, ended.timeIntervalSince(started))),
            "host": settings.host, "platform": settings.platform, "app": settings.appVersion,
        ]
        if let msg = s?.message, state != .done { m["message"] = msg }
        return m
    }
}
