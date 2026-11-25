import SwiftUI
import AppKit

// Hinweis: bewusst ohne SwiftUI-Makros (@State, #Preview …), damit der Build
// auch nur mit den Command Line Tools (ohne Xcode) funktioniert.
// Gestaltung nach den Human Interface Guidelines: Ansichtswechsel per Segmented Control in der
// Symbolleiste, alle Befehle auch in der Menüleiste, Hauptaktion als „prominent“.

// MARK: - App

struct WipeKitApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate
    @ObservedObject private var model = AppModel.shared

    var body: some Scene {
        Window("WipeKit", id: "main") {
            RootView()
                .environmentObject(model)
        }
        .defaultSize(width: 1060, height: 760)
        .windowResizability(.contentMinSize)
        .commands { WipeKitCommands(model: model) }
    }
}

struct WipeKitCommands: Commands {
    @ObservedObject var model: AppModel

    var body: some Commands {
        CommandGroup(replacing: .appInfo) {
            Button("Über WipeKit") { showAboutPanel() }
        }
        CommandGroup(replacing: .newItem) {}
        CommandMenu("Laufwerk") {
            Button("Löschen") { model.view = .wipe }
                .keyboardShortcut("1", modifiers: .command)
            Button("Diagnose") { model.view = .diag }
                .keyboardShortcut("2", modifiers: .command)
            Divider()
            Button("Gesundheitsdaten neu lesen") { model.refreshAllHealth() }
                .keyboardShortcut("r", modifiers: .command)
            Button("Gesundheitsbericht speichern") {
                if let d = model.selectedDisk { model.saveHealthReport(d) }
            }
            .keyboardShortcut("s", modifiers: .command)
            .disabled(model.selectedDisk == nil)
            Button("Oberflächentest starten …") {
                if let d = model.selectedDisk { model.requestStart(disk: d, mode: .check, purpose: "scan") }
            }
            .disabled(model.selectedDisk == nil || model.selectedDisk.map { model.isBusy($0) } == true)
            Divider()
            Button("Laufwerke neu einlesen") { model.refresh() }
                .keyboardShortcut("r", modifiers: [.command, .shift])
            Button("Ordner mit Berichten öffnen") { openProtocolFolder() }
                .keyboardShortcut("o", modifiers: [.command, .shift])
        }
    }
}

func showAboutPanel() {
    let credits = NSMutableAttributedString()
    let body: [NSAttributedString.Key: Any] = [.font: NSFont.systemFont(ofSize: 11), .foregroundColor: NSColor.secondaryLabelColor]
    credits.append(NSAttributedString(string: "Laufwerke sicher löschen und ihre Gesundheit prüfen.\n\n", attributes: body))
    credits.append(NSAttributedString(string: "Enthält smartctl aus smartmontools 7.5 (GPL-2.0-or-later, © Bruce Allen, Christian Franke u. a.) als eigenständiges Programm. Der Quelltext liegt jedem WipeKit-Release auf GitHub bei.\n\n", attributes: body))
    credits.append(NSAttributedString(string: "WipeKit steht unter der MIT-Lizenz. Ohne Gewähr.", attributes: body))
    NSApplication.shared.orderFrontStandardAboutPanel(options: [
        .credits: credits,
        .applicationName: "WipeKit",
    ])
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }

    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        let active = AppModel.shared.jobs.filter { $0.state.isActive }.count
        guard active > 0 else { return .terminateNow }
        let alert = NSAlert()
        alert.messageText = active == 1 ? "Ein Vorgang läuft noch" : "\(active) Vorgänge laufen noch"
        alert.informativeText = "Der Vorgang wird im Hintergrund fortgesetzt und der Mac bleibt wach. Öffne WipeKit erneut, um den Fortschritt zu sehen."
        alert.addButton(withTitle: "Beenden")
        alert.addButton(withTitle: "Abbrechen")
        return alert.runModal() == .alertFirstButtonReturn ? .terminateNow : .terminateCancel
    }
}

func openProtocolFolder() {
    guard let folder = AppModel.protocolFolder else { return }
    try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
    NSWorkspace.shared.open(folder)
}

// MARK: - Farben & Texte

extension WipeMode {
    var tint: Color {
        switch self {
        case .quick:   return Color.orange
        case .secure:  return Color.green
        case .maximum: return Color.indigo
        case .ssd:     return Color.teal
        case .purge:   return Color.purple
        case .check:   return Color.blue
        }
    }
}

extension Rating {
    var color: Color {
        switch self {
        case .veryGood: return Color.green
        case .good:     return Color.blue
        case .caution:  return Color.orange
        case .bad:      return Color.red
        case .unknown:  return Color.secondary
        }
    }

    var symbol: String {
        switch self {
        case .veryGood: return "checkmark.circle.fill"
        case .good:     return "checkmark.circle"
        case .caution:  return "exclamationmark.triangle.fill"
        case .bad:      return "xmark.octagon.fill"
        case .unknown:  return "questionmark.circle"
        }
    }
}

extension WipeJob {
    var tint: Color {
        if foundProblem { return Color.red }
        switch state {
        case .done:      return Color.green
        case .failed:    return Color.red
        case .cancelled: return Color.orange
        default:         return Color.accentColor
        }
    }

    /// Für den Verlauf: Ergebnis und wann
    var historyStatus: String {
        guard let end = status?.finishedAt else { return shortStatus }
        return "\(shortStatus) · \(Fmt.relativeDay(end))"
    }

    var shortStatus: String {
        switch state {
        case .starting:  return "Wird gestartet …"
        case .running:
            if let eta = eta { return "\(Fmt.percent(fraction)) · noch \(Fmt.shortDuration(eta))" }
            return Fmt.percent(fraction)
        case .done:      return doneText
        case .failed:    return "Fehlgeschlagen"
        case .cancelled: return "Abgebrochen"
        }
    }
}

// MARK: - Grundgerüst

struct RootView: View {
    @EnvironmentObject var model: AppModel

    var body: some View {
        NavigationSplitView {
            SidebarView()
                .navigationSplitViewColumnWidth(min: 240, ideal: 270, max: 340)
        } detail: {
            DetailView()
        }
        .frame(minWidth: 900, minHeight: 620)
        .alert(item: $model.alertItem) { item in
            Alert(title: Text(item.title), message: Text(item.message), dismissButton: .default(Text("OK")))
        }
    }
}

