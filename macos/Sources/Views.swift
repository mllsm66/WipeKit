import SwiftUI
import AppKit

// Hinweis: bewusst ohne SwiftUI-Makros (@State, #Preview …), damit der Build
// auch nur mit den Command Line Tools (ohne Xcode) funktioniert.

// MARK: - App

struct WipeKitApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate
    @ObservedObject private var model = AppModel.shared

    var body: some Scene {
        Window("WipeKit", id: "main") {
            RootView()
                .environmentObject(model)
        }
        .defaultSize(width: 980, height: 720)
        .windowResizability(.contentMinSize)
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }

    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        let active = AppModel.shared.jobs.filter { $0.state.isActive }.count
        guard active > 0 else { return .terminateNow }
        let alert = NSAlert()
        alert.messageText = active == 1 ? "Ein Löschvorgang läuft noch" : "\(active) Löschvorgänge laufen noch"
        alert.informativeText = "Das Löschen wird im Hintergrund fortgesetzt und der Mac bleibt wach. Öffne WipeKit erneut, um den Fortschritt zu sehen."
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
        }
    }
}

extension WipeJob {
    var tint: Color {
        switch state {
        case .done:      return Color.green
        case .failed:    return Color.red
        case .cancelled: return Color.orange
        default:         return Color.accentColor
        }
    }

    var shortStatus: String {
        switch state {
        case .starting:  return "Wird gestartet …"
        case .running:
            if let eta = eta { return "\(Fmt.percent(fraction)) · noch \(Fmt.shortDuration(eta))" }
            return Fmt.percent(fraction)
        case .done:      return "Erfolgreich gelöscht"
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
                .navigationSplitViewColumnWidth(min: 230, ideal: 260, max: 340)
        } detail: {
            DetailView()
        }
        .frame(minWidth: 860, minHeight: 600)
        .alert(item: $model.alertItem) { item in
            Alert(title: Text(item.title), message: Text(item.message), dismissButton: .default(Text("OK")))
        }
    }
}

// MARK: - Seitenleiste

struct SidebarView: View {
    @EnvironmentObject var model: AppModel

