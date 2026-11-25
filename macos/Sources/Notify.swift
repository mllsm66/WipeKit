import Foundation
import SwiftUI
import AppKit
import CryptoKit
import UserNotifications

// MARK: - Benachrichtigungen aufs Handy: WipeKit Hub, ntfy, Discord

enum PushService: String, Codable, CaseIterable, Identifiable, Equatable {
    case off, hub, ntfy, discord
    var id: String { rawValue }
    var title: String {
        switch self {
        case .off: return "Aus"
        case .hub: return "WipeKit Hub (selbst gehostet)"
        case .ntfy: return "ntfy"
        case .discord: return "Discord"
        }
    }
}

enum NtfyAuth: String, Codable, CaseIterable, Identifiable {
    case none, token, password
    var id: String { rawValue }
    var title: String {
        switch self {
        case .none: return "Keine"
        case .token: return "Zugriffstoken"
        case .password: return "Benutzer und Passwort"
        }
    }
}

/// Momentaufnahme der Einstellungen – wird jedem Vorgang mitgegeben (der Lösch-Prozess sendet selbst,
/// auch wenn WipeKit geschlossen ist). Enthält Zugangsdaten: Datei nur für den Benutzer lesbar.
struct PushConfig: Codable, Equatable {
    var service: PushService = .off
    var onDone = true, onFailed = true, onCancelled = true, onWarning = true
    var hubCode = ""
    var hubFiles = true
    var ntfyServer = "https://ntfy.sh"
    var ntfyTopic = ""
    var ntfyAuth: NtfyAuth = .none
    var ntfyUser = ""
    var ntfySecret = ""          // Token oder Passwort
    var discordWebhook = ""

    var isConfigured: Bool {
        switch service {
        case .off: return false
        case .hub: return HubCode.parse(hubCode) != nil
        case .ntfy: return URL(string: ntfyServer)?.scheme?.hasPrefix("http") == true && !ntfyTopic.isEmpty
        case .discord: return Push.validDiscord(discordWebhook)
        }
    }
}

final class NotifySettings: ObservableObject {
    static let shared = NotifySettings()
    private let d = UserDefaults.standard
    @Published var config = PushConfig()
    @Published var testResult: String?
    @Published var testing = false

    private init() { reload() }

    func reload() {
        var c = PushConfig()
        if let data = d.data(forKey: "push"), let saved = try? JSONDecoder().decode(PushConfig.self, from: data) { c = saved }
        c.hubCode = Keychain.get("hubCode")
        c.ntfySecret = Keychain.get("ntfySecret")
        c.discordWebhook = Keychain.get("discordWebhook")
        config = c
    }

    func save() {
        var plain = config
        plain.hubCode = ""; plain.ntfySecret = ""; plain.discordWebhook = ""
        if let data = try? JSONEncoder().encode(plain) { d.set(data, forKey: "push") }
        Keychain.set("hubCode", config.hubCode)
        Keychain.set("ntfySecret", config.ntfySecret)
        Keychain.set("discordWebhook", config.discordWebhook)
    }

    func sendTest() {
        save()
        testing = true
        testResult = nil
        let c = config
        DispatchQueue.global(qos: .userInitiated).async {
            let r = Push.send(c, PushEvent(kind: "test", state: "done", warning: false,
                                           notice: "Testnachricht von WipeKit auf »\(MacInfo.host)« – alles eingerichtet.",
                                           meta: ["host": MacInfo.host, "platform": MacInfo.osLabel, "app": AppModel.appVersion],
                                           files: []), force: true)
            DispatchQueue.main.async {
                self.testing = false
                self.testResult = r ?? "Gesendet."
            }
        }
    }
}

/// Ein Ereignis für die Benachrichtigung
struct PushEvent {
    var kind: String          // wipe | check | test
    var state: String         // done | failed | cancelled
    var warning: Bool
    var notice: String
    var meta: [String: Any]
    var files: [(String, Data)]
}

struct HubCode {
    let url: URL
    let token: String
    let key: Data

    static func parse(_ code: String) -> HubCode? {
        let t = code.trimmingCharacters(in: .whitespacesAndNewlines)
        guard t.hasPrefix("WKHUB1:") else { return nil }
        var b = String(t.dropFirst(7)).replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/")
        while b.count % 4 != 0 { b += "=" }
        guard let data = Data(base64Encoded: b),
              let j = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let u = (j["url"] as? String).flatMap(URL.init(string:)), u.scheme == "https" || u.scheme == "http",
              let token = j["token"] as? String, !token.isEmpty,
              let k = (j["key"] as? String).flatMap({ Data(base64Encoded: $0) }), k.count == 65
        else { return nil }
        return HubCode(url: u, token: token, key: k)
    }
}