// MARK: - Seitenleiste

struct SidebarView: View {
    @EnvironmentObject var model: AppModel
    @State private var showAllHistory = false
    private let historyLimit = 6

    var body: some View {
        List(selection: $model.selection) {
            Section("Externe Laufwerke") {
                let ext = model.disks.filter { !$0.isInternal }
                if ext.isEmpty {
                    Text("Kein Laufwerk angeschlossen")
                        .font(.callout)
                        .foregroundStyle(.secondary)
                }
                ForEach(ext) { disk in
                    DiskSidebarRow(disk: disk, job: model.activeJob(for: disk), health: model.healthByDisk[disk.id])
                        .tag(SidebarItem.disk(disk.id))
                }
            }
            let internals = model.disks.filter { $0.isInternal }
            if !internals.isEmpty {
                Section("Interne Laufwerke") {
                    ForEach(internals) { disk in
                        DiskSidebarRow(disk: disk, job: model.activeJob(for: disk), health: model.healthByDisk[disk.id])
                            .tag(SidebarItem.disk(disk.id))
                    }
                }
            }
            let running = model.jobs.filter { $0.state.isActive }
            if !running.isEmpty {
                Section("Läuft") {
                    ForEach(running) { job in
                        JobSidebarRow(job: job)
                            .tag(SidebarItem.job(job.id))
                    }
                }
            }
            let finished = model.jobs.filter { !$0.state.isActive }
            if !finished.isEmpty {
                Section("Verlauf") {
                    ForEach(showAllHistory ? finished : Array(finished.prefix(historyLimit))) { job in
                        JobSidebarRow(job: job)
                            .tag(SidebarItem.job(job.id))
                    }
                    if finished.count > historyLimit {
                        Button(showAllHistory ? "Weniger anzeigen" : "Alle \(finished.count) anzeigen") {
                            showAllHistory.toggle()
                        }
                        .buttonStyle(.link)
                        .font(.caption)
                        .padding(.leading, 34)
                    }
                }
            }
        }
        .listStyle(.sidebar)
        .safeAreaInset(edge: .bottom) {
            SleepStatusBar()
        }
    }
}

struct DiskSidebarRow: View {
    let disk: ExternalDisk
    let job: WipeJob?
    let health: DiskHealth?

    var body: some View {
        HStack(spacing: 10) {
            Image(systemName: disk.symbol)
                .font(.system(size: 17))
                .foregroundStyle(disk.isProtected ? Color.secondary : Color.accentColor)
                .frame(width: 24)
            VStack(alignment: .leading, spacing: 1) {
                Text(disk.name)
                    .lineLimit(1)
                Text("\(Fmt.bytes(disk.size)) · \(disk.id)")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            Spacer(minLength: 4)
            if let job = job {
                JobMiniRing(job: job)
            } else {
                if let h = health { HealthDot(entry: h) }
                if disk.isProtected {
                    Image(systemName: "lock.fill")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .help("Startlaufwerk – geschützt")
                }
            }
        }
        .padding(.vertical, 3)
    }
}

struct HealthDot: View {
    @ObservedObject var entry: DiskHealth

    var body: some View {
        if let h = entry.health, h.available {
            Circle()
                .fill(h.rating.color)
                .frame(width: 8, height: 8)
                .help("Gesamtzustand: \(h.rating.rawValue)")
        }
    }
}

struct JobSidebarRow: View {
    @ObservedObject var job: WipeJob

    var body: some View {
        HStack(spacing: 10) {
            JobStatusIcon(job: job)
                .frame(width: 24)
            VStack(alignment: .leading, spacing: 1) {
                Text(job.kind == .wipe ? job.info.disk.name : "\(job.title) · \(job.info.disk.name)")
                    .lineLimit(1)
                Text(job.state.isActive ? job.shortStatus : job.historyStatus)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }
        }
        .padding(.vertical, 3)
    }
}

struct JobMiniRing: View {
    @ObservedObject var job: WipeJob

    var body: some View {
        ProgressRing(fraction: job.fraction, lineWidth: 2.5, tint: Color.accentColor)
            .frame(width: 15, height: 15)
    }
}

struct JobStatusIcon: View {
    @ObservedObject var job: WipeJob

    var body: some View {
        switch job.state {
        case .done:
            Image(systemName: job.foundProblem ? "exclamationmark.triangle.fill" : "checkmark.circle.fill")
                .font(.system(size: 17))
                .foregroundStyle(job.tint)
        case .failed:
            Image(systemName: "xmark.circle.fill")
                .font(.system(size: 17))
                .foregroundStyle(Color.red)
        case .cancelled:
            Image(systemName: "minus.circle.fill")
                .font(.system(size: 17))
                .foregroundStyle(Color.orange)
        case .starting, .running:
            ProgressRing(fraction: job.fraction, lineWidth: 2.5, tint: Color.accentColor)
                .frame(width: 16, height: 16)
        }
    }
}

struct SleepStatusBar: View {
    @EnvironmentObject var model: AppModel

    var body: some View {
        HStack(spacing: 8) {
            Image(systemName: model.hasActiveJobs ? "cup.and.saucer.fill" : "moon.zzz.fill")
                .foregroundStyle(model.hasActiveJobs ? Color.orange : Color.secondary)
            Text(model.hasActiveJobs ? "Mac bleibt wach" : "Ruhezustand wie gewohnt")
            Spacer()
        }
        .font(.caption)
        .foregroundStyle(.secondary)
        .padding(.horizontal, 16)
        .padding(.vertical, 10)
    }
}

// MARK: - Detailbereich

struct DetailView: View {
    @EnvironmentObject var model: AppModel

