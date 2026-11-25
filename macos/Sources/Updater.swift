import Foundation
import AppKit
import CryptoKit

// MARK: - Updates aus den GitHub-Releases
//
// Ablauf: neuestes Release abfragen → ZIP und Signatur (.sig) laden → Signatur mit dem eingebauten
// öffentlichen Schlüssel prüfen (ECDSA P-256, SHA-256) → App austauschen und neu starten.
// Ohne gültige Signatur wird nichts installiert.

struct ReleaseInfo {
    let version: String
    let pageURL: URL
    let assetURL: URL?
    let signatureURL: URL?
    let assetName: String
}

final class Updater: ObservableObject {
    static let shared = Updater()
    static let repo = "mllsm66/WipeKit"

    @Published var busy = false
    @Published var statusText = "Noch nicht gesucht."
    @Published var available: ReleaseInfo?

    /// Öffentlicher Schlüssel (wird beim Bauen aus dem Signaturschlüssel erzeugt)
    static var publicKey: P256.Signing.PublicKey? {
        guard let url = Bundle.main.url(forResource: "update-public-key", withExtension: "pem"),
              let pem = try? String(contentsOf: url, encoding: .utf8) else { return nil }
        return try? P256.Signing.PublicKey(pemRepresentation: pem)
    }

    static func compare(_ a: String, _ b: String) -> Int {
        let pa = a.split(whereSeparator: { !$0.isNumber }).compactMap { Int($0) }
        let pb = b.split(whereSeparator: { !$0.isNumber }).compactMap { Int($0) }
        for i in 0..<max(pa.count, pb.count) {
            let x = i < pa.count ? pa[i] : 0, y = i < pb.count ? pb[i] : 0
            if x != y { return x < y ? -1 : 1 }
        }
        return 0
    }

    func canInstall(_ r: ReleaseInfo) -> Bool {
        r.assetURL != nil && r.signatureURL != nil && Updater.publicKey != nil && !AppModel.shared.hasActiveJobs
    }

    /// Beim Start (falls eingeschaltet)
    func checkOnLaunch() {
        let s = AppSettings.shared
        guard s.autoCheck, !AppModel.demoMode else { return }
        check(userInitiated: false) { [weak self] r in
            guard let self = self, let r = r, s.autoInstall else { return }
            self.installWhenIdle(r)
        }
    }

    func check(userInitiated: Bool, then: ((ReleaseInfo?) -> Void)? = nil) {
        busy = true
        statusText = "Suche nach Updates …"
        var req = URLRequest(url: URL(string: "https://api.github.com/repos/\(Updater.repo)/releases/latest")!)
        req.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        req.setValue("WipeKit/\(AppModel.appVersion)", forHTTPHeaderField: "User-Agent")
        req.timeoutInterval = 20
        URLSession.shared.dataTask(with: req) { data, resp, err in
            var info: ReleaseInfo?
            var text: String
            if let err = err {
                text = "Keine Verbindung: \(err.localizedDescription)"
            } else if let data = data, let j = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                      let tag = j["tag_name"] as? String {
                let v = tag.hasPrefix("v") ? String(tag.dropFirst()) : tag
                let page = (j["html_url"] as? String).flatMap(URL.init(string:))
                    ?? URL(string: "https://github.com/\(Updater.repo)/releases/latest")!
                let assets = (j["assets"] as? [[String: Any]]) ?? []
                func url(_ name: String) -> URL? {
                    (assets.first { ($0["name"] as? String) == name }?["browser_download_url"] as? String).flatMap(URL.init(string:))
                }
                let name = "WipeKit-macOS-\(v).zip"
                if Updater.compare(v, AppModel.appVersion) > 0 {
                    info = ReleaseInfo(version: v, pageURL: page, assetURL: url(name), signatureURL: url(name + ".sig"), assetName: name)
                    text = "Version \(v) ist verfügbar."
                    if info?.assetURL == nil { text += " (Die Mac-Datei fehlt noch im Release.)" }
                    else if info?.signatureURL == nil || Updater.publicKey == nil { text += " Automatisch installieren geht nicht (keine Signatur) – bitte von GitHub laden." }
                } else {
                    text = "WipeKit ist aktuell."
                }
            } else if let h = resp as? HTTPURLResponse, h.statusCode == 404 {
                text = "Noch keine Version veröffentlicht."
            } else {
                text = "Die Antwort von GitHub war unverständlich."
            }
            DispatchQueue.main.async {
                self.busy = false
                self.available = info
                let f = DateFormatter()
                f.locale = Locale(identifier: "de_DE")
                f.dateFormat = "HH:mm"
                self.statusText = text + " (\(f.string(from: Date())) Uhr)"
                then?(info)
            }
        }.resume()
    }

