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
                message = "Der Löschprozess wurde unerwartet beendet (Absturz oder Neustart?). Das Laufwerk ist nur teilweise überschrieben."
            }
        } else if let l = launchedAt, now.timeIntervalSince(l) > 45 {
            state = .failed
            message = "Der Löschprozess konnte nicht gestartet werden."
        }
        if !state.isActive { finish() }
    }

    private func finish() {
        guard !finishedHandled else { return }
        finishedHandled = true
        stop()
        onFinish?(self)
    }

    static func pidAlive(_ pid: Int32) -> Bool {
        guard pid > 0 else { return false }
        return kill(pid, 0) == 0 || errno == EPERM   // EPERM = läuft, gehört aber root
    }
}

// MARK: - App-Zustand

final class AppModel: ObservableObject {
    static let shared = AppModel()

    @Published var disks: [ExternalDisk] = []
    @Published var selection: SidebarItem? {
        didSet {
            // Passenden Modus vorschlagen, wenn ein anderes Laufwerk gewählt wird
            guard selection != oldValue, let d = selectedDisk else { return }
            if d.isSSD == true { mode = .ssd } else if mode == .ssd { mode = .secure }
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

    static var protocolFolder: URL? {
        FileManager.default.urls(for: .documentDirectory, in: .userDomainMask).first?
            .appendingPathComponent("WipeKit-Protokolle", isDirectory: true)
    }

    private init() {
        baseDir = URL(fileURLWithPath: "/private/tmp/WipeKit-\(getuid())", isDirectory: true)
        try? FileManager.default.createDirectory(at: baseDir, withIntermediateDirectories: true,
                                                 attributes: [.posixPermissions: 0o700])
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
            let list = DiskService.listExternalDisks()
            DispatchQueue.main.async {
                self.disks = list
                self.isRefreshing = false
                if case .disk(let id)? = self.selection, !list.contains(where: { $0.id == id }) {
                    self.selection = nil
                }
                if self.selection == nil,
                   let first = list.first(where: { !$0.isProtected && !self.isBusy($0) }) {
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

    func confirmStart() {
        guard let d = pendingDisk else { return }
        pendingDisk = nil
        requestStart(disk: d, mode: mode)
    }

    func requestStart(disk: ExternalDisk, mode: WipeMode) {
        // Dialog erst schließen lassen, dann Passwortabfrage
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.4) {
            self.start(disk: disk, mode: mode)
        }
    }

    private func start(disk: ExternalDisk, mode: WipeMode) {
        guard !disk.isProtected else { return }
        guard !isBusy(disk) else {
            enqueueAlert("Läuft bereits", "\(disk.id) wird bereits gelöscht.")
            return
        }

        let info = JobInfo(id: UUID(), disk: disk, mode: mode, createdAt: Date())
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
        let command = "/usr/bin/nohup " + parts.map(AppModel.shellQuote).joined(separator: " ") + " >/dev/null 2>&1 &"
        let prompt = "WipeKit möchte »\(disk.name)« (\(disk.id), \(Fmt.bytes(disk.size))) unwiderruflich löschen."
        let source = "do shell script \"\(AppModel.appleScriptEscape(command))\" with prompt \"\(AppModel.appleScriptEscape(prompt))\" with administrator privileges"

        var errorInfo: NSDictionary?
        let script = NSAppleScript(source: source)
        _ = script?.executeAndReturnError(&errorInfo)

        if let e = errorInfo {
            let code = (e[NSAppleScript.errorNumber] as? Int) ?? 0
            remove(job)
            if code != -128 {   // -128 = Benutzer hat "Abbrechen" gedrückt
                let text = (e[NSAppleScript.errorMessage] as? String) ?? "Unbekannter Fehler (\(code))"
                enqueueAlert("Löschvorgang konnte nicht gestartet werden", text)
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
            if let d = disks.first(where: { !$0.isProtected && !isBusy($0) }) { selection = .disk(d.id) }
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
            if let p = try? String(contentsOf: dir.appendingPathComponent("protocol_path"), encoding: .utf8) {
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

    private func attach(_ job: WipeJob) {
        job.onFinish = { [weak self] j in self?.jobFinished(j) }
    }

    // MARK: Fertig

    private func jobFinished(_ job: WipeJob) {
        guard !job.alreadyReported else { return }
        job.alreadyReported = true

        if let url = writeProtocol(for: job) {
            job.protocolURL = url
            try? url.path.write(to: job.dir.appendingPathComponent("protocol_path"), atomically: true, encoding: .utf8)
        }

        let d = job.info.disk
        switch job.state {
        case .done:
            NSSound(named: NSSound.Name("Glass"))?.play()
            var text = "\(d.name) (\(d.id), \(Fmt.bytes(d.size))) wurde erfolgreich gelöscht."
            if let e = job.elapsed { text += "\nDauer: \(Fmt.duration(e))" }
            if job.status?.verified == true { text += "\nPrüfung: bestanden – jeder Sektor ist überschrieben." }
            if job.info.mode.trims, let t = job.status?.trimResult { text += "\nTRIM: \(t)" }
            enqueueAlert("Löschen abgeschlossen", text)
        case .cancelled:
            enqueueAlert("Löschvorgang abgebrochen", "\(d.name) (\(d.id)): \(job.message ?? "")")
        default:
            NSSound(named: NSSound.Name("Basso"))?.play()
            enqueueAlert("Löschen fehlgeschlagen", "\(d.name) (\(d.id)): \(job.message ?? "Unbekannter Fehler")")
        }
        NSApplication.shared.requestUserAttention(.criticalRequest)
        objectWillChange.send()
    }

    private func writeProtocol(for job: WipeJob) -> URL? {
        guard let folder = AppModel.protocolFolder else { return nil }
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)

        let d = job.info.disk
        let s = job.status
        let started = s?.startedAt ?? job.info.createdAt
        let ended = s?.finishedAt ?? Date()
        let duration = max(0, ended.timeIntervalSince(started))

        let fileDF = DateFormatter()
        fileDF.dateFormat = "yyyy-MM-dd_HH-mm"
        let safeName = d.name.components(separatedBy: CharacterSet(charactersIn: "/:\\")).joined(separator: "-")
        let url = folder.appendingPathComponent("\(fileDF.string(from: started))_\(d.id)_\(safeName).txt")

        let result: String
        switch job.state {
        case .done:      result = "ERFOLGREICH"
        case .cancelled: result = "ABGEBROCHEN – Laufwerk nur teilweise überschrieben"
        default:         result = "FEHLGESCHLAGEN"
        }
        var verification = "nicht vorgesehen (Modus »Schnell«)"
        if job.info.mode.verifies {
            switch s?.verified {
            case .some(true):
                verification = job.info.mode.passes.last == .seeded
                    ? "bestanden – alle Sektoren gelesen und mit den geschriebenen Zufallsdaten verglichen"
                    : "bestanden – alle Sektoren gelesen, alle enthalten Nullen"
            case .some(false): verification = "NICHT bestanden"
            case .none:        verification = "nicht durchgeführt"
            }
        }
        let written = s?.bytesDoneTotal ?? 0
        let avg = duration > 0 ? Double(written) / duration : 0
        let host = Host.current().localizedName ?? ProcessInfo.processInfo.hostName

        let text = """
        WIPEKIT – LÖSCHPROTOKOLL
        ========================

        Laufwerk:          \(d.name)
        Kennung:           \(d.id)
        Kapazität:         \(Fmt.bytes(d.size)) (\(Fmt.exactBytes(s?.diskSize ?? d.size)))
        Anschluss:         \(d.bus)
        Typ:               \(d.typeLabel)
        Frühere Volumes:   \(d.volumes.isEmpty ? "–" : d.volumes.joined(separator: ", "))

        Methode:           \(job.info.mode.title) – \(job.info.mode.methodDescription)
        Beginn:            \(Fmt.dateTime(started))
        Ende:              \(Fmt.dateTime(ended))
        Dauer:             \(Fmt.duration(duration))
        Ø Geschwindigkeit: \(Fmt.speed(avg))

        Ergebnis:          \(result)
        Verifikation:      \(verification)
        TRIM:              \(job.info.mode.trims ? (s?.trimResult ?? "nicht durchgeführt") : "nicht vorgesehen")
        Meldung:           \(job.message ?? "–")

        Rechner:           \(host)
        Erstellt mit WipeKit \(AppModel.appVersion)
        """
        do {
            try text.write(to: url, atomically: true, encoding: .utf8)
            return url
        } catch {
            return nil
        }
    }

    // MARK: Hinweise (nacheinander anzeigen)

    func enqueueAlert(_ title: String, _ message: String) {
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