    var body: some View {
        Group {
            if let job = model.selectedJob {
                JobDetailView(job: job)
            } else if let disk = model.selectedDisk {
                if model.view == .diag {
                    if let entry = model.healthByDisk[disk.id] {
                        DiagnoseView(disk: disk, entry: entry)
                    } else {
                        ProgressView("Gesundheitsdaten werden gelesen …")
                            .frame(maxWidth: .infinity, maxHeight: .infinity)
                            .onAppear { model.ensureHealth(disk) }
                    }
                } else {
                    DiskDetailView(disk: disk)
                }
            } else if model.disks.isEmpty {
                EmptyStateView(
                    symbol: "externaldrive.badge.plus",
                    title: "Kein Laufwerk angeschlossen",
                    message: "Schließe ein Laufwerk an. Es erscheint automatisch in der Seitenleiste."
                )
            } else {
                EmptyStateView(
                    symbol: "externaldrive",
                    title: "Kein Laufwerk ausgewählt",
                    message: "Wähle in der Seitenleiste ein Laufwerk aus."
                )
            }
        }
        .toolbar {
            ToolbarItem(placement: .principal) {
                if model.selectedDisk != nil && model.selectedJob == nil {
                    Picker("Ansicht", selection: $model.view) {
                        ForEach(MainView.allCases) { v in
                            Text(v.title).tag(v)
                        }
                    }
                    .pickerStyle(.segmented)
                    .frame(width: 220)
                    .help("Zwischen Löschen (⌘1) und Diagnose (⌘2) wechseln")
                }
            }
            ToolbarItemGroup(placement: .primaryAction) {
                Button {
                    model.refresh()
                    model.refreshAllHealth()
                } label: {
                    Label("Aktualisieren", systemImage: "arrow.clockwise")
                }
                .help("Laufwerke und Gesundheitsdaten neu einlesen")

                Button {
                    openProtocolFolder()
                } label: {
                    Label("Berichte", systemImage: "doc.text.magnifyingglass")
                }
                .help("Ordner mit Protokollen und Berichten öffnen")
            }
        }
    }
}

struct EmptyStateView: View {
    let symbol: String
    let title: String
    let message: String

    var body: some View {
        VStack(spacing: 12) {
            Image(systemName: symbol)
                .font(.system(size: 54, weight: .thin))
                .foregroundStyle(.tertiary)
            Text(title)
                .font(.title2.weight(.semibold))
            Text(message)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
                .frame(maxWidth: 360)
        }
        .padding(40)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

// MARK: - Laufwerk: Löschen

struct DiskDetailView: View {
    let disk: ExternalDisk
    @EnvironmentObject var model: AppModel

    var body: some View {
        Form {
            Section {
                DiskHeader(disk: disk)
            }

            if !disk.canWipe {
                Section {
                    HStack(alignment: .top, spacing: 12) {
                        Image(systemName: "lock.fill").foregroundStyle(.secondary)
                        VStack(alignment: .leading, spacing: 6) {
                            Text(disk.isProtected
                                 ? "Auf diesem Laufwerk läuft macOS. Es ist geschützt und kann nicht gelöscht werden."
                                 : "Interne Laufwerke löscht WipeKit unter macOS nicht – zum Löschen eines internen Mac-Laufwerks ist das Festplattendienstprogramm im macOS-Wiederherstellungsmodus vorgesehen.")
                                .fixedSize(horizontal: false, vertical: true)
                            Text("Gesundheitsdaten und Oberflächentest stehen unter „Diagnose“ zur Verfügung.")
                                .font(.callout)
                                .foregroundStyle(.secondary)
                        }
                        Spacer()
                        Button("Zur Diagnose") { model.view = .diag }
                    }
                }
            } else if let job = model.activeJob(for: disk) {
                Section {
                    HStack {
                        Label("Auf diesem Laufwerk läuft gerade: \(job.title).", systemImage: "hourglass")
                        Spacer()
                        Button("Fortschritt anzeigen") { model.selection = .job(job.id) }
                    }
                }
            } else {
                Section {
                    ForEach(WipeMode.allCases) { m in
                        ModeRow(mode: m,
                                selected: model.mode == m,
                                recommended: m == WipeMode.recommended(for: disk))
                            .contentShape(Rectangle())
                            .onTapGesture { if m.availableOnMac { model.mode = m } }
                            .opacity(m.availableOnMac ? 1 : 0.5)
                            .help(m.availableOnMac ? m.explanation : "Unter macOS nicht verfügbar – siehe Erklärung")
                    }
                } header: {
                    Text("Methode")
                }

                ModeDetailSection(mode: model.mode, disk: disk)
            }

            Section("Informationen") {
                LabeledContent("Gerät", value: "/dev/\(disk.id)")
                LabeledContent("Kapazität", value: "\(Fmt.bytes(disk.size)) (\(Fmt.exactBytes(disk.size)))")
                LabeledContent("Anschluss", value: disk.bus + (disk.isInternal ? " (intern)" : ""))
                LabeledContent("Typ", value: disk.typeLabel)
                if let s = model.healthByDisk[disk.id]?.health?.serial, !s.isEmpty {
                    LabeledContent("Seriennummer", value: s)
                }
                LabeledContent("Volumes", value: disk.volumes.isEmpty ? "Keine" : disk.volumes.joined(separator: ", "))
            }
        }
        .formStyle(.grouped)
        .safeAreaInset(edge: .bottom) {
            if disk.canWipe && !model.isBusy(disk) {
                BottomBar {
                    Text(model.mode.readOnly ? "Es werden keine Daten verändert."
                                             : "Alle Daten auf „\(disk.name)“ werden unwiderruflich gelöscht.")
                        .font(.callout)
                        .foregroundStyle(.secondary)
                        .lineLimit(2)
                    Spacer()
                    if model.mode.readOnly {
                        Button {
                            model.askConfirm(disk)
                        } label: {
                            Text("Prüfen …").frame(minWidth: 90)
                        }
                        .buttonStyle(.borderedProminent)
                        .controlSize(.large)
                    } else {
                        Button(role: .destructive) {
                            model.askConfirm(disk)
                        } label: {
                            Text("Löschen …").frame(minWidth: 90)
                        }
                        .buttonStyle(.borderedProminent)
                        .tint(Color.red)
                        .controlSize(.large)
                        .disabled(!model.mode.availableOnMac)
                    }
                }
            }
        }
        .navigationTitle(disk.name)
        .navigationSubtitle("\(disk.id) · \(Fmt.bytes(disk.size))")
        .alert(model.mode.readOnly ? "„\(disk.name)“ prüfen?" : "„\(disk.name)“ löschen?", isPresented: $model.showConfirm) {
            if model.mode.readOnly {
                Button("Prüfung starten") { model.confirmStart() }
            } else {
                Button("Löschen", role: .destructive) { model.confirmStart() }
            }
            Button("Abbrechen", role: .cancel) {}
        } message: {
            Text(model.mode.readOnly
                 ? "WipeKit liest alle Sektoren von \(disk.id) (\(Fmt.bytes(disk.size))) und prüft, ob das Laufwerk leer ist. Es werden keine Daten verändert."
                 : "Alle Daten auf \(disk.id) (\(Fmt.bytes(disk.size))) werden mit der Methode „\(model.mode.title)“ unwiderruflich gelöscht. Dieser Vorgang kann nicht rückgängig gemacht werden.")
        }
    }
}

struct DiskHeader: View {
    let disk: ExternalDisk

    var body: some View {
        HStack(spacing: 16) {
            ZStack {
                RoundedRectangle(cornerRadius: 14, style: .continuous)
                    .fill((disk.isProtected ? Color.gray : Color.accentColor).gradient)
                Image(systemName: disk.symbol)
                    .font(.system(size: 28, weight: .medium))
                    .foregroundStyle(Color.white)
            }
            .frame(width: 60, height: 60)

            VStack(alignment: .leading, spacing: 4) {
                Text(disk.name)
                    .font(.title2.weight(.semibold))
                    .lineLimit(2)
                Text("\(Fmt.bytes(disk.size)) · \(disk.typeLabel) · \(disk.bus)\(disk.isInternal ? " · intern" : "")")
                    .foregroundStyle(.secondary)
            }
            Spacer()
        }
        .padding(.vertical, 6)
    }
}

struct ModeRow: View {
    let mode: WipeMode
    let selected: Bool
    let recommended: Bool

    var body: some View {
        HStack(spacing: 12) {
            ZStack {
                RoundedRectangle(cornerRadius: 7, style: .continuous)
                    .fill(mode.tint.gradient)
                Image(systemName: mode.symbol)
                    .font(.system(size: 14, weight: .semibold))
                    .foregroundStyle(Color.white)
            }
            .frame(width: 28, height: 28)

            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 6) {
                    Text(mode.title)
                        .font(.body.weight(.medium))
                    if recommended {
                        Badge(text: "Empfohlen", tint: Color.accentColor)
                    }
                    if mode.readOnly {
                        Badge(text: "Ändert nichts", tint: Color.secondary)
                    }
                    if !mode.availableOnMac {
                        Badge(text: "Nicht unter macOS", tint: Color.secondary)
                    }
                }
                Text(mode.tagline)
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            Spacer()

            Image(systemName: selected ? "checkmark.circle.fill" : "circle")
                .font(.system(size: 18))
                .foregroundStyle(selected ? Color.accentColor : Color.secondary.opacity(0.4))
        }
        .padding(.vertical, 2)
    }
}

struct Badge: View {
    let text: String
    let tint: Color