enum Push {
    static func validDiscord(_ s: String) -> Bool {
        guard let u = URL(string: s.trimmingCharacters(in: .whitespaces)), u.scheme == "https",
              let h = u.host?.lowercased() else { return false }
        return (h == "discord.com" || h == "discordapp.com" || h.hasSuffix(".discord.com")) && u.path.hasPrefix("/api/webhooks/")
    }

    /// Verschlüsselt für den Hub („WKE1“, siehe docs/hub-protocol.md)
    static func seal(_ plain: Data, to pub: Data) throws -> Data {
        let recipient = try P256.KeyAgreement.PublicKey(x963Representation: pub)
        let eph = P256.KeyAgreement.PrivateKey()
        let shared = try eph.sharedSecretFromKeyAgreement(with: recipient)
        let ephPub = eph.publicKey.x963Representation
        let key = shared.hkdfDerivedSymmetricKey(using: SHA256.self, salt: ephPub + pub,
                                                 sharedInfo: Data("WipeKit Hub v1".utf8), outputByteCount: 32)
        let box = try AES.GCM.seal(plain, using: key, authenticating: Data("WKE1".utf8))
        guard let combined = box.combined else { throw CocoaError(.coderInvalidValue) }
        return Data("WKE1".utf8) + ephPub + combined
    }

    /// Sendet synchron. Gibt nil bei Erfolg zurück, sonst eine Fehlermeldung.
    @discardableResult
    static func send(_ c: PushConfig, _ e: PushEvent, force: Bool = false) -> String? {
        if !force {
            let wanted = (e.state == "done" && c.onDone) || (e.state == "failed" && c.onFailed)
                || (e.state == "cancelled" && c.onCancelled) || (e.warning && c.onWarning)
            if !wanted { return nil }
        }
        switch c.service {
        case .off: return "Keine Benachrichtigung eingerichtet."
        case .hub: return sendHub(c, e)
        case .ntfy: return sendNtfy(c, e)
        case .discord: return sendDiscord(c, e)
        }
    }

    private static func request(_ r: URLRequest) -> String? {
        var result: String? = "Keine Antwort."
        let sem = DispatchSemaphore(value: 0)
        let cfg = URLSessionConfiguration.ephemeral
        cfg.timeoutIntervalForRequest = 30
        URLSession(configuration: cfg).dataTask(with: r) { data, resp, err in
            if let err = err { result = err.localizedDescription }
            else if let h = resp as? HTTPURLResponse {
                if (200..<300).contains(h.statusCode) { result = nil }
                else {
                    let body = data.flatMap { String(data: $0.prefix(200), encoding: .utf8) } ?? ""
                    result = "Server antwortet mit \(h.statusCode). \(body)".trimmingCharacters(in: .whitespaces)
                }
            }
            sem.signal()
        }.resume()
        _ = sem.wait(timeout: .now() + 60)
        return result
    }

    private static func sendHub(_ c: PushConfig, _ e: PushEvent) -> String? {
        guard let hub = HubCode.parse(c.hubCode) else { return "Der Kopplungscode für den Hub ist ungültig." }
        var meta = e.meta
        let files = c.hubFiles ? e.files : []
        meta["files"] = files.map { $0.0 }
        do {
            let metaData = try JSONSerialization.data(withJSONObject: meta)
            let body: [String: Any] = [
                "v": 1, "id": UUID().uuidString, "created": ISO8601DateFormatter().string(from: Date()),
                "kind": e.kind, "state": e.state, "warning": e.warning, "notice": e.notice,
                "meta": try seal(metaData, to: hub.key).base64EncodedString(),
                "files": try files.map { try seal($0.1, to: hub.key).base64EncodedString() },
            ]
            var r = URLRequest(url: hub.url.appendingPathComponent("api/v1/events"))
            r.httpMethod = "POST"
            r.setValue("Bearer \(hub.token)", forHTTPHeaderField: "Authorization")
            r.setValue("application/json", forHTTPHeaderField: "Content-Type")
            r.setValue("WipeKit/\(AppModel.appVersion)", forHTTPHeaderField: "User-Agent")
            r.httpBody = try JSONSerialization.data(withJSONObject: body)
            return request(r)
        } catch {
            return "Verschlüsseln fehlgeschlagen: \(error.localizedDescription)"
        }
    }

