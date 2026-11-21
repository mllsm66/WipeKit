import Foundation
import AppKit

struct AlertItem: Identifiable {
    let id = UUID()
    let title: String
    let message: String
}

// MARK: - Ein Löschvorgang (App-Seite)

final class WipeJob: ObservableObject, Identifiable {
    let info: JobInfo
    let dir: URL
    var id: UUID { info.id }

    @Published private(set) var status: HelperStatus?
    @Published private(set) var state: JobState = .starting
    @Published private(set) var message: String?
    @Published var protocolURL: URL?
    @Published var reports: [String: String] = [:]
    @Published private(set) var cancelRequested = false
    @Published private(set) var now = Date()
    @Published var askCancel = false

    var alreadyReported = false
    var onFinish: ((WipeJob) -> Void)?

    private var timer: Timer?
    private var launchedAt: Date?
    private var finishedHandled = false

    init(info: JobInfo, dir: URL) {
        self.info = info
        self.dir = dir
    }

    var mode: WipeMode { info.mode }
    var disk: ExternalDisk { info.disk }

    enum Kind { case wipe, check }
    var kind: Kind { info.mode.readOnly ? .check : .wipe }

    var title: String {
        if kind == .check { return info.purpose == "scan" ? "Oberflächentest" : "Prüfung" }
        return "Löschen"
    }

    var doneText: String {
        if kind == .wipe { return "Erfolgreich gelöscht" }
        if let b = status?.badBlocks, b > 0 { return "Unlesbare Bereiche gefunden" }
        if info.purpose == "scan" { return "Alle Sektoren lesbar" }
        return status?.nonZeroBytes == 0 ? "Laufwerk ist leer" : "Laufwerk enthält Daten"
    }

    var foundProblem: Bool { kind == .check && state == .done && (status?.badBlocks ?? 0) > 0 }

    var fraction: Double {
        guard let s = status, s.bytesTotal > 0 else { return state == .done ? 1 : 0 }
        return min(1, Double(s.bytesDoneTotal) / Double(s.bytesTotal))
    }

    /// Geschätzte Restzeit in Sekunden
    var eta: TimeInterval? {
        guard let s = status, s.state == .running, s.speed > 1, s.bytesTotal > s.bytesDoneTotal else { return nil }
        guard now.timeIntervalSince(s.startedAt) > 15 else { return nil }   // erst messen
        return Double(s.bytesTotal - s.bytesDoneTotal) / s.speed
    }

    var elapsed: TimeInterval? {
        guard let s = status, s.state != .starting else { return nil }
        return (s.finishedAt ?? now).timeIntervalSince(s.startedAt)
    }

    func markLaunched(at date: Date = Date()) {
        launchedAt = date
        timer?.invalidate()
        timer = Timer.scheduledTimer(withTimeInterval: 1.0, repeats: true) { [weak self] _ in self?.poll() }
        poll()
    }

    func requestCancel() {
        cancelRequested = true
        FileManager.default.createFile(atPath: dir.appendingPathComponent("cancel").path, contents: Data())
    }

    func stop() {
        timer?.invalidate()
        timer = nil
    }

    private func poll() {
        now = Date()
        let url = dir.appendingPathComponent("status.json")
        if let data = try? Data(contentsOf: url),
           let s = try? JSONDecoder().decode(HelperStatus.self, from: data) {
            status = s
            state = s.state
            message = s.message
            if s.state.isActive && !WipeJob.pidAlive(s.pid) && now.timeIntervalSince(s.updatedAt) > 10 {
                state = .failed
                message = "Der Arbeitsprozess wurde unerwartet beendet (Absturz oder Neustart?)."
                    + (kind == .wipe ? " Das Laufwerk ist nur teilweise überschrieben." : "") + logTail()
            }
        } else if let l = launchedAt, now.timeIntervalSince(l) > 30 {
            state = .failed
            message = "Der Arbeitsprozess konnte nicht gestartet werden." + logTail()
        }
        if !state.isActive { finish() }
    }