    var body: some View {
        Text(text)
            .font(.caption2.weight(.semibold))
            .foregroundStyle(tint)
            .padding(.horizontal, 6)
            .padding(.vertical, 2)
            .background(Capsule().fill(tint.opacity(0.15)))
    }
}

struct ModeDetailSection: View {
    let mode: WipeMode
    let disk: ExternalDisk

    private var estimate: TimeInterval {
        let speed: Double = disk.isSSD == true ? 300_000_000 : 130_000_000
        return Double(disk.size) * (Double(mode.workUnits) + (mode.verify == .samples ? 0.02 : 0)) / speed
    }

    private var hint: String? {
        if mode.readOnly || !mode.availableOnMac { return nil }
        if disk.isSSD == true && mode != .ssd {
            return "Dieses Laufwerk ist eine SSD – dafür ist der Modus »SSD« besser geeignet."
        }
        if disk.isSSD == false && mode == .ssd {
            return "Dieses Laufwerk ist eine Festplatte – TRIM wird hier nicht unterstützt. »Sicher« ist die bessere Wahl."
        }
        if disk.isSSD == nil {
            return "Der Laufwerkstyp wird vom USB-Adapter nicht gemeldet. Handelt es sich um eine SSD, wähle den Modus »SSD«."
        }
        return nil
    }

    var body: some View {
        Section {
            Text(mode.explanation)
                .fixedSize(horizontal: false, vertical: true)
            LabeledContent("Geeignet für") {
                Text(mode.bestFor)
                    .multilineTextAlignment(.trailing)
            }
            LabeledContent("Sicherheit") {
                SecurityMeter(level: mode.securityLevel, label: mode.securityLabel, tint: mode.tint)
            }
            LabeledContent("Orientiert sich an") {
                Text(mode.standard)
                    .multilineTextAlignment(.trailing)
            }
            if mode.availableOnMac {
                LabeledContent("Geschätzte Dauer", value: "ca. \(Fmt.shortDuration(estimate))")
            }
        } header: {
            Text("Über »\(mode.title)«")
        } footer: {
            VStack(alignment: .leading, spacing: 8) {
                Text(mode.securityNote)
                    .fixedSize(horizontal: false, vertical: true)
                if !mode.readOnly {
                    Text(WipeMode.notCertified)
                        .fixedSize(horizontal: false, vertical: true)
                }
                if let hint = hint {
                    Label {
                        Text(hint).fixedSize(horizontal: false, vertical: true)
                    } icon: {
                        Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(Color.orange)
                    }
                }
            }
            .font(.caption)
            .foregroundStyle(.secondary)
        }
    }
}

struct SecurityMeter: View {
    let level: Int
    let label: String
    let tint: Color

    var body: some View {
        HStack(spacing: 8) {
            HStack(spacing: 3) {
                ForEach(0..<5, id: \.self) { i in
                    Capsule()
                        .fill(i < level ? tint : Color.secondary.opacity(0.2))
                        .frame(width: 12, height: 6)
                }
            }
            Text(label)
        }
        .accessibilityElement(children: .combine)
        .accessibilityLabel("Sicherheit: \(label)")
    }
}

// MARK: - Laufwerk: Diagnose

struct DiagnoseView: View {
    let disk: ExternalDisk
    @ObservedObject var entry: DiskHealth
    @EnvironmentObject var model: AppModel