    var body: some View {
        List(selection: $model.selection) {
            Section("Externe Laufwerke") {
                if model.disks.isEmpty {
                    Text("Kein Laufwerk angeschlossen")
                        .font(.callout)
                        .foregroundStyle(.secondary)
                }
                ForEach(model.disks) { disk in
                    DiskSidebarRow(disk: disk, job: model.activeJob(for: disk))
                        .tag(SidebarItem.disk(disk.id))
                }
            }
            if !model.jobs.isEmpty {
                Section("Löschvorgänge") {
                    ForEach(model.jobs) { job in
                        JobSidebarRow(job: job)
                            .tag(SidebarItem.job(job.id))
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
            if disk.isProtected {
                Image(systemName: "lock.fill")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            } else if let job = job {
                JobMiniRing(job: job)
            }
        }
        .padding(.vertical, 3)
    }
}

struct JobSidebarRow: View {
    @ObservedObject var job: WipeJob

    var body: some View {
        HStack(spacing: 10) {
            JobStatusIcon(job: job)
                .frame(width: 24)
            VStack(alignment: .leading, spacing: 1) {
                Text(job.info.disk.name)
                    .lineLimit(1)
                Text(job.shortStatus)
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
            Image(systemName: "checkmark.circle.fill")
                .font(.system(size: 17))
                .foregroundStyle(Color.green)
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
                DiskDetailView(disk: disk)
            } else if model.disks.isEmpty {
                EmptyStateView(
                    symbol: "externaldrive.badge.plus",
                    title: "Kein Laufwerk angeschlossen",
                    message: "Schließe ein Laufwerk per USB an. Es erscheint automatisch in der Seitenleiste."
                )
            } else {
                EmptyStateView(
                    symbol: "externaldrive",
                    title: "Kein Laufwerk ausgewählt",
                    message: "Wähle in der Seitenleiste das Laufwerk aus, das gelöscht werden soll."
                )
            }
        }
        .toolbar {
            ToolbarItemGroup(placement: .primaryAction) {
                Button {
                    model.refresh()
                } label: {
                    Label("Aktualisieren", systemImage: "arrow.clockwise")
                }
                .help("Laufwerke neu einlesen")

                Button {
                    openProtocolFolder()
                } label: {
                    Label("Protokolle", systemImage: "doc.text.magnifyingglass")
                }
                .help("Ordner mit den Löschprotokollen öffnen")
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
                .frame(maxWidth: 340)
        }
        .padding(40)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

// MARK: - Laufwerk

struct DiskDetailView: View {
    let disk: ExternalDisk
    @EnvironmentObject var model: AppModel

    var body: some View {
        Form {
            Section {
                DiskHeader(disk: disk)
            }

            if disk.isProtected {
                Section {
                    Label {
                        Text("Auf diesem Laufwerk läuft macOS. Es ist geschützt und kann nicht gelöscht werden.")
                    } icon: {
                        Image(systemName: "lock.fill").foregroundStyle(.secondary)
                    }
                }
            } else if let job = model.activeJob(for: disk) {
                Section {
                    HStack {
                        Label("Dieses Laufwerk wird gerade gelöscht.", systemImage: "hourglass")
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
                            .onTapGesture { model.mode = m }
                    }
                } header: {
                    Text("Löschmethode")
                }

                ModeDetailSection(mode: model.mode, disk: disk)
            }

            Section("Informationen") {
                LabeledContent("Gerät", value: "/dev/\(disk.id)")
                LabeledContent("Kapazität", value: "\(Fmt.bytes(disk.size)) (\(Fmt.exactBytes(disk.size)))")
                LabeledContent("Anschluss", value: disk.bus)
                LabeledContent("Typ", value: disk.typeLabel)
                LabeledContent("Volumes", value: disk.volumes.isEmpty ? "Keine" : disk.volumes.joined(separator: ", "))
            }
        }
        .formStyle(.grouped)
        .safeAreaInset(edge: .bottom) {
            if !disk.isProtected && !model.isBusy(disk) {
                BottomBar {
                    Text("Alle Daten auf „\(disk.name)“ werden unwiderruflich gelöscht.")
                        .font(.callout)
                        .foregroundStyle(.secondary)
                        .lineLimit(2)
                    Spacer()
                    Button(role: .destructive) {
                        model.askConfirm(disk)
                    } label: {
                        Text("Löschen …")
                            .frame(minWidth: 90)
                    }
                    .buttonStyle(.borderedProminent)
                    .tint(Color.red)
                    .controlSize(.large)
                }
            }
        }
        .navigationTitle(disk.name)
        .navigationSubtitle("\(disk.id) · \(Fmt.bytes(disk.size))")
        .alert("„\(disk.name)“ löschen?", isPresented: $model.showConfirm) {
            Button("Löschen", role: .destructive) { model.confirmStart() }
            Button("Abbrechen", role: .cancel) {}
        } message: {
            Text("Alle Daten auf \(disk.id) (\(Fmt.bytes(disk.size))) werden mit der Methode „\(model.mode.title)“ unwiderruflich gelöscht. Dieser Vorgang kann nicht rückgängig gemacht werden.")
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
                Text("\(Fmt.bytes(disk.size)) · \(disk.typeLabel) · \(disk.bus)")
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
                        Text("Empfohlen")
                            .font(.caption2.weight(.semibold))
                            .foregroundStyle(Color.accentColor)
                            .padding(.horizontal, 6)
                            .padding(.vertical, 2)
                            .background(Capsule().fill(Color.accentColor.opacity(0.15)))
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

struct ModeDetailSection: View {
    let mode: WipeMode
    let disk: ExternalDisk

    private var estimate: TimeInterval {
        let speed: Double = disk.isSSD == true ? 300_000_000 : 130_000_000
        return Double(disk.size) * Double(mode.workUnits) / speed
    }

    private var hint: String? {
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
            LabeledContent("Geschätzte Dauer", value: "ca. \(Fmt.shortDuration(estimate))")
        } header: {
            Text("Über »\(mode.title)«")
        } footer: {
            VStack(alignment: .leading, spacing: 8) {
                Text(mode.securityNote)
                    .fixedSize(horizontal: false, vertical: true)
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
                ForEach(0..<4, id: \.self) { i in
                    Capsule()
                        .fill(i < level ? tint : Color.secondary.opacity(0.2))
                        .frame(width: 14, height: 6)
                }
            }
            Text(label)
        }
    }
}

// MARK: - Löschvorgang

struct JobDetailView: View {
    @ObservedObject var job: WipeJob
    @EnvironmentObject var model: AppModel

    var body: some View {
        Form {
            Section {
                JobHero(job: job)
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
                LabeledContent("Methode", value: "\(job.info.mode.title) · \(job.info.mode.tagline)")
            }
        }
        .formStyle(.grouped)
        .safeAreaInset(edge: .bottom) {
            bottomBar
        }
        .navigationTitle(job.info.disk.name)
        .navigationSubtitle(job.shortStatus)
        .confirmationDialog("Löschvorgang abbrechen?", isPresented: $job.askCancel) {
            Button("Löschvorgang abbrechen", role: .destructive) { job.requestCancel() }
            Button("Weiterlaufen lassen", role: .cancel) {}
        } message: {
            Text("Das Laufwerk ist danach nur teilweise überschrieben.")
        }
    }

    @ViewBuilder private var progressSection: some View {
        Section("Fortschritt") {
            if let s = job.status, s.state == .running {
                LabeledContent("Schritt", value: "\(s.step) von \(s.stepCount) · \(s.phase)")
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
            if job.info.mode.verifies {
                LabeledContent("Prüfung", value: verificationText)
            }
            if job.info.mode.trims {
                LabeledContent("TRIM") {
                    Text(job.status?.trimResult ?? "Nicht durchgeführt")
                        .multilineTextAlignment(.trailing)
                }
            }
            if let s = job.status, let end = s.finishedAt {
                LabeledContent("Beendet", value: Fmt.dateTime(end))
            }
        }
        if let url = job.protocolURL {
            Section("Protokoll") {
                LabeledContent(url.lastPathComponent) {
                    HStack {
                        Button("Öffnen") { NSWorkspace.shared.open(url) }
                        Button("Im Finder zeigen") { NSWorkspace.shared.activateFileViewerSelecting([url]) }
                    }
                }
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
                Text("Du kannst WipeKit schließen – das Löschen läuft im Hintergrund weiter.")
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
            Image(systemName: "checkmark")
                .font(.system(size: 48, weight: .semibold))
                .foregroundStyle(Color.green)
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
                if let s = job.status, s.state == .running {
                    Text("Schritt \(s.step) von \(s.stepCount)")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            }
        }
    }

    private var headline: String {
        switch job.state {
        case .starting:  return "Wird vorbereitet"
        case .running:   return "Wird gelöscht"
        case .done:      return "Löschen abgeschlossen"
        case .failed:    return "Löschen fehlgeschlagen"
        case .cancelled: return "Löschen abgebrochen"
        }
    }

    private var subline: String {
        switch job.state {
        case .starting:
            return job.status?.phase ?? "Bitte das Administrator-Passwort eingeben."
        case .running:
            return job.status?.phase ?? ""
        case .done:
            return "„\(job.info.disk.name)“ wurde sicher gelöscht."
        case .failed, .cancelled:
            return job.info.disk.name
        }
    }
}

// MARK: - Bausteine

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
