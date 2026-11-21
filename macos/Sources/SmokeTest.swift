import AppKit
import SwiftUI

/// Nur für die automatische Prüfung (CI): baut alle Ansichten mit Beispieldaten auf,
/// speichert Bildschirmfotos (WIPEKIT_SHOTS) und Berichte (WIPEKIT_REPORTS).
enum SmokeTest {
    @MainActor static func run() -> Int32 {
        AppModel.demoMode = true
        let env = ProcessInfo.processInfo.environment
        let app = NSApplication.shared
        app.setActivationPolicy(.regular)
        let model = AppModel.shared
        let fixtures = URL(fileURLWithPath: env["WIPEKIT_FIXTURES"] ?? "tests/fixtures", isDirectory: true)
        let shots = env["WIPEKIT_SHOTS"].map { URL(fileURLWithPath: $0, isDirectory: true) }
        if let s = shots { try? FileManager.default.createDirectory(at: s, withIntermediateDirectories: true) }
        var failures: [String] = []

        func fixture(_ name: String) -> Data {
            (try? Data(contentsOf: fixtures.appendingPathComponent("smartctl-\(name).json"))) ?? Data()
        }

        let disks = [
            ExternalDisk(id: "disk4", name: "WDC WD5000AAKX-00ERMA0", size: 500_107_862_016, bus: "USB", isSSD: false,
                         volumes: ["Backup"], isProtected: false),
            ExternalDisk(id: "disk5", name: "Samsung SSD 970 EVO Plus 1TB", size: 1_000_204_886_016, bus: "PCI-Express",
                         isSSD: true, volumes: ["Projekte"], isProtected: false),
            ExternalDisk(id: "disk6", name: "USB-Stick", size: 64_000_000_000, bus: "USB", isSSD: nil,
                         volumes: [], isProtected: false),
            ExternalDisk(id: "disk0", name: "APPLE SSD AP0512Q", size: 500_277_790_720, bus: "Apple Fabric", isSSD: true,
                         volumes: ["Macintosh HD"], isProtected: true, isInternal: true),
        ]
        model.disks = disks
        for (id, f) in [("disk4", "hdd"), ("disk5", "nvme"), ("disk6", "usb-unsupported"), ("disk0", "ssd")] {
            let e = model.healthModel(id)
            let raw = fixture(f)
            e.health = HealthParser.parse(data: raw)
            e.raw = raw
            e.readAt = Date()
        }
        if model.healthByDisk["disk4"]?.health?.rating != .good { failures.append("Bewertung HDD") }
        if model.healthByDisk["disk5"]?.health?.lifePercent != 96 { failures.append("Restlebensdauer NVMe") }

        // Fenster
        let hosting = NSHostingView(rootView: RootView().environmentObject(model))
        if #available(macOS 14.0, *) { hosting.sceneBridgingOptions = [.toolbars, .title] }
        let window = NSWindow(contentRect: NSRect(x: 80, y: 80, width: 1180, height: 820),
                              styleMask: [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView],
                              backing: .buffered, defer: false)
        window.title = "WipeKit"
        window.appearance = NSAppearance(named: .aqua)
        window.contentView = hosting
        window.makeKeyAndOrderFront(nil)
        app.activate(ignoringOtherApps: true)

        func spin(_ t: TimeInterval) { RunLoop.current.run(until: Date().addingTimeInterval(t)) }
        func shot(_ name: String) {
            spin(1.2)
            guard let dir = shots else { return }
            let view: NSView = window.contentView?.superview ?? hosting
            guard let rep = view.bitmapImageRepForCachingDisplay(in: view.bounds) else { return }
            view.cacheDisplay(in: view.bounds, to: rep)
            if let png = rep.representation(using: .png, properties: [:]) {
                try? png.write(to: dir.appendingPathComponent(name + ".png"))
            }
        }

        // Alle Laufwerke in beiden Ansichten
        for v in [MainView.wipe, .diag] {
            model.view = v
            for d in disks {
                model.selection = .disk(d.id)
                for m in WipeMode.allCases where v == .wipe { model.mode = m; spin(0.05) }
                spin(0.2)
            }
        }
        model.view = .wipe
        model.selection = .disk("disk4")
        model.mode = .secure
        shot("macos-1-loeschen")
        model.view = .diag
        shot("macos-2-diagnose")
        model.selection = .disk("disk5")
        shot("macos-3-diagnose-ssd")
        model.selection = .disk("disk6")
        shot("macos-4-usb-hinweis")

        // Vorgänge
        let tmp = FileManager.default.temporaryDirectory.appendingPathComponent("wipekit-smoke-\(getpid())")
        let cases: [(WipeMode, JobState, String?)] = [(.ssd, .running, nil), (.secure, .done, nil), (.quick, .failed, nil),
                                                      (.check, .done, "scan"), (.check, .running, "scan"), (.maximum, .cancelled, nil)]
        for (mode, state, purpose) in cases {
            let info = JobInfo(id: UUID(), disk: disks[0], mode: mode, createdAt: Date().addingTimeInterval(-3600), purpose: purpose)
            let dir = tmp.appendingPathComponent(info.id.uuidString)
            try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
            try? JSONEncoder().encode(info).write(to: dir.appendingPathComponent("job.json"))
            try? fixture("hdd").write(to: dir.appendingPathComponent("smart-before.json"))
            try? fixture("hdd").write(to: dir.appendingPathComponent("smart-after.json"))
            var st = HelperStatus(state: state, pid: getpid(), phase: state == .running ? "Durchgang 1: Zufallsdaten schreiben" : "",
                                  step: 1, stepCount: mode.stepCount, diskSize: disks[0].size,
                                  bytesDoneTotal: 400_000_000_000, bytesTotal: 1_500_321_586_048, speed: 142_000_000,
                                  startedAt: Date().addingTimeInterval(-3000), updatedAt: Date(),
                                  finishedAt: state.isActive ? nil : Date(), message: state == .failed ? "Testfehler" : "Testmeldung",
                                  verified: state == .done ? true : nil, trimResult: nil, sleepPrevented: state.isActive)
            if mode == .check {
                let count = state == .running ? 90 : 200
                st.curve = (0..<count).map { (i: Int) -> Double in
                    let base = 160.0 - Double(i) * 0.35
                    return i % 7 == 0 ? base - 12.0 : base
                }
                st.badBlocks = 0
                st.nonZeroBytes = 0
                st.phase = state == .running ? "Alle Sektoren lesen" : ""
            }
            if mode == .quick { st.sampleResult = "bestanden – 2080 Stellen gelesen und verglichen" }
            try? JSONEncoder().encode(st).write(to: dir.appendingPathComponent("status.json"))
            let job = WipeJob(info: info, dir: dir)
            model.attach(job)
            model.jobs.insert(job, at: 0)
            job.markLaunched(at: info.createdAt)
            model.selection = .job(job.id)
            spin(0.3)
            if !state.isActive && job.reports["pdf"] == nil { failures.append("Kein Protokoll für \(mode.rawValue)/\(state.rawValue): \(job.reports)") }
            if mode == .ssd && state == .running { shot("macos-5-fortschritt") }
            if mode == .secure && state == .done { shot("macos-6-fertig") }
            if mode == .check && state == .done { shot("macos-7-oberflaechentest") }
        }

        // Gesundheitsberichte für alle Beispieldaten
        for d in disks {
            let h = model.healthByDisk[d.id]?.health
            let r = Reports.healthReport(h, identity: Reports.identity(health: h, model: d.name, size: d.size, serial: ""),
                                         appVersion: AppModel.appVersion, platform: MacInfo.osLabel, host: "CI")
            do {
                let (pdf, txt) = try Reports.save(r)
                let data = try Data(contentsOf: pdf)
                if !data.starts(with: Data("%PDF-1.4".utf8)) { failures.append("PDF-Kopf \(d.id)") }
                let text = try String(contentsOf: txt, encoding: .utf8)
                if !text.contains("Gesamtzustand") { failures.append("Text \(d.id)") }
                print("Bericht:", pdf.lastPathComponent)
            } catch {
                failures.append("Bericht \(d.id): \(error)")
            }
        }
        let name = Reports.baseName(Reports.identity(health: model.healthByDisk["disk4"]?.health, model: "", size: 0, serial: ""),
                                    Date(timeIntervalSince1970: 0))
        if !name.hasSuffix("_WESTERN-DIGITAL-WD5000AAKX-00ERMA0-500GB-WD-WCC2EXX12345") { failures.append("Dateiname: \(name)") }

        for j in model.jobs { j.stop() }
        if failures.isEmpty {
            print("SMOKE TEST OK")
            return 0
        }
        print("SMOKE TEST FEHLGESCHLAGEN:\n" + failures.joined(separator: "\n"))
        return 1
    }
}