    var body: some View {
        Form {
            Section {
                DiskHeader(disk: disk)
            }
            if let h = entry.health {
                if h.available { overview(h) }
                if h.available {
                    Section("Bewertung") {
                        ForEach(h.reasons, id: \.self) { r in
                            Label {
                                Text(r).fixedSize(horizontal: false, vertical: true)
                            } icon: {
                                Image(systemName: h.rating.symbol).foregroundStyle(h.rating.color)
                            }
                        }
                    }
                    Section("Kennwerte") {
                        ForEach(Array(HFmt.keyValues(h).dropFirst().enumerated()), id: \.offset) { _, kv in
                            LabeledContent(kv.0) {
                                Text(kv.1)
                                    .monospacedDigit()
                                    .foregroundStyle(valueColor(kv.0, kv.1))
                                    .multilineTextAlignment(.trailing)
                                    .textSelection(.enabled)
                            }
                        }
                    }
                } else {
                    unavailable(h)
                }
                tests(h)
                if h.available {
                    Section("Laufwerk") {
                        ForEach(Array(HFmt.driveDetails(h).enumerated()), id: \.offset) { _, kv in
                            LabeledContent(kv.0) {
                                Text(kv.1).multilineTextAlignment(.trailing).textSelection(.enabled)
                            }
                        }
                    }
                    if !h.attributes.isEmpty { attributes(h) }
                    if !h.nvme.isEmpty {
                        Section {
                            DisclosureGroup("NVMe-Gesundheitswerte") {
                                ForEach(Array(HFmt.nvmeRows(h).enumerated()), id: \.offset) { _, kv in
                                    LabeledContent(kv.0) { Text(kv.1).monospacedDigit() }
                                }
                            }
                        }
                    }
                    if !h.selfTests.isEmpty {
                        Section("Selbsttest-Protokoll des Laufwerks") {
                            ForEach(h.selfTests.prefix(8)) { t in
                                LabeledContent {
                                    Text(t.hours.map { "Std. \(HFmt.count($0))" } ?? "").monospacedDigit()
                                } label: {
                                    VStack(alignment: .leading, spacing: 2) {
                                        Text(t.kind)
                                        Text(t.result).font(.caption).foregroundStyle(.secondary)
                                    }
                                }
                            }
                        }
                    }
                }
            } else {
                Section {
                    HStack {
                        ProgressView().controlSize(.small)
                        Text("Gesundheitsdaten werden gelesen …").foregroundStyle(.secondary)
                    }
                }
            }
        }
        .formStyle(.grouped)
        .safeAreaInset(edge: .bottom) {
            BottomBar {
                if let t = entry.readAt {
                    Text("Gelesen \(Fmt.clock(t))")
                        .font(.callout)
                        .foregroundStyle(.secondary)
                }
                Spacer()
                Button("Aktualisieren") {
                    if entry.health?.usbTried == true { model.readHealthAsAdmin(disk) } else { model.ensureHealth(disk, force: true) }
                }
                    .controlSize(.large)
                    .disabled(entry.loading || model.isBusy(disk))
                Button("Bericht speichern") { model.saveHealthReport(disk) }
                    .buttonStyle(.borderedProminent)
                    .controlSize(.large)
                    .disabled(entry.health == nil)
            }
        }
        .navigationTitle(disk.name)
        .navigationSubtitle("Diagnose · \(disk.id)")
    }

    private func valueColor(_ key: String, _ value: String) -> Color {
        let zero = value == "0" || value.hasPrefix("–")
        if key.hasPrefix("Unkorrigierbare") && !zero { return Color.red }
        if key.hasPrefix("Ausstehende") && !zero { return Color.orange }
        return Color.secondary
    }

    @ViewBuilder private func overview(_ h: Health) -> some View {
        Section {
            HStack(alignment: .top, spacing: 0) {
                StatTile(title: "Gesamtzustand", value: h.rating.rawValue, tint: h.rating.color,
                         footnote: h.available ? (h.reasons.first ?? "") : "keine Daten", symbol: h.rating.symbol)
                Divider().padding(.horizontal, 12)
                if h.isSSD == true {
                    StatTile(title: "Restlebensdauer", value: h.lifePercent.map { "\($0) %" } ?? "–", tint: Color.primary,
                             footnote: "laut Laufwerk", symbol: "gauge.with.dots.needle.67percent")
                } else {
                    StatTile(title: "Betriebsstunden", value: HFmt.count(h.powerOnHours), tint: Color.primary,
                             footnote: "Stunden eingeschaltet", symbol: "clock")
                }
                Divider().padding(.horizontal, 12)
                StatTile(title: "Temperatur", value: HFmt.temp(h.temperature), tint: Color.primary,
                         footnote: "aktuell", symbol: "thermometer.medium")
            }
            .fixedSize(horizontal: false, vertical: true)
            .padding(.vertical, 6)
        }
    }