    private static func sendNtfy(_ c: PushConfig, _ e: PushEvent) -> String? {
        guard let base = URL(string: c.ntfyServer.trimmingCharacters(in: .whitespaces)), !c.ntfyTopic.isEmpty else {
            return "Server oder Thema für ntfy fehlt."
        }
        var r = URLRequest(url: base.appendingPathComponent(c.ntfyTopic.trimmingCharacters(in: .whitespaces)))
        r.httpMethod = "POST"
        r.httpBody = Data(e.notice.utf8)
        r.setValue("WipeKit", forHTTPHeaderField: "Title")
        r.setValue(e.state == "done" && !e.warning ? "white_check_mark" : (e.state == "cancelled" ? "warning" : "x"),
                   forHTTPHeaderField: "Tags")
        if e.state == "failed" || e.warning { r.setValue("high", forHTTPHeaderField: "Priority") }
        switch c.ntfyAuth {
        case .none: break
        case .token: r.setValue("Bearer \(c.ntfySecret)", forHTTPHeaderField: "Authorization")
        case .password:
            let b = Data("\(c.ntfyUser):\(c.ntfySecret)".utf8).base64EncodedString()
            r.setValue("Basic \(b)", forHTTPHeaderField: "Authorization")
        }
        return request(r)
    }

    private static func sendDiscord(_ c: PushConfig, _ e: PushEvent) -> String? {
        guard validDiscord(c.discordWebhook), let u = URL(string: c.discordWebhook.trimmingCharacters(in: .whitespaces)) else {
            return "Die Webhook-Adresse für Discord ist ungültig."
        }
        let color = e.state == "done" && !e.warning ? 0x1F8F3D : (e.state == "cancelled" || e.warning ? 0xDB8C00 : 0xD93026)
        let body: [String: Any] = ["username": "WipeKit", "allowed_mentions": ["parse": []],
                                   "embeds": [["title": "WipeKit", "description": e.notice, "color": color]]]
        var r = URLRequest(url: u)
        r.httpMethod = "POST"
        r.setValue("application/json", forHTTPHeaderField: "Content-Type")
        r.httpBody = try? JSONSerialization.data(withJSONObject: body)
        return request(r)
    }
}

// MARK: - Mitteilungen am Mac (oben rechts)

enum LocalNotify {
    static func requestPermission() {
        UNUserNotificationCenter.current().requestAuthorization(options: [.alert, .sound]) { _, _ in }
    }

    static func post(title: String, body: String, sound: Bool) {
        let c = UNMutableNotificationContent()
        c.title = title
        c.body = body
        if sound { c.sound = .default }
        let req = UNNotificationRequest(identifier: UUID().uuidString, content: c, trigger: nil)
        UNUserNotificationCenter.current().add(req, withCompletionHandler: nil)
    }

    /// Aus dem Lösch-Prozess (root), wenn WipeKit geschlossen ist: Mitteilung in der Sitzung des Benutzers
    static func postAsUser(uid: uid_t, title: String, body: String, sound: Bool) {
        let esc = { (s: String) in s.replacingOccurrences(of: "\\", with: "\\\\").replacingOccurrences(of: "\"", with: "\\\"") }
        var script = "display notification \"\(esc(body))\" with title \"\(esc(title))\""
        if sound { script += " sound name \"Glass\"" }
        Shell.run("/bin/launchctl", ["asuser", String(uid), "/usr/bin/sudo", "-u", "#\(uid)", "/usr/bin/osascript", "-e", script])
    }
}

/// Zeigt Mitteilungen auch, während WipeKit im Vordergrund ist
final class NotificationDelegate: NSObject, UNUserNotificationCenterDelegate {
    static let shared = NotificationDelegate()
    func userNotificationCenter(_ center: UNUserNotificationCenter, willPresent notification: UNNotification,
                                withCompletionHandler completionHandler: @escaping (UNNotificationPresentationOptions) -> Void) {
        completionHandler([.banner, .sound])
    }
}

// MARK: - Einstellungen: Benachrichtigungen

struct NotificationsPane: View {
    @ObservedObject var n = NotifySettings.shared
    @State private var showSecurity = false