    /// Wartet, bis kein Vorgang mehr läuft
    private func installWhenIdle(_ r: ReleaseInfo) {
        guard canInstall(r) || AppModel.shared.hasActiveJobs else { return }
        if AppModel.shared.hasActiveJobs {
            statusText = "Version \(r.version) wird nach dem laufenden Vorgang installiert."
            DispatchQueue.main.asyncAfter(deadline: .now() + 60) { [weak self] in self?.installWhenIdle(r) }
            return
        }
        install(r)
    }

    func install(_ r: ReleaseInfo) {
        guard let assetURL = r.assetURL, let sigURL = r.signatureURL, let key = Updater.publicKey else {
            NSWorkspace.shared.open(r.pageURL)
            return
        }
        guard !AppModel.shared.hasActiveJobs else {
            statusText = "Erst nach dem laufenden Vorgang möglich."
            return
        }
        busy = true
        statusText = "Lade Version \(r.version) …"
        DispatchQueue.global(qos: .userInitiated).async {
            let result = Updater.downloadAndPrepare(assetURL: assetURL, sigURL: sigURL, key: key, version: r.version)
            DispatchQueue.main.async {
                self.busy = false
                switch result {
                case .failure(let msg):
                    self.statusText = msg
                case .success(let newApp):
                    self.statusText = "Installiere Version \(r.version) …"
                    self.replaceAndRelaunch(with: newApp, version: r.version, page: r.pageURL)
                }
            }
        }
    }

    private enum Prepared { case success(URL), failure(String) }

    private static func fetch(_ u: URL) -> Data? {
        var data: Data?
        let sem = DispatchSemaphore(value: 0)
        var req = URLRequest(url: u)
        req.setValue("WipeKit/\(AppModel.appVersion)", forHTTPHeaderField: "User-Agent")
        req.timeoutInterval = 120
        URLSession.shared.dataTask(with: req) { d, resp, _ in
            if let h = resp as? HTTPURLResponse, h.statusCode == 200 { data = d }
            sem.signal()
        }.resume()
        sem.wait()
        return data
    }

    private static func downloadAndPrepare(assetURL: URL, sigURL: URL, key: P256.Signing.PublicKey, version: String) -> Prepared {
        guard let zip = fetch(assetURL), let sig = fetch(sigURL) else { return .failure("Download fehlgeschlagen.") }
        guard let signature = try? P256.Signing.ECDSASignature(derRepresentation: sig), key.isValidSignature(signature, for: zip) else {
            return .failure("Die Signatur des Updates ist ungültig – es wurde nichts installiert.")
        }
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("WipeKit-Update-\(UUID().uuidString)", isDirectory: true)
        do {
            try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
            let zipURL = dir.appendingPathComponent("update.zip")
            try zip.write(to: zipURL)
            let r = Shell.runWithOutput("/usr/bin/ditto", ["-x", "-k", zipURL.path, dir.path])
            guard r.status == 0 else { return .failure("Entpacken fehlgeschlagen: \(r.output)") }
            let app = dir.appendingPathComponent("WipeKit.app")
            guard let b = Bundle(url: app), b.bundleIdentifier == Bundle.main.bundleIdentifier,
                  (b.infoDictionary?["CFBundleShortVersionString"] as? String) == version else {
                return .failure("Das Update enthält keine passende WipeKit-App.")
            }
            return .success(app)
        } catch {
            return .failure("Update konnte nicht vorbereitet werden: \(error.localizedDescription)")
        }
    }

    /// Tauscht die App aus, sobald WipeKit beendet ist, und startet sie neu.
    private func replaceAndRelaunch(with newApp: URL, version: String, page: URL) {
        let target = Bundle.main.bundleURL
        let parent = target.deletingLastPathComponent()
        guard FileManager.default.isWritableFile(atPath: parent.path) else {
            statusText = "Keine Schreibrechte für \(parent.path) – das neue WipeKit liegt im Finder bereit."
            NSWorkspace.shared.activateFileViewerSelecting([newApp])
            return
        }
        let q = AppModel.shellQuote
        let backup = target.path + ".alt"
        let script = """
        while kill -0 \(getpid()) 2>/dev/null; do sleep 0.3; done
        rm -rf \(q(backup))
        if mv \(q(target.path)) \(q(backup)) && mv \(q(newApp.path)) \(q(target.path)); then
          rm -rf \(q(backup))
        else
          [ -d \(q(backup)) ] && [ ! -d \(q(target.path)) ] && mv \(q(backup)) \(q(target.path))
          open -R \(q(newApp.path))
        fi
        open \(q(target.path))
        """
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/bin/sh")
        p.arguments = ["-c", script]
        do {
            try p.run()
            NSApplication.shared.terminate(nil)
        } catch {
            statusText = "Update konnte nicht gestartet werden – das neue WipeKit liegt im Finder bereit."
            NSWorkspace.shared.activateFileViewerSelecting([newApp])
        }
    }
}