    @ViewBuilder private func unavailable(_ h: Health) -> some View {
        let usb = disk.bus == "USB"
        Section {
            if entry.loading {
                HStack(spacing: 10) {
                    ProgressView().controlSize(.small)
                    VStack(alignment: .leading, spacing: 2) {
                        Text(usb ? "Wird über den USB-Adapter gelesen …" : "Gesundheitsdaten werden gelesen …")
                        if usb {
                            Text("Das Laufwerk ist dabei kurz ausgeworfen. Das dauert meist unter einer Minute.")
                                .font(.callout).foregroundStyle(.secondary)
                        }
                    }
                }
                .padding(.vertical, 2)
            } else if usb && !h.usbTried {
                VStack(alignment: .leading, spacing: 6) {
                    Text("macOS gibt die Gesundheitsdaten von USB-Laufwerken nicht an Programme weiter. WipeKit kann sie direkt über den USB-Adapter lesen.")
                        .fixedSize(horizontal: false, vertical: true)
                    Text("Dafür wird das Laufwerk kurz ausgeworfen und meldet sich danach wieder an. Daten werden nicht verändert.")
                        .font(.callout)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
                .frame(maxWidth: .infinity, alignment: .leading)
                HStack {
                    Spacer()
                    Button("Über USB-Adapter lesen …") { model.readHealthAsAdmin(disk) }
                        .buttonStyle(.borderedProminent)
                        .disabled(model.isBusy(disk))
                        .help("Administrator-Passwort nötig")
                }
            } else {
                Label {
                    Text(h.reason.isEmpty ? "Für dieses Laufwerk sind keine Gesundheitsdaten verfügbar." : h.reason)
                        .fixedSize(horizontal: false, vertical: true)
                } icon: {
                    Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(.orange)
                }
                if usb {
                    Text("Adapter und Gehäuse mit Chips von ASMedia, JMicron oder Realtek geben die Daten meist weiter. Thunderbolt- und USB4-Gehäuse liefern sie immer.")
                        .font(.callout)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
                HStack {
                    Spacer()
                    Button(usb ? "Erneut versuchen …" : "Mit Administratorrechten lesen") { model.readHealthAsAdmin(disk) }
                        .disabled(model.isBusy(disk))
                        .help("Administrator-Passwort nötig")
                }
            }
        } header: {
            Text("Gesundheitsdaten")
        }
    }

    @ViewBuilder private func tests(_ h: Health) -> some View {
        Section {
            if let kind = entry.selfTestKind {
                HStack {
                    ProgressView().controlSize(.small)
                    Text("\(kind) läuft im Laufwerk" + (entry.health?.selfTestRemaining.map { " – noch \($0) %" } ?? ""))
                    Spacer()
                    Button("Abbrechen") { model.abortSelfTest(disk) }
                }
            } else if let m = entry.selfTestMessage {
                LabeledContent("Letzter Selbsttest", value: m)
            }
            if h.available && h.supportsSelfTests && h.proto != "NVMe" && !h.usbTried {
                testRow("Kurztest", "Elektronik und Teile der Oberfläche · ca. \(h.shortMinutes ?? 2) Min.") {
                    model.startSelfTest(disk, "short")
                }
                testRow("Langtest", "Gesamte Oberfläche durch das Laufwerk · ca. \(h.longMinutes.map { Fmt.shortDuration(Double($0) * 60) } ?? "1–2 Std.")") {
                    model.startSelfTest(disk, "long")
                }
                if h.supportsConveyance {
                    testRow("Transporttest", "Transportschäden erkennen · ca. 2 Min.") { model.startSelfTest(disk, "conveyance") }
                }
            }
            testRow("Oberflächentest", "WipeKit liest jeden Sektor und misst die Geschwindigkeit · ca. \(Fmt.shortDuration(Double(disk.size) / (disk.isSSD == true ? 400e6 : 130e6))) · Administrator-Passwort nötig") {
                model.requestStart(disk: disk, mode: .check, purpose: "scan")
            }
        } header: {
            Text("Tests")
        } footer: {
            VStack(alignment: .leading, spacing: 4) {
                Text("Keiner der Tests verändert Daten. Selbsttests führt das Laufwerk selbst aus.")
                if h.usbTried && h.available {
                    Text("Selbsttests sind über den USB-Direktzugriff nicht möglich – das Laufwerk müsste dafür minutenlang ausgeworfen bleiben.")
                } else if h.proto == "NVMe" {
                    Text("Selbsttests von NVMe-SSDs gibt macOS für Programme nicht frei.")
                }
            }
            .font(.caption)
            .foregroundStyle(.secondary)
        }
    }

    private func testRow(_ title: String, _ subtitle: String, _ action: @escaping () -> Void) -> some View {
        HStack {
            VStack(alignment: .leading, spacing: 2) {
                Text(title)
                Text(subtitle).font(.caption).foregroundStyle(.secondary)
            }
            Spacer()
            Button("Starten", action: action)
                .disabled(model.isBusy(disk) || entry.selfTestKind != nil)
        }
    }

    @ViewBuilder private func attributes(_ h: Health) -> some View {
        let warn = h.attributes.filter { $0.status != "OK" }.count
        Section {
            DisclosureGroup {
                Grid(alignment: .leading, horizontalSpacing: 14, verticalSpacing: 6) {
                    GridRow {
                        Text("ID").gridColumnAlignment(.trailing)
                        Text("Attribut")
                        Text("Wert").gridColumnAlignment(.trailing)
                        Text("Schlechtester").gridColumnAlignment(.trailing)
                        Text("Grenzwert").gridColumnAlignment(.trailing)
                        Text("Rohwert").gridColumnAlignment(.trailing)
                        Text("Status")
                    }
                    .font(.caption.weight(.semibold))
                    .foregroundStyle(.secondary)
                    Divider()
                    ForEach(h.attributes) { a in
                        GridRow {
                            Text("\(a.id)").monospacedDigit()
                            Text(a.name).lineLimit(1)
                            Text("\(a.value)").monospacedDigit()
                            Text("\(a.worst)").monospacedDigit()
                            Text("\(a.thresh)").monospacedDigit()
                            Text(a.rawString).monospacedDigit().lineLimit(1)
                            Text(a.status)
                                .fontWeight(a.status == "OK" ? .regular : .semibold)
                                .foregroundStyle(a.status == "OK" ? Color.secondary : (a.status == "Fehler" ? Color.red : Color.orange))
                        }
                        .font(.callout)
                    }
                }
                .padding(.vertical, 4)
            } label: {
                HStack {
                    Text("S.M.A.R.T.-Attribute")
                    Spacer()
                    Text(warn == 0 ? "\(h.attributes.count) · alle unauffällig" : "\(warn) auffällig")
                        .foregroundStyle(warn == 0 ? Color.secondary : Color.orange)
                }
            }
        }
    }
}

struct StatTile: View {
    let title: String
    let value: String
    let tint: Color
    let footnote: String
    let symbol: String

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Label(title, systemImage: symbol)
                .font(.caption.weight(.semibold))
                .foregroundStyle(.secondary)
            Text(value)
                .font(.system(size: 26, weight: .semibold, design: .rounded))
                .monospacedDigit()
                .foregroundStyle(tint)
                .lineLimit(1)
                .minimumScaleFactor(0.6)
            Text(footnote)
                .font(.caption)
                .foregroundStyle(.secondary)
                .lineLimit(2)
                .fixedSize(horizontal: false, vertical: true)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .accessibilityElement(children: .combine)
    }
}

// MARK: - Vorgang

struct JobDetailView: View {
    @ObservedObject var job: WipeJob
    @EnvironmentObject var model: AppModel

