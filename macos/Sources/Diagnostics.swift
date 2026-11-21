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
    static func readAsAdmin(_ diskID: String) -> (Health, Data)? {
        guard DiskService.isValidWholeDiskID(diskID), let exe = smartctlPath else { return nil }
        let cmd = "'\(exe.replacingOccurrences(of: "'", with: "'\\''"))' -x -j /dev/\(diskID) || true"
        let source = "do shell script \"\(AppModel.appleScriptEscape(cmd))\" with prompt \"WipeKit möchte die Gesundheitsdaten von \(diskID) lesen. Es werden keine Daten verändert.\" with administrator privileges"
        var err: NSDictionary?
        guard let out = NSAppleScript(source: source)?.executeAndReturnError(&err).stringValue, err == nil else { return nil }
        let data = Data(out.utf8)
        return (HealthParser.parse(data: data), data)
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

    /// Erklärung, warum über USB keine Gesundheitsdaten kommen – und wie es trotzdem geht
    static let usbExplanation = "macOS leitet die Gesundheitsdaten (S.M.A.R.T.) über USB grundsätzlich nicht an Programme weiter – das ist eine Einschränkung von macOS, nicht des Laufwerks."
    static let usbWays: [(String, String)] = [
        ("Intern einbauen", "Interne SATA- und NVMe-Laufwerke liefern ihre Gesundheitsdaten immer."),
        ("Thunderbolt / USB4", "NVMe-SSDs in einem Thunderbolt- oder USB4-Gehäuse arbeiten wie interne Laufwerke – die Daten sind lesbar."),
        ("Treiber für USB (nur SATA)", "Der kostenlose Open-Source-Treiber »SAT SMART Driver« macht die Daten von SATA-Laufwerken in vielen USB-Gehäusen lesbar. Auf Macs mit Apple-Chip muss dafür die Sicherheitsstufe auf »Reduzierte Sicherheit« gesenkt werden – das ist ein Eingriff in den Systemschutz. NVMe-SSDs in USB-Gehäusen werden auch damit nicht unterstützt."),
        ("Windows oder Linux", "WipeKit für Windows und Linux liest die Daten über USB-Adapter direkt (ohne Treiber)."),
    ]
    static let satDriverURL = URL(string: "https://binaryfruit.com/drivedx/usb-drive-support")!
    static let satDriverSourceURL = URL(string: "https://github.com/kasbert/OS-X-SAT-SMART-Driver")!
}
