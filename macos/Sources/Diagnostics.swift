import Foundation
import AppKit

// S.M.A.R.T.-Daten über das mitgelieferte smartctl (smartmontools, GPL-2.0-or-later).
// smartctl ist ein eigenständiges Programm im App-Paket (Contents/MacOS/smartctl) und wird nur aufgerufen.

enum SmartService {
    static var smartctlPath: String? {
        let candidates = [
            Bundle.main.executableURL?.deletingLastPathComponent().appendingPathComponent("smartctl").path,
            "/usr/local/sbin/smartctl", "/opt/homebrew/sbin/smartctl", "/usr/local/bin/smartctl", "/opt/homebrew/bin/smartctl",
        ].compactMap { $0 }
        return candidates.first { FileManager.default.isExecutableFile(atPath: $0) }
    }

    static let missingJSON = Data(#"{"smartctl":{"messages":[{"string":"smartctl fehlt","severity":"error"}]}}"#.utf8)

    /// Liest die Gesundheitsdaten eines Laufwerks. Gibt die Auswertung und das rohe JSON zurück.
    static func read(_ diskID: String) -> (Health, Data) {
        guard DiskService.isValidWholeDiskID(diskID) else { return (HealthParser.parse(data: missingJSON), missingJSON) }
        guard let exe = smartctlPath else { return (HealthParser.parse(data: missingJSON), missingJSON) }
        var r = Shell.run(exe, ["-x", "-j", "/dev/\(diskID)"])
        var h = HealthParser.parse(data: r.out)
        if !h.available {
            let text = String(data: r.out, encoding: .utf8) ?? ""
            if text.contains("permissive") || text.contains("Ambiguous") {
                let r2 = Shell.run(exe, ["-x", "-j", "-T", "permissive", "/dev/\(diskID)"])
                let h2 = HealthParser.parse(data: r2.out)
                if h2.available { r = r2; h = h2 }
            }
        }
        return (h, r.out)
    }

    /// Liest mit Administratorrechten (Passwortabfrage) – für Laufwerke, die ohne Rechte nicht antworten.
    /// USB-Laufwerke werden dabei direkt über den Adapter gelesen (kurz ausgeworfen, siehe readViaUSB).
    static func readAsAdmin(_ disk: ExternalDisk) -> (Health, Data)? {
        guard DiskService.isValidWholeDiskID(disk.id) else { return nil }
        let exe = Bundle.main.executablePath ?? CommandLine.arguments[0]
        let cmd = [exe, "--usb-health", disk.id].map(AppModel.shellQuote).joined(separator: " ")
        let prompt = disk.bus == "USB"
            ? "WipeKit möchte die Gesundheitsdaten von »\(disk.name)« direkt über den USB-Adapter lesen. Dafür wird das Laufwerk kurz ausgeworfen und danach wieder verbunden. Es werden keine Daten verändert."
            : "WipeKit möchte die Gesundheitsdaten von »\(disk.name)« lesen. Es werden keine Daten verändert."
        let source = "do shell script \"\(AppModel.appleScriptEscape(cmd))\" with prompt \"\(AppModel.appleScriptEscape(prompt))\" with administrator privileges without altering line endings"
        // osascript statt NSAppleScript: läuft im Hintergrund, die Oberfläche bleibt bedienbar
        let r = Shell.run("/usr/bin/osascript", ["-e", source])
        guard r.status == 0, !r.out.isEmpty else { return nil }
        return (HealthParser.parse(data: r.out), r.out)
    }

    /// Für `WipeKit --usb-health diskN` (läuft als root): liest und gibt das JSON aus.
    static func runUSBHealthCommand(_ args: [String]) -> Int32 {
        guard let id = args.first, DiskService.isValidWholeDiskID(id) else { return 2 }
        let data: Data
        if DiskService.usbLocation(id) != nil {
            data = readViaUSB(id, forceUnmount: false).1
        } else {
            data = read(id).1
        }
        FileHandle.standardOutput.write(data)
        return 0
    }

    /// Weitere Adapter-Befehle, falls die Datenbank von smartmontools den Adapter nicht kennt
    static let usbFallbackTypes = ["sat", "sntjmicron", "sntasmedia", "sntrealtek", "usbjmicron",
                                   "usbcypress", "usbprolific", "usbsunplus", "sat,12"]

    /// Gesundheitsdaten direkt über den USB-Adapter (nur als root).
    /// macOS gibt diese Befehle über USB nicht weiter. Das mitgelieferte smartctl übernimmt deshalb
    /// das USB-Gerät kurz selbst („usbcap“) und gibt es danach an macOS zurück. Das Laufwerk wird
    /// dafür ausgeworfen und meldet sich anschließend neu an – eventuell unter neuer Kennung.
    /// Gibt Auswertung, JSON und die Kennung nach dem Neuanmelden zurück.
    static func readViaUSB(_ diskID: String, forceUnmount: Bool) -> (Health, Data, String?) {
        func fail(_ text: String) -> (Health, Data, String?) {
            let obj: [String: Any] = ["smartctl": ["messages": [["string": text, "severity": "error"]]],
                                      "wipekit_usbcap": ["tried": true]]
            let d = (try? JSONSerialization.data(withJSONObject: obj)) ?? missingJSON
            return (HealthParser.parse(data: d), d, diskID)
        }
        guard geteuid() == 0 else { return fail("requires administrator rights") }
        guard let exe = smartctlPath else { return fail("smartctl fehlt") }
        guard let loc = DiskService.usbLocation(diskID) else { return fail("not a USB drive") }
        let dev = String(format: "usbcap:0x%08x", loc)

        Shell.run("/usr/sbin/diskutil", ["unmountDisk"] + (forceUnmount ? ["force"] : []) + ["/dev/\(diskID)"])

        var best: Data?
        var attempts: [[String: Any]] = []
        for type in [nil] + usbFallbackTypes.map(Optional.some) {
            let args = ["-x", "-j"] + (type.map { ["-d", $0] } ?? []) + [dev]
            let r = Shell.run(exe, args, env: ["WIPEKIT_USBCAP_KEEP": "1"])
            let h = HealthParser.parse(data: r.out)
            let obj = (try? JSONSerialization.jsonObject(with: r.out)) as? [String: Any] ?? [:]
            let msgs = ((obj["smartctl"] as? [String: Any])?["messages"] as? [[String: Any]] ?? [])
                .compactMap { $0["string"] as? String }
            attempts.append(["type": type ?? "auto", "ok": h.available, "messages": msgs])
            if best == nil || h.available { best = r.out }
            if h.available { break }
            // Hat das Übernehmen selbst nicht geklappt, helfen andere Adapter-Befehle auch nicht
            let text = msgs.joined(separator: " ").lowercased()
            if ["mounted", "hand over", "administrator", "not found", "bulk-only", "not accessible"].contains(where: text.contains) { break }
        }
        // Gerät an macOS zurückgeben
        Shell.run(exe, ["-i", "-j", "-d", "scsi", dev])

        var after: String?
        for _ in 0..<40 {
            if let d = DiskService.wholeDisk(usbLocation: loc) { after = d; break }
            usleep(500_000)
        }
        var obj = ((try? JSONSerialization.jsonObject(with: best ?? missingJSON)) as? [String: Any]) ?? [:]
        obj["wipekit_usbcap"] = ["tried": true, "location": String(format: "0x%08x", loc),
                                 "attempts": attempts, "disk_after": after ?? ""] as [String: Any]
        let data = (try? JSONSerialization.data(withJSONObject: obj)) ?? (best ?? missingJSON)
        return (HealthParser.parse(data: data), data, after)
    }

    /// Startet einen Selbsttest (short, long, conveyance) oder bricht ihn ab (abort).
    static func selfTest(_ diskID: String, _ test: String) -> (Bool, String) {
        guard DiskService.isValidWholeDiskID(diskID), let exe = smartctlPath else { return (false, "smartctl fehlt.") }
        let args = test == "abort" ? ["-j", "-X", "/dev/\(diskID)"] : ["-j", "-t", test, "/dev/\(diskID)"]
        let r = Shell.run(exe, args)
        var msgs: [String] = []
        if let j = try? JSONSerialization.jsonObject(with: r.out) as? [String: Any],
           let sm = j["smartctl"] as? [String: Any], let m = sm["messages"] as? [[String: Any]] {
            msgs = m.compactMap { $0["string"] as? String }
        }
        let ok = r.status >= 0 && (r.status & 0b11) == 0 && !msgs.contains { $0.lowercased().contains("not supported") }
        return (ok, msgs.joined(separator: "; "))
    }
}

/// Gesundheitsdaten eines Laufwerks in der App
final class DiskHealth: ObservableObject {
    @Published var health: Health?
    @Published var raw: Data?
    @Published var readAt: Date?
    @Published var loading = false
    @Published var selfTestKind: String?      // läuft gerade (Kurztest …)
    @Published var selfTestMessage: String?

    private var timer: Timer?

    func stopPolling() { timer?.invalidate(); timer = nil }

    func startPolling(diskID: String, onUpdate: @escaping () -> Void) {
        timer?.invalidate()
        timer = Timer.scheduledTimer(withTimeInterval: 20, repeats: true) { [weak self] _ in
            guard let self = self else { return }
            DispatchQueue.global(qos: .utility).async {
                let (h, raw) = SmartService.read(diskID)
                DispatchQueue.main.async {
                    self.health = h
                    self.raw = raw
                    self.readAt = Date()
                    if h.selfTestRemaining == nil {
                        let last = h.selfTests.first
                        self.selfTestMessage = last.map { "\($0.kind): \($0.result)" }
                        self.selfTestKind = nil
                        self.stopPolling()
                        onUpdate()
                    }
                }
            }
        }
    }
}

enum MacInfo {
    static var osLabel: String {
        let v = ProcessInfo.processInfo.operatingSystemVersion
        return "macOS \(v.majorVersion).\(v.minorVersion)" + (v.patchVersion > 0 ? ".\(v.patchVersion)" : "")
    }

    static var host: String {
        Host.current().localizedName ?? ProcessInfo.processInfo.hostName
    }

}