    private func finish() {
        guard !finishedHandled else { return }
        finishedHandled = true
        stop()
        onFinish?(self)
    }

    /// Letzte Zeilen aus helper.log für Fehlermeldungen.
    func logTail() -> String {
        guard let text = try? String(contentsOf: dir.appendingPathComponent("helper.log"), encoding: .utf8) else {
            return "\n\n(Kein Protokoll des Löschprozesses vorhanden – er wurde vermutlich gar nicht gestartet.)"
        }
        let lines = text.split(separator: "\n").suffix(6).joined(separator: "\n")
        return lines.isEmpty ? "" : "\n\nDetails:\n" + lines
    }

    static func pidAlive(_ pid: Int32) -> Bool {
        guard pid > 0 else { return false }
        return kill(pid, 0) == 0 || errno == EPERM   // EPERM = läuft, gehört aber root
    }
}

// MARK: - App-Zustand

enum MainView: String, CaseIterable, Identifiable {
    case wipe, diag
    var id: String { rawValue }
    var title: String { self == .wipe ? "Löschen" : "Diagnose" }
    var symbol: String { self == .wipe ? "trash" : "stethoscope" }
}

final class AppModel: ObservableObject {
    static let shared = AppModel()

    @Published var disks: [ExternalDisk] = []
    @Published var view: MainView = .wipe {
        didSet { if view == .diag, let d = selectedDisk { ensureHealth(d) } }
    }
    /// Nur für den automatischen Oberflächentest (CI): keine echten Laufwerke einlesen
    static var demoMode = false
    @Published var healthByDisk: [String: DiskHealth] = [:]
    @Published var selection: SidebarItem? {
        didSet {
            // Passenden Modus vorschlagen, wenn ein anderes Laufwerk gewählt wird
            guard selection != oldValue, let d = selectedDisk else { return }
            if d.isSSD == true { mode = .ssd } else if mode == .ssd { mode = .secure }
            if view == .diag { ensureHealth(d) }
        }
    }
    @Published var mode: WipeMode = .secure
    @Published var jobs: [WipeJob] = []
    @Published var isRefreshing = false
    @Published var showConfirm = false
    @Published var alertItem: AlertItem? {
        didSet { if alertItem == nil { showNextAlertSoon() } }
    }

    private var pendingAlerts: [AlertItem] = []
    private var watcher: DiskWatcher?
    private var refreshScheduled = false
    private var pendingDisk: ExternalDisk?
    private let baseDir: URL

    var selectedDisk: ExternalDisk? {
        if case .disk(let id)? = selection { return disks.first { $0.id == id } }
        return nil
    }

    var selectedJob: WipeJob? {
        if case .job(let id)? = selection { return jobs.first { $0.id == id } }
        return nil
    }
    var hasActiveJobs: Bool { jobs.contains { $0.state.isActive } }

    static var appVersion: String {
        (Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String) ?? "dev"
    }

    static var protocolFolder: URL? { Reports.folder }

    private init() {
        baseDir = URL(fileURLWithPath: "/private/tmp/WipeKit-\(getuid())", isDirectory: true)
        try? FileManager.default.createDirectory(at: baseDir, withIntermediateDirectories: true,
                                                 attributes: [.posixPermissions: 0o700])
        if AppModel.demoMode { return }
        restoreJobs()
        refresh()
        watcher = DiskWatcher { [weak self] in self?.scheduleRefresh() }
    }

    func isBusy(_ disk: ExternalDisk) -> Bool {
        activeJob(for: disk) != nil
    }

    func activeJob(for disk: ExternalDisk) -> WipeJob? {
        jobs.first { $0.info.disk.id == disk.id && $0.state.isActive }
    }

    // MARK: Laufwerke

    func refresh() {
        isRefreshing = true
        DispatchQueue.global(qos: .userInitiated).async {
            let list = DiskService.listDisks()
            DispatchQueue.main.async {
                self.disks = list
                for d in list where self.healthByDisk[d.id]?.health == nil { self.ensureHealth(d) }
                self.isRefreshing = false
                if case .disk(let id)? = self.selection, !list.contains(where: { $0.id == id }) {
                    self.selection = nil
                }
                if self.selection == nil,
                   let first = list.first(where: { $0.canWipe && !self.isBusy($0) }) ?? list.first {
                    self.selection = .disk(first.id)
                }
            }
        }
    }