    var body: some View {
        Form {
            Section {
                JobHero(job: job)
            }

            if job.kind == .check, let s = job.status {
                Section("Lesegeschwindigkeit") {
                    SpeedCurve(values: s.curve ?? [], total: SpeedCurve.total(size: s.diskSize > 0 ? s.diskSize : job.disk.size))
                        .frame(height: 150)
                        .padding(.vertical, 4)
                }
                Section("Befund") {
                    LabeledContent("Unlesbare Bereiche") {
                        Text((s.badBlocks ?? 0) == 0 ? "keine" : "\(s.badBlocks ?? 0) (je 64 KiB)")
                            .foregroundStyle((s.badBlocks ?? 0) == 0 ? Color.secondary : Color.red)
                    }
                    if let nz = s.nonZeroBytes {
                        LabeledContent("Daten gefunden", value: nz == 0 ? "keine – nur Nullen" : "\(Fmt.bytes(nz)) enthalten Daten")
                    }
                }
            }

            if job.state.isActive {
                progressSection
            } else {
                resultSection
            }

            Section("Laufwerk") {
                LabeledContent("Name", value: job.info.disk.name)
                LabeledContent("Gerät", value: "/dev/\(job.info.disk.id)")
                LabeledContent("Kapazität", value: Fmt.bytes(job.info.disk.size))
                LabeledContent("Vorgang", value: "\(job.kind == .check ? job.title : job.info.mode.title) · \(job.info.mode.tagline)")
            }
        }
        .formStyle(.grouped)
        .safeAreaInset(edge: .bottom) {
            bottomBar
        }
        .navigationTitle(job.info.disk.name)
        .navigationSubtitle(job.shortStatus)
        .confirmationDialog("\(job.title) abbrechen?", isPresented: $job.askCancel) {
            Button("\(job.title) abbrechen", role: .destructive) { job.requestCancel() }
            Button("Weiterlaufen lassen", role: .cancel) {}
        } message: {
            Text(job.kind == .wipe ? "Das Laufwerk ist danach nur teilweise überschrieben." : "Es werden keine Daten verändert.")
        }
    }

    @ViewBuilder private var progressSection: some View {
        Section("Fortschritt") {
            if let s = job.status, s.state == .running {
                LabeledContent("Schritt", value: s.stepCount > 1 ? "\(s.step) von \(s.stepCount) · \(s.phase)" : s.phase)
                LabeledContent("Verarbeitet", value: "\(Fmt.bytes(s.bytesDoneTotal)) von \(Fmt.bytes(s.bytesTotal))")
                LabeledContent("Geschwindigkeit", value: s.speed > 0 ? Fmt.speed(s.speed) : "–")
                LabeledContent("Verbleibend", value: job.eta.map { "ca. \(Fmt.duration($0))" } ?? "Wird berechnet …")
                LabeledContent("Voraussichtlich fertig", value: job.eta.map { Fmt.clock(job.now.addingTimeInterval($0)) } ?? "–")
                LabeledContent("Läuft seit", value: job.elapsed.map { Fmt.duration($0) } ?? "–")
            } else {
                LabeledContent("Status", value: job.status?.phase ?? "Warte auf Administrator-Passwort …")
            }
        }
        Section {
            LabeledContent("Ruhezustand") {
                Label {
                    Text(job.status?.sleepPrevented == true ? "Verhindert – Display darf ausgehen" : "Wird vorbereitet …")
                } icon: {
                    Image(systemName: "cup.and.saucer.fill").foregroundStyle(Color.orange)
                }
            }
        } footer: {
            Text("Sobald der Vorgang beendet ist, geht der Mac wieder wie gewohnt in den Ruhezustand.")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
    }

    @ViewBuilder private var resultSection: some View {
        Section("Ergebnis") {
            Text(job.message ?? "–")
                .foregroundStyle(job.state == .failed ? Color.red : Color.primary)
                .fixedSize(horizontal: false, vertical: true)
            if let e = job.elapsed {
                LabeledContent("Dauer", value: Fmt.duration(e))
            }
            if job.kind == .wipe {
                if job.info.mode.verify == .full {
                    LabeledContent("Prüfung", value: verificationText)
                }
                if let sr = job.status?.sampleResult {
                    LabeledContent("Stichproben") { Text(sr).multilineTextAlignment(.trailing) }
                }
                if job.info.mode.trims {
                    LabeledContent("TRIM") {
                        Text(job.status?.trimResult ?? "Nicht durchgeführt")
                            .multilineTextAlignment(.trailing)
                    }
                }
            }
            if let s = job.status, let end = s.finishedAt {
                LabeledContent("Beendet", value: Fmt.dateTime(end))
            }
        }
        if job.reports["pdf"] != nil {
            Section {
                reportRow(job.reports["title"] ?? "Protokoll", pdf: job.reports["pdf"], txt: job.reports["txt"])
                if job.reports["pdf_full"] != nil {
                    reportRow("Ausführliches Protokoll", pdf: job.reports["pdf_full"], txt: job.reports["txt_full"])
                }
            } header: {
                Text("Protokolle")
            } footer: {
                Text("Gespeichert unter Dokumente › WipeKit-Protokolle (Unterordner „PDF“ und „Text“).")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        } else if let url = job.protocolURL {
            Section("Protokoll") {
                LabeledContent(url.lastPathComponent) {
                    Button("Öffnen") { NSWorkspace.shared.open(url) }
                }
            }
        }
    }

    private func reportRow(_ title: String, pdf: String?, txt: String?) -> some View {
        LabeledContent {
            HStack {
                if let p = pdf { Button("PDF öffnen") { NSWorkspace.shared.open(URL(fileURLWithPath: p)) } }
                if let t = txt { Button("Text") { NSWorkspace.shared.open(URL(fileURLWithPath: t)) } }
                if let p = pdf {
                    Button {
                        NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: p)])
                    } label: {
                        Image(systemName: "folder")
                    }
                    .help("Im Finder zeigen")
                }
            }
        } label: {
            VStack(alignment: .leading, spacing: 2) {
                Text(title)
                Text(pdf.map { URL(fileURLWithPath: $0).deletingPathExtension().lastPathComponent } ?? "")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .truncationMode(.middle)
            }
        }
    }

    private var verificationText: String {
        switch job.status?.verified {
        case .some(true):  return "Bestanden"
        case .some(false): return "Nicht bestanden"
        case .none:        return "Nicht durchgeführt"
        }
    }

    @ViewBuilder private var bottomBar: some View {
        BottomBar {
            if job.state.isActive {
                Text("Du kannst WipeKit schließen – der Vorgang läuft im Hintergrund weiter.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .lineLimit(2)
                Spacer()
                if job.cancelRequested {
                    Text("Wird abgebrochen …")
                        .foregroundStyle(.secondary)
                } else {
                    Button("Abbrechen …") { job.askCancel = true }
                        .controlSize(.large)
                        .disabled(job.status == nil)
                }
            } else {
                Spacer()
                Button("Entfernen") { model.remove(job) }
                    .controlSize(.large)
                if let url = job.protocolURL {
                    Button("Protokoll öffnen") { NSWorkspace.shared.open(url) }
                        .buttonStyle(.borderedProminent)
                        .controlSize(.large)
                }
            }
        }
    }
}

struct JobHero: View {
    @ObservedObject var job: WipeJob