    var body: some View {
        Form {
            Section {
                Picker("Dienst", selection: $n.config.service) {
                    ForEach(PushService.allCases) { Text($0.title).tag($0) }
                }
            } footer: {
                Text("Schickt eine Nachricht aufs Handy, wenn ein Vorgang endet – auch wenn WipeKit geschlossen ist.")
                    .font(.caption).foregroundStyle(.secondary)
            }

            switch n.config.service {
            case .off:
                EmptyView()
            case .hub:
                Section {
                    TextField("Kopplungscode", text: $n.config.hubCode, prompt: Text("WKHUB1:…"), axis: .vertical)
                        .lineLimit(1...3)
                        .font(.system(.body, design: .monospaced))
                    if let h = HubCode.parse(n.config.hubCode) {
                        LabeledContent("Hub", value: h.url.absoluteString)
                    } else if !n.config.hubCode.isEmpty {
                        Text("Der Code ist ungültig. Kopiere ihn im Hub unter »Geräte« erneut.").foregroundStyle(.red).font(.callout)
                    }
                    Toggle("Protokolle (PDF) mitsenden", isOn: $n.config.hubFiles)
                } header: {
                    Text("WipeKit Hub")
                } footer: {
                    Text("Protokolle und Laufwerksdaten werden auf diesem Mac verschlüsselt. Nur dein Browser kann sie mit deinem Hub-Passwort öffnen – der Server selbst nicht. Lesbar ist für ihn nur die kurze Mitteilung.")
                        .font(.caption).foregroundStyle(.secondary)
                }
            case .ntfy:
                Section {
                    TextField("Server", text: $n.config.ntfyServer, prompt: Text("https://ntfy.sh"))
                    TextField("Thema", text: $n.config.ntfyTopic, prompt: Text("langer, zufälliger Name"))
                    Picker("Anmeldung", selection: $n.config.ntfyAuth) {
                        ForEach(NtfyAuth.allCases) { Text($0.title).tag($0) }
                    }
                    if n.config.ntfyAuth == .password {
                        TextField("Benutzer", text: $n.config.ntfyUser)
                    }
                    if n.config.ntfyAuth != .none {
                        SecureField(n.config.ntfyAuth == .token ? "Token" : "Passwort", text: $n.config.ntfySecret)
                    }
                } header: {
                    Text("ntfy")
                } footer: {
                    VStack(alignment: .leading, spacing: 4) {
                        Text("Gesendet wird nur die kurze Mitteilung, keine Protokolle. Auf dem öffentlichen Server ntfy.sh kann jeder mitlesen, der das Thema kennt – wähle einen langen, zufälligen Namen oder einen eigenen Server mit Anmeldung.")
                        Button("Mehr zur Sicherheit …") { showSecurity = true }.buttonStyle(.link)
                    }
                    .font(.caption).foregroundStyle(.secondary)
                }
            case .discord:
                Section {
                    SecureField("Webhook-Adresse", text: $n.config.discordWebhook, prompt: Text("https://discord.com/api/webhooks/…"))
                    if !n.config.discordWebhook.isEmpty && !Push.validDiscord(n.config.discordWebhook) {
                        Text("Das ist keine Webhook-Adresse von Discord.").foregroundStyle(.red).font(.callout)
                    }
                } header: {
                    Text("Discord")
                } footer: {
                    Text("Gesendet wird nur die kurze Mitteilung, keine Protokolle – Discord-Nachrichten sind nicht Ende-zu-Ende-verschlüsselt. Den Webhook legst du in Discord unter Kanal bearbeiten › Integrationen an.")
                        .font(.caption).foregroundStyle(.secondary)
                }
            }

            if n.config.service != .off {
                Section("Benachrichtigen bei") {
                    Toggle("Erfolgreich beendet", isOn: $n.config.onDone)
                    Toggle("Fehlgeschlagen", isOn: $n.config.onFailed)
                    Toggle("Abgebrochen", isOn: $n.config.onCancelled)
                    Toggle("Gesundheitswarnung", isOn: $n.config.onWarning)
                }
                Section {
                    HStack {
                        if n.testing { ProgressView().controlSize(.small) }
                        if let t = n.testResult { Text(t).foregroundStyle(.secondary).font(.callout) }
                        Spacer()
                        Button("Testnachricht senden") { n.sendTest() }
                            .disabled(!n.config.isConfigured || n.testing)
                    }
                }
            }
        }
        .formStyle(.grouped)
        .onChange(of: n.config) { _ in n.save() }
        .sheet(isPresented: $showSecurity) { NtfySecuritySheet() }
    }
}

private struct NtfySecuritySheet: View {
    @Environment(\.dismiss) private var dismiss
    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("ntfy und Sicherheit").font(.title3.bold())
            Text("• **Öffentlicher Server (ntfy.sh):** Es gibt keine Anmeldung. Wer den Namen des Themas kennt, kann alle Mitteilungen lesen. Ein langer, zufälliger Name schützt wie ein Passwort.")
            Text("• **Eigener Server:** Mit Anmeldung (Token oder Passwort) und Zugriff nur für dich. Für sofortige Mitteilungen auf dem iPhone leitet auch ein eigener Server einen Weckruf über ntfy.sh weiter – ohne Inhalt.")
            Text("• **Inhalt:** WipeKit sendet über ntfy nur die kurze Mitteilung (Laufwerk und Ergebnis), nie Protokolle oder Seriennummern. Für verschlüsselte Protokolle nutze den WipeKit Hub.")
            HStack { Spacer(); Button("Fertig") { dismiss() }.keyboardShortcut(.defaultAction) }
        }
        .padding(20)
        .frame(width: 460)
    }
}
