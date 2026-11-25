import Foundation
import SwiftUI
import AppKit
import Security

// MARK: - Einstellungen (UserDefaults; Geheimnisse im Schlüsselbund)

final class AppSettings: ObservableObject {
    static let shared = AppSettings()
    private let d = UserDefaults.standard

    // Allgemein
    @Published var notifyLocal: Bool { didSet { d.set(notifyLocal, forKey: "notifyLocal") } }
    @Published var playSound: Bool { didSet { d.set(playSound, forKey: "playSound") } }

    // Löschen
    @Published var defaultMode: WipeMode { didSet { d.set(defaultMode.rawValue, forKey: "defaultMode") } }
    @Published var ejectAfter: Bool { didSet { d.set(ejectAfter, forKey: "ejectAfter") } }
    @Published var usbHealth: Bool { didSet { d.set(usbHealth, forKey: "usbHealth") } }

    // Berichte
    @Published var reportsPath: String { didSet { d.set(reportsPath, forKey: "reportsPath") } }
    @Published var detailedReport: Bool { didSet { d.set(detailedReport, forKey: "detailedReport") } }
    @Published var serialInFileName: Bool { didSet { d.set(serialInFileName, forKey: "serialInFileName") } }
    @Published var operatorName: String { didSet { d.set(operatorName, forKey: "operatorName") } }
    @Published var organization: String { didSet { d.set(organization, forKey: "organization") } }
    @Published var reportNote: String { didSet { d.set(reportNote, forKey: "reportNote") } }

    // Updates
    @Published var autoCheck: Bool { didSet { d.set(autoCheck, forKey: "autoCheck") } }
    @Published var autoInstall: Bool { didSet { d.set(autoInstall, forKey: "autoInstall") } }
    @Published var updatesAsked: Bool { didSet { d.set(updatesAsked, forKey: "updatesAsked") } }

    static var defaultReportsPath: String {
        (FileManager.default.urls(for: .documentDirectory, in: .userDomainMask).first?
            .appendingPathComponent("WipeKit-Protokolle", isDirectory: true).path) ?? NSHomeDirectory() + "/Documents/WipeKit-Protokolle"
    }

    private init() {
        func bool(_ k: String, _ def: Bool) -> Bool { d.object(forKey: k) == nil ? def : d.bool(forKey: k) }
        func str(_ k: String, _ def: String = "") -> String { d.string(forKey: k) ?? def }
        notifyLocal = bool("notifyLocal", true)
        playSound = bool("playSound", true)
        defaultMode = WipeMode(rawValue: str("defaultMode", "secure")).flatMap { $0.availableOnMac ? $0 : nil } ?? .secure
        ejectAfter = bool("ejectAfter", false)
        usbHealth = bool("usbHealth", true)
        reportsPath = str("reportsPath", AppSettings.defaultReportsPath)
        detailedReport = bool("detailedReport", true)
        serialInFileName = bool("serialInFileName", true)
        operatorName = str("operatorName")
        organization = str("organization")
        reportNote = str("reportNote")
        autoCheck = bool("autoCheck", false)
        autoInstall = bool("autoInstall", false)
        updatesAsked = bool("updatesAsked", false)
    }

    /// Alles auf Anfang (Schlüsselbund-Einträge inklusive)
    func reset() {
        if let id = Bundle.main.bundleIdentifier { d.removePersistentDomain(forName: id) }
        Keychain.removeAll()
        notifyLocal = true; playSound = true
        defaultMode = .secure; ejectAfter = false; usbHealth = true
        reportsPath = AppSettings.defaultReportsPath; detailedReport = true; serialInFileName = true
        operatorName = ""; organization = ""; reportNote = ""
        autoCheck = false; autoInstall = false; updatesAsked = true
        NotifySettings.shared.reload()
    }

    /// Angaben für die Berichte („Durchgeführt von“ …)
    var reportMeta: [(String, String)] {
        var rows: [(String, String)] = []
        let o = operatorName.trimmingCharacters(in: .whitespacesAndNewlines)
        let f = organization.trimmingCharacters(in: .whitespacesAndNewlines)
        let n = reportNote.trimmingCharacters(in: .whitespacesAndNewlines)
        if !o.isEmpty { rows.append(("Durchgeführt von", o)) }
        if !f.isEmpty { rows.append(("Firma / Organisation", f)) }
        if !n.isEmpty { rows.append(("Notiz", n)) }
        return rows
    }
}

// MARK: - Schlüsselbund (nur für Zugangsdaten der Benachrichtigungen)

enum Keychain {
    private static let service = "io.github.mllsm66.wipekit"

    static func get(_ key: String) -> String {
        var item: CFTypeRef?
        let q: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service,
                                kSecAttrAccount as String: key, kSecReturnData as String: true,
                                kSecMatchLimit as String: kSecMatchLimitOne]
        guard SecItemCopyMatching(q as CFDictionary, &item) == errSecSuccess, let data = item as? Data else { return "" }
        return String(data: data, encoding: .utf8) ?? ""
    }

    static func set(_ key: String, _ value: String) {
        let q: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service,
                                kSecAttrAccount as String: key]
        SecItemDelete(q as CFDictionary)
        guard !value.isEmpty else { return }
        var add = q
        add[kSecValueData as String] = Data(value.utf8)
        add[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
        SecItemAdd(add as CFDictionary, nil)
    }

    static func removeAll() {
        let q: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service]
        SecItemDelete(q as CFDictionary)
    }
}

// MARK: - Einstellungsfenster (⌘,)

struct SettingsView: View {
    @State private var tab: Int
    init(tab: Int = 0) { _tab = State(initialValue: tab) }