    /// Beim Anstecken feuern viele Ereignisse (je Partition) – kurz sammeln.
    private func scheduleRefresh() {
        guard !refreshScheduled else { return }
        refreshScheduled = true
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.0) {
            self.refreshScheduled = false
            self.refresh()
        }
    }

    // MARK: Löschen starten

    func askConfirm(_ disk: ExternalDisk) {
        pendingDisk = disk
        showConfirm = true
    }

    var pendingDiskForConfirm: ExternalDisk? { pendingDisk }

    func confirmStart() {
        guard let d = pendingDisk else { return }
        pendingDisk = nil
        requestStart(disk: d, mode: mode)
    }

    func requestStart(disk: ExternalDisk, mode: WipeMode, purpose: String? = nil) {
        // Dialog erst schließen lassen, dann Passwortabfrage
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.4) {
            self.start(disk: disk, mode: mode, purpose: purpose)
        }
    }

    private func start(disk: ExternalDisk, mode: WipeMode, purpose: String? = nil) {
        guard mode.readOnly || disk.canWipe, mode.availableOnMac else { return }
        if (Bundle.main.executablePath ?? "").contains("/AppTranslocation/") {
            enqueueAlert("Bitte WipeKit in »Programme« bewegen",
                         "macOS führt WipeKit gerade aus einem geschützten Zwischenordner aus. Bitte beende WipeKit, ziehe die App in den Ordner »Programme« und starte sie von dort.")
            return
        }
        guard !isBusy(disk) else {
            enqueueAlert("Läuft bereits", "Auf \(disk.id) läuft bereits ein Vorgang.")
            return
        }

        let info = JobInfo(id: UUID(), disk: disk, mode: mode, createdAt: Date(), purpose: purpose)
        let dir = baseDir.appendingPathComponent(info.id.uuidString, isDirectory: true)
        do {
            try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true,
                                                    attributes: [.posixPermissions: 0o700])
            try JSONEncoder().encode(info).write(to: dir.appendingPathComponent("job.json"))
        } catch {
            enqueueAlert("Fehler", "Arbeitsordner konnte nicht angelegt werden: \(error.localizedDescription)")
            return
        }

        let job = WipeJob(info: info, dir: dir)
        attach(job)
        jobs.insert(job, at: 0)
        selection = .job(job.id)

        let exe = Bundle.main.executablePath ?? CommandLine.arguments[0]
        let parts = [exe, "--wipe-helper",
                     "--disk", disk.id,
                     "--mode", mode.rawValue,
                     "--job-dir", dir.path,
                     "--expected-size", String(disk.size)]
        // Der Starter übergibt den Löschvorgang an launchd und kehrt sofort zurück
        let command = parts.map(AppModel.shellQuote).joined(separator: " ") + " 2>&1"
        let prompt = mode.readOnly
            ? "WipeKit möchte »\(disk.name)« (\(disk.id), \(Fmt.bytes(disk.size))) vollständig lesen und prüfen. Es werden keine Daten verändert."
            : "WipeKit möchte »\(disk.name)« (\(disk.id), \(Fmt.bytes(disk.size))) unwiderruflich löschen."
        let source = "do shell script \"\(AppModel.appleScriptEscape(command))\" with prompt \"\(AppModel.appleScriptEscape(prompt))\" with administrator privileges"

        var errorInfo: NSDictionary?
        let script = NSAppleScript(source: source)
        let output = script?.executeAndReturnError(&errorInfo)
        if let out = output?.stringValue, !out.isEmpty {
            NSLog("WipeKit-Starter: %@", out)
        }

        if let e = errorInfo {
            let code = (e[NSAppleScript.errorNumber] as? Int) ?? 0
            remove(job)
            if code != -128 {   // -128 = Benutzer hat "Abbrechen" gedrückt
                let text = (e[NSAppleScript.errorMessage] as? String) ?? "Unbekannter Fehler (\(code))"
                enqueueAlert("Vorgang konnte nicht gestartet werden", text)
            }
            return
        }
        job.markLaunched()
    }

    func remove(_ job: WipeJob) {
        guard !job.state.isActive || job.status == nil else { return }
        job.stop()
        if selection == .job(job.id) {
            selection = nil
            if let d = disks.first(where: { $0.canWipe && !isBusy($0) }) ?? disks.first { selection = .disk(d.id) }
        }
        jobs.removeAll { $0.id == job.id }
        try? FileManager.default.removeItem(at: job.dir)
    }

    func removeFinished() {
        for j in jobs where !j.state.isActive { remove(j) }
    }

    // MARK: Wiederherstellen nach Neustart der App

    private func restoreJobs() {
        let fm = FileManager.default
        guard let items = try? fm.contentsOfDirectory(at: baseDir, includingPropertiesForKeys: nil) else { return }
        var restored: [WipeJob] = []
        for dir in items {
            guard let data = try? Data(contentsOf: dir.appendingPathComponent("job.json")),
                  let info = try? JSONDecoder().decode(JobInfo.self, from: data) else { continue }
            guard fm.fileExists(atPath: dir.appendingPathComponent("status.json").path) else {
                if Date().timeIntervalSince(info.createdAt) > 120 { try? fm.removeItem(at: dir) }
                continue
            }
            let job = WipeJob(info: info, dir: dir)
            if let data = try? Data(contentsOf: dir.appendingPathComponent("reports.json")),
               let rep = try? JSONDecoder().decode([String: String].self, from: data) {
                job.reports = rep
                if let p = rep["pdf"] { job.protocolURL = URL(fileURLWithPath: p) }
                job.alreadyReported = true
            } else if let p = try? String(contentsOf: dir.appendingPathComponent("protocol_path"), encoding: .utf8) {
                job.protocolURL = URL(fileURLWithPath: p)
                job.alreadyReported = true
            }
            attach(job)
            restored.append(job)
        }
        jobs = restored.sorted { $0.info.createdAt > $1.info.createdAt }
        // Einen noch laufenden Vorgang direkt anzeigen
        for job in jobs {
            if let data = try? Data(contentsOf: job.dir.appendingPathComponent("status.json")),
               let st = try? JSONDecoder().decode(HelperStatus.self, from: data),
               st.state.isActive, WipeJob.pidAlive(st.pid) {
                selection = .job(job.id)
                break
            }
        }
        // erst nach dem Start der Oberfläche weiter überwachen
        DispatchQueue.main.async {
            for job in self.jobs { job.markLaunched(at: job.info.createdAt) }
        }
    }

    func attach(_ job: WipeJob) {
        job.onFinish = { [weak self] j in self?.jobFinished(j) }
    }

    // MARK: Fertig

    private func jobFinished(_ job: WipeJob) {
        guard !job.alreadyReported else { return }
        job.alreadyReported = true

        if let h = loadHealth(job.dir.appendingPathComponent("smart-after.json")), h.available {
            let entry = healthModel(job.disk.id)
            entry.health = h
            entry.readAt = Date()
        }
        let rep = writeReports(for: job)
        job.reports = rep
        if let p = rep["pdf"] { job.protocolURL = URL(fileURLWithPath: p) }
        if let data = try? JSONEncoder().encode(rep) {
            try? data.write(to: job.dir.appendingPathComponent("reports.json"))
        }

        let d = job.info.disk
        switch job.state {
        case .done:
            NSSound(named: NSSound.Name(job.foundProblem ? "Basso" : "Glass"))?.play()
            var text = job.kind == .wipe
                ? "\(d.name) (\(d.id), \(Fmt.bytes(d.size))) wurde erfolgreich gelöscht."
                : "\(d.name): \(job.message ?? job.doneText)"
            if let e = job.elapsed { text += "\nDauer: \(Fmt.duration(e))" }
            if job.kind == .wipe, job.status?.verified == true {
                text += job.mode.verify == .full ? "\nPrüfung: bestanden – jeder Sektor ist überschrieben."
                                                 : "\nKontrolle: \(job.status?.sampleResult ?? "bestanden")"
            }
            if job.info.mode.trims, let t = job.status?.trimResult { text += "\nTRIM: \(t)" }
            enqueueAlert(job.kind == .wipe ? "Löschen abgeschlossen" : "\(job.title) abgeschlossen", text)
        case .cancelled:
            enqueueAlert("\(job.title) abgebrochen", "\(d.name) (\(d.id)): \(job.message ?? "")")
        default:
            NSSound(named: NSSound.Name("Basso"))?.play()
            enqueueAlert("\(job.title) fehlgeschlagen", "\(d.name) (\(d.id)): \(job.message ?? "Unbekannter Fehler")")
        }
        NSApplication.shared.requestUserAttention(.criticalRequest)
        objectWillChange.send()
    }

    func loadHealth(_ url: URL) -> Health? {
        guard let data = try? Data(contentsOf: url), !data.isEmpty else { return nil }
        return HealthParser.parse(data: data)
    }

    private func writeReports(for job: WipeJob) -> [String: String] {
        let d = job.info.disk
        let s = job.status
        let started = s?.startedAt ?? job.info.createdAt
        let ended = s?.finishedAt ?? Date()
        let before = loadHealth(job.dir.appendingPathComponent("smart-before.json"))
        let after = loadHealth(job.dir.appendingPathComponent("smart-after.json"))
        var verification = "nicht vorgesehen"
        switch job.mode.verify {
        case .full:
            switch s?.verified {
            case .some(true):
                verification = job.mode.passes.last == .seeded
                    ? "bestanden – alle Sektoren gelesen und mit den geschriebenen Zufallsdaten verglichen"
                    : "bestanden – alle Sektoren gelesen, alle enthalten Nullen"
            case .some(false): verification = "NICHT bestanden"
            case .none: verification = "nicht durchgeführt"
            }
        case .samples:
            verification = s?.sampleResult ?? "nicht durchgeführt"
        case .none: break
        }
        var w = WipeRecord(modeTitle: job.kind == .check ? job.title : job.mode.title, method: job.mode.methodDescription,
                           standard: job.mode.standard, state: job.state, started: started, ended: ended)
        w.bytesWritten = s?.bytesDoneTotal ?? 0
        w.message = job.message ?? ""
        w.verification = verification
        w.trim = job.mode.trims ? (s?.trimResult ?? "nicht durchgeführt") : ""
        w.device = "/dev/\(d.id)"
        w.bus = d.bus + (d.isInternal ? " (intern)" : "")
        w.typeLabel = d.typeLabel
        w.volumes = d.volumes
        w.exactSize = (s?.diskSize ?? 0) > 0 ? s!.diskSize : d.size
        w.hiddenArea = s?.hiddenArea ?? ""
        w.host = MacInfo.host
        w.platform = MacInfo.osLabel
        w.appVersion = AppModel.appVersion
        w.readOnly = job.mode.readOnly
        if job.mode.readOnly {
            w.badBlocks = s?.badBlocks
            w.nonZero = s?.nonZeroBytes
        }
        w.purgeMethod = s?.purgeMethod ?? ""
        let ident = Reports.identity(health: before ?? after, model: d.name, size: d.size, serial: "")
        var out: [String: String] = [:]
        do {
            if job.mode.readOnly {
                let (pdf, txt) = try Reports.save(Reports.wipeReport(w, identity: ident, before: before, after: after, detailed: true))
                out = ["pdf": pdf.path, "txt": txt.path, "title": "Prüfprotokoll"]
            } else {
                let (pdf, txt) = try Reports.save(Reports.wipeReport(w, identity: ident, before: before, after: after, detailed: false))
                out = ["pdf": pdf.path, "txt": txt.path, "title": "Löschprotokoll"]
                let (pdf2, txt2) = try Reports.save(Reports.wipeReport(w, identity: ident, before: before, after: after, detailed: true))
                out["pdf_full"] = pdf2.path
                out["txt_full"] = txt2.path
            }
        } catch {
            out["error"] = error.localizedDescription
        }
        return out
    }

    // MARK: Diagnose

    func healthModel(_ id: String) -> DiskHealth {
        if let e = healthByDisk[id] { return e }
        let e = DiskHealth()
        healthByDisk[id] = e
        return e
    }

    /// Liest die Gesundheitsdaten (ohne Passwort – smartctl braucht unter macOS keine Administratorrechte)
    func ensureHealth(_ disk: ExternalDisk, force: Bool = false) {
        let e = healthModel(disk.id)
        guard force || (e.health == nil && !e.loading) else { return }
        e.loading = true
        DispatchQueue.global(qos: .userInitiated).async {
            let (h, raw) = SmartService.read(disk.id)
            DispatchQueue.main.async {
                e.health = h
                e.raw = raw
                e.readAt = Date()
                e.loading = false
                self.objectWillChange.send()
            }
        }
    }

    func refreshAllHealth() {
        for d in disks { ensureHealth(d, force: true) }
    }

    func startSelfTest(_ disk: ExternalDisk, _ test: String) {
        let e = healthModel(disk.id)
        DispatchQueue.global(qos: .userInitiated).async {
            let (ok, msg) = SmartService.selfTest(disk.id, test)
            DispatchQueue.main.async {
                if ok {
                    e.selfTestKind = ["short": "Kurztest", "long": "Langtest", "conveyance": "Transporttest"][test] ?? test
                    e.selfTestMessage = nil
                    e.startPolling(diskID: disk.id) { [weak self] in
                        self?.objectWillChange.send()
                        if let m = e.selfTestMessage { self?.enqueueAlert("Selbsttest beendet", "\(disk.name): \(m)") }
                    }
                } else {
                    self.enqueueAlert("Selbsttest konnte nicht gestartet werden",
                                      msg.isEmpty ? "Das Laufwerk bzw. macOS unterstützt diesen Test nicht." : msg)
                }
            }
        }
    }

    func abortSelfTest(_ disk: ExternalDisk) {
        let e = healthModel(disk.id)
        DispatchQueue.global(qos: .userInitiated).async {
            _ = SmartService.selfTest(disk.id, "abort")
            DispatchQueue.main.async {
                e.stopPolling()
                e.selfTestKind = nil
                e.selfTestMessage = "Abgebrochen"
                self.ensureHealth(disk, force: true)
            }
        }
    }

    func saveHealthReport(_ disk: ExternalDisk) {
        let h = healthByDisk[disk.id]?.health
        let ident = Reports.identity(health: h, model: disk.name, size: disk.size, serial: "")
        let r = Reports.healthReport(h, identity: ident, appVersion: AppModel.appVersion,
                                     platform: MacInfo.osLabel, host: MacInfo.host)
        do {
            let (pdf, _) = try Reports.save(r)
            NSWorkspace.shared.open(pdf)
        } catch {
            enqueueAlert("Bericht konnte nicht gespeichert werden", error.localizedDescription)
        }
    }

    // MARK: Hinweise (nacheinander anzeigen)

    func enqueueAlert(_ title: String, _ message: String) {
        if AppModel.demoMode { print("Hinweis: \(title) – \(message)"); return }
        pendingAlerts.append(AlertItem(title: title, message: message))
        if alertItem == nil { showNextAlertSoon() }
    }

    private func showNextAlertSoon() {
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.3) {
            guard self.alertItem == nil, !self.pendingAlerts.isEmpty else { return }
            self.alertItem = self.pendingAlerts.removeFirst()
        }
    }

    // MARK: Quoting

    static func shellQuote(_ s: String) -> String {
        "'" + s.replacingOccurrences(of: "'", with: "'\\''") + "'"
    }

    static func appleScriptEscape(_ s: String) -> String {
        s.replacingOccurrences(of: "\\", with: "\\\\").replacingOccurrences(of: "\"", with: "\\\"")
    }
}