    var body: some View {
        VStack(spacing: 16) {
            ZStack {
                ProgressRing(fraction: job.state == .done ? 1 : job.fraction, lineWidth: 10, tint: job.tint)
                center
            }
            .frame(width: 150, height: 150)

            VStack(spacing: 4) {
                Text(headline)
                    .font(.title2.weight(.semibold))
                Text(subline)
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
            }
        }
        .frame(maxWidth: .infinity)
        .padding(.vertical, 18)
    }

    @ViewBuilder private var center: some View {
        switch job.state {
        case .done:
            Image(systemName: job.foundProblem ? "exclamationmark.triangle" : "checkmark")
                .font(.system(size: 48, weight: .semibold))
                .foregroundStyle(job.tint)
        case .failed:
            Image(systemName: "xmark")
                .font(.system(size: 48, weight: .semibold))
                .foregroundStyle(Color.red)
        case .cancelled:
            Image(systemName: "minus")
                .font(.system(size: 48, weight: .semibold))
                .foregroundStyle(Color.orange)
        case .starting, .running:
            VStack(spacing: 2) {
                Text(Fmt.percent(job.fraction))
                    .font(.system(size: 30, weight: .semibold, design: .rounded).monospacedDigit())
                if let s = job.status, s.state == .running, s.stepCount > 1 {
                    Text("Schritt \(s.step) von \(s.stepCount)")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            }
        }
    }

    private var headline: String {
        let t = job.title
        switch job.state {
        case .starting:  return "Wird vorbereitet"
        case .running:   return job.kind == .wipe ? "Wird gelöscht" : "\(t) läuft"
        case .done:      return job.kind == .wipe ? "Löschen abgeschlossen" : "\(t) abgeschlossen"
        case .failed:    return "\(t) fehlgeschlagen"
        case .cancelled: return "\(t) abgebrochen"
        }
    }

    private var subline: String {
        switch job.state {
        case .starting:
            return job.status?.phase ?? "Bitte das Administrator-Passwort eingeben."
        case .running:
            return job.status?.phase ?? ""
        case .done:
            return job.kind == .wipe ? "„\(job.info.disk.name)“ wurde sicher gelöscht." : (job.message ?? job.info.disk.name)
        case .failed, .cancelled:
            return job.info.disk.name
        }
    }
}

// MARK: - Bausteine

struct SpeedCurve: View {
    let values: [Double]
    let total: Int

    static func total(size: UInt64) -> Int {
        let chunk: UInt64 = 4 * 1024 * 1024
        let bucket = max(chunk, size / 200)
        return max(1, Int((size + bucket - 1) / max(1, bucket)))
    }

    var body: some View {
        GeometryReader { geo in
            let padL: CGFloat = 44, padB: CGFloat = 18, padT: CGFloat = 6
            let w = geo.size.width - padL - 6, h = geo.size.height - padB - padT
            let vmax = max(1, (values.max() ?? 100) * 1.15)
            ZStack(alignment: .topLeading) {
                ForEach(0..<5, id: \.self) { i in
                    let y = padT + h * CGFloat(i) / 4
                    Path { p in
                        p.move(to: CGPoint(x: padL, y: y))
                        p.addLine(to: CGPoint(x: padL + w, y: y))
                    }
                    .stroke(Color.secondary.opacity(0.2), lineWidth: 1)
                    Text(String(format: "%.0f", vmax * Double(4 - i) / 4))
                        .font(.caption2)
                        .foregroundStyle(.secondary)
                        .frame(width: padL - 8, alignment: .trailing)
                        .position(x: (padL - 8) / 2, y: y)
                }
                if !values.isEmpty {
                    let pts = values.enumerated().map { i, v in
                        CGPoint(x: padL + w * (CGFloat(i) + 0.5) / CGFloat(max(total, 1)),
                                y: padT + h * (1 - CGFloat(v / vmax)))
                    }
                    Path { p in
                        p.move(to: CGPoint(x: pts[0].x, y: padT + h))
                        for pt in pts { p.addLine(to: pt) }
                        p.addLine(to: CGPoint(x: pts[pts.count - 1].x, y: padT + h))
                        p.closeSubpath()
                    }
                    .fill(Color.accentColor.opacity(0.18))
                    Path { p in
                        p.move(to: pts[0])
                        for pt in pts.dropFirst() { p.addLine(to: pt) }
                    }
                    .stroke(Color.accentColor, lineWidth: 1.8)
                }
                Text("Anfang").font(.caption2).foregroundStyle(.secondary)
                    .position(x: padL + 18, y: geo.size.height - 6)
                Text("MB/s").font(.caption2).foregroundStyle(.secondary)
                    .position(x: padL + w / 2, y: geo.size.height - 6)
                Text("Ende").font(.caption2).foregroundStyle(.secondary)
                    .position(x: padL + w - 12, y: geo.size.height - 6)
            }
        }
        .accessibilityLabel("Lesegeschwindigkeit über die Position auf dem Laufwerk")
    }
}

struct ProgressRing: View {
    let fraction: Double
    let lineWidth: CGFloat
    let tint: Color

    var body: some View {
        ZStack {
            Circle()
                .stroke(tint.opacity(0.15), lineWidth: lineWidth)
            Circle()
                .trim(from: 0, to: CGFloat(min(1, max(0, fraction))))
                .stroke(tint, style: StrokeStyle(lineWidth: lineWidth, lineCap: .round))
                .rotationEffect(.degrees(-90))
                .animation(.easeInOut(duration: 0.8), value: fraction)
        }
        .padding(lineWidth / 2)
    }
}

struct BottomBar<Content: View>: View {
    let content: Content

    init(@ViewBuilder content: () -> Content) {
        self.content = content()
    }

    var body: some View {
        VStack(spacing: 0) {
            Divider()
            HStack(spacing: 12) {
                content
            }
            .padding(.horizontal, 20)
            .padding(.vertical, 12)
        }
        .background(.bar)
    }
}