    var body: some View {
        TabView(selection: $tab) {
            GeneralPane().tabItem { Label("Allgemein", systemImage: "gearshape") }.tag(0)
            WipePane().tabItem { Label("Löschen", systemImage: "eraser") }.tag(1)
            ReportsPane().tabItem { Label("Berichte", systemImage: "doc.text") }.tag(2)
            NotificationsPane().tabItem { Label("Benachrichtigungen", systemImage: "bell.badge") }.tag(3)
            UpdatesPane().tabItem { Label("Updates", systemImage: "arrow.down.circle") }.tag(4)
        }
        .frame(width: 560)
    }
}

private struct GeneralPane: View {
    @ObservedObject var s = AppSettings.shared
    @State private var askReset = false

    var body: some View {
        Form {
            Section {
                Toggle("Mitteilung anzeigen, wenn ein Vorgang endet", isOn: $s.notifyLocal)
                Toggle("Ton abspielen, wenn ein Vorgang endet", isOn: $s.playSound)
            } footer: {
                Text("Auch wenn WipeKit geschlossen ist, meldet sich der Vorgang beim Ende mit einer Mitteilung.")
                    .font(.caption).foregroundStyle(.secondary)
            }
            Section {
                LabeledContent("Ruhezustand") {
                    Text("Während eines Vorgangs bleibt der Mac automatisch wach.").foregroundStyle(.secondary)
                }
            }
            Section {
                HStack {
                    Spacer()
                    Button("Alle Einstellungen zurücksetzen …") { askReset = true }
                }
            }
        }
        .formStyle(.grouped)
        .confirmationDialog("Alle Einstellungen zurücksetzen?", isPresented: $askReset) {
            Button("Zurücksetzen", role: .destructive) { s.reset() }
        } message: {
            Text("Auch gespeicherte Zugangsdaten für Benachrichtigungen werden gelöscht. Berichte bleiben erhalten.")
        }
    }
}

private struct WipePane: View {
    @ObservedObject var s = AppSettings.shared

    var body: some View {
        Form {
            Section {
                Picker("Vorausgewählte Methode", selection: $s.defaultMode) {
                    ForEach(WipeMode.allCases.filter { $0.availableOnMac && !$0.readOnly }) { m in
                        Text(m.title).tag(m)
                    }
                }
                Toggle("Laufwerk nach erfolgreichem Löschen auswerfen", isOn: $s.ejectAfter)
            }
            Section {
                Toggle("Gesundheitsdaten über den USB-Adapter lesen", isOn: $s.usbHealth)
            } footer: {
                Text("Vor und nach dem Löschen. Das Laufwerk wird dafür jeweils kurz ausgeworfen. Ausgeschaltet stehen bei vielen USB-Laufwerken keine Gesundheitsdaten im Protokoll.")
                    .font(.caption).foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
    }
}

private struct ReportsPane: View {
    @ObservedObject var s = AppSettings.shared

    var body: some View {
        Form {
            Section {
                LabeledContent("Speicherort") {
                    HStack {
                        Text((s.reportsPath as NSString).abbreviatingWithTildeInPath)
                            .lineLimit(1).truncationMode(.middle)
                            .foregroundStyle(.secondary)
                        Button("Ändern …") { choose() }
                    }
                }
                Toggle("Ausführliches Protokoll zusätzlich erstellen", isOn: $s.detailedReport)
                Toggle("Seriennummer im Dateinamen", isOn: $s.serialInFileName)
            } footer: {
                Text("In den Unterordnern „PDF“ und „Text“.").font(.caption).foregroundStyle(.secondary)
            }
            Section("Angaben im Protokoll") {
                TextField("Durchgeführt von", text: $s.operatorName, prompt: Text("Name"))
                TextField("Firma / Organisation", text: $s.organization, prompt: Text("optional"))
                TextField("Notiz", text: $s.reportNote, prompt: Text("z. B. Auftragsnummer"), axis: .vertical)
                    .lineLimit(1...3)
            }
        }
        .formStyle(.grouped)
    }

    private func choose() {
        let p = NSOpenPanel()
        p.canChooseDirectories = true
        p.canChooseFiles = false
        p.canCreateDirectories = true
        p.prompt = "Auswählen"
        p.directoryURL = URL(fileURLWithPath: s.reportsPath)
        if p.runModal() == .OK, let u = p.url { s.reportsPath = u.path }
    }
}

private struct UpdatesPane: View {
    @ObservedObject var s = AppSettings.shared
    @ObservedObject var u = Updater.shared

    var body: some View {
        Form {
            Section {
                LabeledContent("Installierte Version", value: AppModel.appVersion)
                LabeledContent("Status") {
                    HStack(spacing: 8) {
                        if u.busy { ProgressView().controlSize(.small) }
                        Text(u.statusText).foregroundStyle(.secondary).multilineTextAlignment(.trailing)
                    }
                }
                HStack {
                    Spacer()
                    if let r = u.available, !u.busy {
                        Button("Versionshinweise") { NSWorkspace.shared.open(r.pageURL) }
                        Button("Version \(r.version) installieren") { u.install(r) }
                            .buttonStyle(.borderedProminent)
                            .disabled(!u.canInstall(r))
                    }
                    Button("Jetzt suchen") { u.check(userInitiated: true) }
                        .disabled(u.busy)
                }
            }
            Section {
                Toggle("Beim Start automatisch nach Updates suchen", isOn: $s.autoCheck)
                Toggle("Updates automatisch laden und installieren", isOn: $s.autoInstall)
                    .disabled(!s.autoCheck)
            } footer: {
                Text("Updates kommen aus den Releases auf GitHub und werden vor der Installation anhand ihrer digitalen Signatur geprüft. Während ein Vorgang läuft, wird nichts installiert.")
                    .font(.caption).foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
    }
}
