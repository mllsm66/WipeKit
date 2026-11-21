import Foundation
import IOKit.pwr_mgt
import CryptoKit

/// Lösch-Prozess. Läuft als root (gestartet über die Administrator-Passwortabfrage).
///
/// Ablauf:  App → (Passwort) → `--wipe-helper` (Starter) → startet `--wipe-worker` in einer eigenen
/// Sitzung und kehrt sofort zurück. Der Worker löscht, schreibt den Fortschritt nach
/// <job-dir>/status.json und bricht ab, sobald <job-dir>/cancel existiert.
/// Ausgaben landen in <job-dir>/helper.log.
enum WipeHelper {
    static func parse(_ arguments: [String]) -> [String: String] {
        var opts: [String: String] = [:]
        var i = 0
        while i < arguments.count {
            let a = arguments[i]
            if a.hasPrefix("--") {
                if i + 1 < arguments.count, !arguments[i + 1].hasPrefix("--") {
                    opts[String(a.dropFirst(2))] = arguments[i + 1]
                    i += 2
                } else {
                    opts[String(a.dropFirst(2))] = "1"
                    i += 1
                }
            } else {
                i += 1
            }
        }
        return opts
    }

    /// Starter: übergibt den eigentlichen Lösch-Prozess an launchd (läuft dann völlig unabhängig von
    /// App und Passwortabfrage weiter) und beendet sich sofort. Notfalls direkter Start in eigener Sitzung.
    static func launch(arguments: [String]) -> Int32 {
        let opts = parse(arguments)
        guard let dirPath = opts["job-dir"], !dirPath.isEmpty else {
            FileHandle.standardError.write("Kein Arbeitsordner angegeben.\n".data(using: .utf8)!)
            return 2
        }
        let dir = URL(fileURLWithPath: dirPath, isDirectory: true)
        let logPath = dir.appendingPathComponent("helper.log").path
        let exe = Bundle.main.executablePath ?? CommandLine.arguments[0]
        let label = "io.github.mllsm66.wipekit.job." + dir.lastPathComponent.filter { $0.isLetter || $0.isNumber || $0 == "-" }
        appendLog(logPath, "Starter: \(exe) (euid \(geteuid()))")

        // 1. Weg: launchd (empfohlen)
        let workerArgs = [exe, "--wipe-worker"] + arguments + ["--launchd-label", label]
        let plist: [String: Any] = [
            "Label": label,
            "ProgramArguments": workerArgs,
            "RunAtLoad": true,
            "KeepAlive": false,
            "ProcessType": "Interactive",
            "AbandonProcessGroup": true,
            "StandardOutPath": logPath,
            "StandardErrorPath": logPath,
        ]
        let plistPath = "/private/var/tmp/\(label).plist"
        if let data = try? PropertyListSerialization.data(fromPropertyList: plist, format: .xml, options: 0) {
            unlink(plistPath)
            let fd = open(plistPath, O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW, 0o644)
            if fd >= 0 {
                _ = data.withUnsafeBytes { write(fd, $0.baseAddress, $0.count) }
                fchmod(fd, 0o644)
                close(fd)
                _ = Shell.run("/bin/launchctl", ["bootout", "system/\(label)"])   // falls ein alter Eintrag existiert
                let r = Shell.runWithOutput("/bin/launchctl", ["bootstrap", "system", plistPath])
                unlink(plistPath)
                if r.status == 0 {
                    appendLog(logPath, "Starter: an launchd übergeben (\(label))")
                    print("Lösch-Prozess über launchd gestartet")
                    return 0
                }
                appendLog(logPath, "Starter: launchctl bootstrap fehlgeschlagen (\(r.status)): \(r.output)")
            } else {
                appendLog(logPath, "Starter: plist konnte nicht angelegt werden: \(String(cString: strerror(errno)))")
            }
        }

        // 2. Weg: direkt in eigener Sitzung starten
        var attr: posix_spawnattr_t? = nil
        posix_spawnattr_init(&attr)
        posix_spawnattr_setflags(&attr, Int16(0x0400))   // POSIX_SPAWN_SETSID: eigene Sitzung
        var actions: posix_spawn_file_actions_t? = nil
        posix_spawn_file_actions_init(&actions)
        posix_spawn_file_actions_addopen(&actions, 0, "/dev/null", O_RDONLY, 0)
        posix_spawn_file_actions_addopen(&actions, 1, logPath, O_WRONLY | O_CREAT | O_APPEND, 0o644)
        posix_spawn_file_actions_adddup2(&actions, 1, 2)

        let args = [exe, "--wipe-worker"] + arguments
        var cargs: [UnsafeMutablePointer<CChar>?] = args.map { strdup($0) }
        cargs.append(nil)
        var pid: pid_t = 0
        let result = posix_spawn(&pid, exe, &actions, &attr, cargs, environ)
        for p in cargs { free(p) }
        posix_spawn_file_actions_destroy(&actions)
        posix_spawnattr_destroy(&attr)

        if result != 0 {
            let msg = "Der Lösch-Prozess konnte nicht gestartet werden: \(String(cString: strerror(result)))"
            appendLog(logPath, "Starter: " + msg)
            FileHandle.standardError.write((msg + "\n").data(using: .utf8)!)
            let engine = WipeEngine(disk: opts["disk"] ?? "", mode: WipeMode(rawValue: opts["mode"] ?? ""),
                                    dir: dir, expectedSize: 0)
            engine.failEarly(msg)
            return 1
        }
        appendLog(logPath, "Starter: direkt gestartet (PID \(pid))")
        print("Lösch-Prozess gestartet (PID \(pid))")
        return 0
    }

    static func appendLog(_ path: String, _ line: String) {
        let fd = open(path, O_WRONLY | O_CREAT | O_APPEND | O_NOFOLLOW, 0o644)
        guard fd >= 0 else { return }
        let text = "[\(ISO8601DateFormatter().string(from: Date()))] \(line)\n"
        _ = text.withCString { write(fd, $0, strlen($0)) }
        fchmod(fd, 0o644)
        close(fd)
    }

    /// Worker: der eigentliche Löschvorgang.
    static func run(arguments: [String]) -> Int32 {
        let opts = parse(arguments)
        guard let dirPath = opts["job-dir"], !dirPath.isEmpty else { return 2 }
        let engine = WipeEngine(
            disk: opts["disk"] ?? "",
            mode: WipeMode(rawValue: opts["mode"] ?? ""),
            dir: URL(fileURLWithPath: dirPath, isDirectory: true),
            expectedSize: UInt64(opts["expected-size"] ?? "") ?? 0,
            allowDiskImage: opts["allow-disk-image"] != nil
        )
        let code = engine.run()
        // launchd-Eintrag wieder entfernen (beendet diesen Prozess – alles ist dann bereits erledigt)
        if let label = opts["launchd-label"] {
            fflush(stdout)
            _ = Shell.run("/bin/launchctl", ["bootout", "system/\(label)"])
        }
        return code
    }
}

final class WipeEngine {
    private enum StepResult {
        case ok
        case cancelled
        case failed(String)
    }

    // _IOR('d', 24, uint32_t) / _IOR('d', 25, uint64_t) aus <sys/disk.h>
    private static let DKIOCGETBLOCKSIZE: UInt = 0x40046418
    private static let DKIOCGETBLOCKCOUNT: UInt = 0x40086419
    // _IOW('d', 31, dk_unmap_t)
    private static let DKIOCUNMAP: UInt = 0x8010641F
    private static let chunkSize = 4 * 1024 * 1024   // 4 MiB pro Schreibvorgang

    /// Spiegelt dk_extent_t / dk_unmap_t aus <sys/disk.h>
    private struct DKExtent {
        var offset: UInt64
        var length: UInt64
    }
    private struct DKUnmap {
        var extents: UnsafeMutableRawPointer?
        var extentsCount: UInt32
        var options: UInt32
    }

    /// Startwert für reproduzierbare Zufallsdaten (nur für die Prüfung, bleibt im Speicher)
    private let seed = UInt64.random(in: UInt64.min ... UInt64.max)

    private let disk: String
    private let mode: WipeMode?
    private let dir: URL
    private let expectedSize: UInt64
    private let cancelPath: String
    private var status: HelperStatus
    private var lastStatusWrite = Date.distantPast
    private var samples: [(Date, UInt64)] = []
    private var assertionID: IOPMAssertionID = 0
    private var hasAssertion = false
    private let allowDiskImage: Bool
    private var diskValidated = false
    private var sampleHashes: [UInt64: Data] = [:]

    init(disk: String, mode: WipeMode?, dir: URL, expectedSize: UInt64, allowDiskImage: Bool = false) {
        self.allowDiskImage = allowDiskImage
        self.disk = disk
        self.mode = mode
        self.dir = dir
        self.expectedSize = expectedSize
        self.cancelPath = dir.appendingPathComponent("cancel").path
        let now = Date()
        self.status = HelperStatus(
            state: .starting, pid: getpid(), phase: "Vorbereitung …",
            step: 0, stepCount: mode?.stepCount ?? 0, diskSize: 0,
            bytesDoneTotal: 0, bytesTotal: 0, speed: 0,
            startedAt: now, updatedAt: now, finishedAt: nil,
            message: nil, verified: nil, trimResult: nil, sleepPrevented: false
        )
    }

    /// Fehler, bevor der Worker überhaupt laufen konnte.
    func failEarly(_ message: String) {
        _ = finish(.failed, message)
    }

    func run() -> Int32 {
        signal(SIGHUP, SIG_IGN)
        print("WipeKit-Worker gestartet: \(disk), Modus \(mode?.rawValue ?? "?"), PID \(getpid())")
        writeStatus()

        guard geteuid() == 0 else { return finish(.failed, "Keine Administratorrechte.") }
        guard let mode = self.mode else { return finish(.failed, "Unbekannter Modus.") }
        guard DiskService.isValidWholeDiskID(disk) else { return finish(.failed, "Ungültige Laufwerkskennung »\(disk)«.") }
        guard mode.availableOnMac else {
            return finish(.failed, "Die Firmware-Löschung ist unter macOS nicht möglich – macOS gibt diese Befehle nicht frei.")
        }

        // --- Sicherheitsprüfungen -------------------------------------------------------------
        let protected = DiskService.protectedDisks()
        let info = Shell.diskutilPlist(["info", "-plist", disk]) ?? [:]
        // Nur für die automatischen Tests: ein Disk-Image (niemals ein echtes Laufwerk)
        let isTestImage = allowDiskImage && (info["BusProtocol"] as? String) == "Disk Image"
        if !isTestImage {
            let physical = (Shell.diskutilPlist(["list", "-plist", "physical"])?["WholeDisks"] as? [String]) ?? []
            guard physical.contains(disk) else {
                return finish(.failed, "\(disk) ist nicht (mehr) angeschlossen.")
            }
        }
        if !mode.readOnly {
            guard !protected.isEmpty else {
                return finish(.failed, "Das Startlaufwerk konnte nicht ermittelt werden – Abbruch aus Sicherheitsgründen.")
            }
            guard !protected.contains(disk) else {
                return finish(.failed, "\(disk) enthält das laufende macOS und wird niemals gelöscht.")
            }
            if !isTestImage {
                let externals = (Shell.diskutilPlist(["list", "-plist", "external", "physical"])?["WholeDisks"] as? [String]) ?? []
                guard externals.contains(disk) else {
                    return finish(.failed, "\(disk) ist kein angeschlossenes externes Laufwerk (mehr).")
                }
                if (info["Internal"] as? Bool) == true {
                    return finish(.failed, "\(disk) ist ein internes Laufwerk – Abbruch.")
                }
            }
        }
        diskValidated = true

        // --- Ruhezustand verhindern (Display darf ausgehen) -----------------------------------
        let reason = "WipeKit: Vorgang auf \(disk)" as CFString
        let r = IOPMAssertionCreateWithName("PreventUserIdleSystemSleep" as CFString,
                                            IOPMAssertionLevel(kIOPMAssertionLevelOn),
                                            reason, &assertionID)
        hasAssertion = (r == kIOReturnSuccess)
        status.sleepPrevented = hasAssertion

        status.phase = "Gesundheitsdaten werden gelesen …"
        writeStatus()
        saveSmart("smart-before.json")

        if mode.readOnly { return runCheck() }

        // --- Aushängen (diskutil unmountDisk) --------------------------------------------------
        status.phase = "Laufwerk wird ausgehängt …"
        writeStatus()
        Shell.run("/usr/sbin/diskutil", ["unmountDisk", "force", "/dev/\(disk)"])

        // --- Raw-Device öffnen (/dev/rdiskN wie bei dd) -------------------------------------------
        let fd = open("/dev/r\(disk)", O_RDWR)
        guard fd >= 0 else { return finish(.failed, openError(errno)) }
        defer { close(fd) }

        guard let size = diskSize(fd) else { return finish(.failed, sizeError) }
        if let m = sizeMismatch(size) { return finish(.failed, m) }

        // --- Puffer ----------------------------------------------------------------------------
        let chunk = WipeEngine.chunkSize
        guard let writeBuf = alignedAlloc(chunk),
              let readBuf = alignedAlloc(chunk),
              let expectBuf = alignedAlloc(chunk) else {
            return finish(.failed, "Nicht genügend Arbeitsspeicher.")
        }
        defer { free(writeBuf); free(readBuf); free(expectBuf) }

        // --- Los geht's ----------------------------------------------------------------------
        let samples = mode.verify == .samples ? WipeEngine.pickSamples(size: size, count: 2048) : []
        status.diskSize = size
        status.stepCount = mode.stepCount
        status.bytesTotal = size * UInt64(mode.workUnits) + UInt64(samples.count) * WipeEngine.mib
        status.state = .running
        status.startedAt = Date()
        var base: UInt64 = 0

        for (idx, pattern) in mode.passes.enumerated() {
            status.step = idx + 1
            status.phase = mode.passes.count > 1 ? "Durchgang \(idx + 1): \(pattern.label)" : pattern.label
            writeStatus()
            let last = idx == mode.passes.count - 1
            switch writePass(fd: fd, buf: writeBuf, size: size, pattern: pattern, base: base, samples: last ? samples : []) {
            case .ok: break
            case .cancelled: return finish(.cancelled, "Vom Benutzer abgebrochen. Das Laufwerk ist nur teilweise überschrieben.")
            case .failed(let m): return finish(.failed, m)
            }
            base += size
        }

        if mode.verify == .full {
            status.step = mode.passes.count + 1
            status.phase = "Prüfen (gesamte Platte lesen)"
            writeStatus()
            let expected = mode.passes.last ?? .zeros
            switch verifyPass(fd: fd, buf: readBuf, expect: expectBuf, pattern: expected, size: size, base: base) {
            case .ok: status.verified = true
            case .cancelled: return finish(.cancelled, "Prüfung abgebrochen. Überschrieben wurde vollständig, aber nicht verifiziert.")
            case .failed(let m):
                status.verified = false
                return finish(.failed, m)
            }
        } else if mode.verify == .samples {
            status.step = mode.passes.count + 1
            status.phase = "Stichproben prüfen"
            writeStatus()
            var bad: [UInt64] = []
            for (i, s) in samples.enumerated() {
                if cancelRequested() { return finish(.cancelled, "Stichproben-Prüfung abgebrochen. Überschrieben wurde vollständig.") }
                if !readFully(fd: fd, buf: readBuf, count: Int(WipeEngine.mib), offset: s)
                    || sampleHashes[s] != WipeEngine.sha(readBuf, Int(WipeEngine.mib)) {
                    bad.append(s)
                }
                reportProgress(base + UInt64(i + 1) * WipeEngine.mib)
            }
            if bad.isEmpty {
                status.verified = true
                status.sampleResult = "bestanden – \(samples.count) Stellen (je 1 MiB, inkl. Anfang und Ende) gelesen und verglichen"
            } else {
                status.verified = false
                let where_ = bad.prefix(3).map { Fmt.bytes($0) }.joined(separator: ", ")
                status.sampleResult = "\(bad.count) von \(samples.count) geprüften Stellen auffällig (z. B. bei \(where_))."
                return finish(.failed, "Stichproben-Prüfung FEHLGESCHLAGEN: \(status.sampleResult ?? "") Das Laufwerk (oder der Adapter) arbeitet nicht zuverlässig.")
            }
        }

        if mode.trims {
            status.step = mode.stepCount
            status.phase = "TRIM – Blöcke freigeben"
            writeStatus()
            status.trimResult = trimAll(fd: fd, size: size)
            if cancelRequested() {
                return finish(.cancelled, "TRIM abgebrochen. Überschrieben und geprüft wurde vollständig.")
            }
        }

        return finish(.done, mode.verify == .full
            ? "Alle Sektoren wurden überschrieben und die Prüfung war erfolgreich."
            : "Alle Sektoren wurden mit Zufallsdaten überschrieben; die Stichproben-Prüfung war erfolgreich.")
    }

    // MARK: Öffnen und Größe

    private let sizeError = "Die Größe des Laufwerks konnte nicht ermittelt werden."

    private func openError(_ e: Int32) -> String {
        var msg = "/dev/r\(disk) konnte nicht geöffnet werden: \(String(cString: strerror(e)))."
        if e == EPERM || e == EACCES {
            msg += " Tipp: WipeKit unter Systemeinstellungen › Datenschutz & Sicherheit › Festplattenvollzugriff erlauben."
        } else if e == EBUSY {
            msg += " Das Laufwerk wird noch benutzt – bitte in anderen Programmen auswerfen."
        }
        return msg
    }

    private func diskSize(_ fd: Int32) -> UInt64? {
        var blockSize: UInt32 = 0
        var blockCount: UInt64 = 0
        let okBS = (withUnsafeMutablePointer(to: &blockSize) { ioctl(fd, WipeEngine.DKIOCGETBLOCKSIZE, $0) }) == 0
        let okBC = (withUnsafeMutablePointer(to: &blockCount) { ioctl(fd, WipeEngine.DKIOCGETBLOCKCOUNT, $0) }) == 0
        var size: UInt64 = (okBS && okBC) ? UInt64(blockSize) * blockCount : 0
        if size == 0 { size = expectedSize }
        return size > 0 ? size : nil
    }

    private func sizeMismatch(_ size: UInt64) -> String? {
        guard expectedSize > 0 else { return nil }
        let diff = size > expectedSize ? size - expectedSize : expectedSize - size
        if diff > 1024 * 1024 {
            return "Die Größe von \(disk) hat sich geändert (\(Fmt.bytes(size)) statt \(Fmt.bytes(expectedSize))). Wurde das Laufwerk getauscht? Abbruch aus Sicherheitsgründen."
        }
        return nil
    }

    // MARK: Gesundheitsdaten

    private func saveSmart(_ name: String) {
        guard diskValidated, !allowDiskImage || SmartService.smartctlPath != nil else { return }
        let (_, raw) = SmartService.read(disk)
        let path = dir.appendingPathComponent(name).path
        let fd = open(path, O_WRONLY | O_CREAT | O_TRUNC | O_NOFOLLOW, 0o644)
        guard fd >= 0 else { return }
        _ = raw.withUnsafeBytes { write(fd, $0.baseAddress, $0.count) }
        fchmod(fd, 0o644)
        close(fd)
    }

    // MARK: Nur prüfen / Oberflächentest

    private func runCheck() -> Int32 {
        let fd = open("/dev/r\(disk)", O_RDONLY)
        guard fd >= 0 else { return finish(.failed, openError(errno)) }
        defer { close(fd) }
        guard let size = diskSize(fd) else { return finish(.failed, sizeError) }
        if let m = sizeMismatch(size) { return finish(.failed, m) }
        let chunk = WipeEngine.chunkSize
        guard let buf = alignedAlloc(chunk), let zero = alignedAlloc(chunk) else {
            return finish(.failed, "Nicht genügend Arbeitsspeicher.")
        }
        defer { free(buf); free(zero) }
        memset(zero, 0, chunk)
        status.diskSize = size
        status.stepCount = 1
        status.step = 1
        status.bytesTotal = size
        status.state = .running
        status.startedAt = Date()
        status.phase = "Alle Sektoren lesen"
        status.curve = []
        status.nonZeroBytes = 0
        status.badBlocks = 0
        let bucket = max(UInt64(chunk), size / 200)
        var bucketStart: UInt64 = 0
        var bucketTime = Date()
        var offset: UInt64 = 0
        while offset < size {
            if cancelRequested() { return finish(.cancelled, "Vom Benutzer abgebrochen.") }
            let n = Int(min(UInt64(chunk), size - offset))
            if readFully(fd: fd, buf: buf, count: n, offset: offset) {
                if memcmp(buf, zero, n) != 0 { status.nonZeroBytes! += countNonZero(buf, n, zero) }
            } else if let fail = readSlowly(fd: fd, buf: buf, count: n, offset: offset, zero: zero) {
                return finish(.failed, fail)
            }
            offset += UInt64(n)
            if offset - bucketStart >= bucket || offset >= size {
                let dt = max(0.000001, Date().timeIntervalSince(bucketTime))
                status.curve!.append((Double(offset - bucketStart) / dt / 1e6 * 10).rounded() / 10)
                bucketStart = offset
                bucketTime = Date()
            }
            reportProgress(offset)
        }
        let nz = status.nonZeroBytes ?? 0
        let bad = status.badBlocks ?? 0
        var msg = bad > 0 ? "\(bad) unlesbare Bereiche (je 64 KiB) gefunden – das Laufwerk ist beschädigt. " : "Alle Sektoren sind lesbar. "
        if nz == 0 {
            msg += "Das Laufwerk ist leer (nur Nullen)."
        } else {
            let pct = String(format: "%.1f", Double(nz) / Double(size) * 100).replacingOccurrences(of: ".", with: ",")
            msg += "\(Fmt.bytes(nz)) (\(pct) %) enthalten Daten (nicht nur Nullen)."
        }
        status.verified = nz == 0 && bad == 0
        return finish(.done, msg)
    }

    private func countNonZero(_ buf: UnsafeMutableRawPointer, _ n: Int, _ zero: UnsafeMutableRawPointer) -> UInt64 {
        var total: UInt64 = 0
        var o = 0
        let mib = Int(WipeEngine.mib)
        while o < n {
            let m = min(mib, n - o)
            if memcmp(buf + o, zero, m) != 0 { total += UInt64(m) }
            o += m
        }
        return total
    }

    /// Bei einem Lesefehler: den Block in 64-KiB-Stücken lesen und defekte Stücke zählen.
    private func readSlowly(fd: Int32, buf: UnsafeMutableRawPointer, count n: Int, offset: UInt64,
                            zero: UnsafeMutableRawPointer) -> String? {
        let sub = 64 * 1024
        var o = 0
        while o < n {
            let m = min(sub, n - o)
            if readFully(fd: fd, buf: buf + o, count: m, offset: offset + UInt64(o)) {
                if memcmp(buf + o, zero, m) != 0 { status.nonZeroBytes! += UInt64(m) }
            } else {
                status.badBlocks! += 1
                if status.badBlocks! >= 4096 {
                    return "Mehr als 4096 unlesbare Bereiche – Test abgebrochen. Das Laufwerk ist stark beschädigt."
                }
            }
            o += m
        }
        return nil
    }

    private func readFully(fd: Int32, buf: UnsafeMutableRawPointer, count n: Int, offset: UInt64) -> Bool {
        var done = 0
        while done < n {
            let r = pread(fd, buf + done, n - done, off_t(offset) + off_t(done))
            if r < 0 {
                if errno == EINTR { continue }
                return false
            }
            if r == 0 { return false }
            done += r
        }
        return true
    }

    // MARK: Stichproben

    static let mib: UInt64 = 1024 * 1024

    /// 1-MiB-Stichproben: Anfang und Ende (je 16 MiB) plus zufällig verteilte Stellen
    static func pickSamples(size: UInt64, count: Int) -> [UInt64] {
        guard size >= mib else { return [] }
        let last = (size - mib) / mib
        var picks = Set<UInt64>()
        for i in 0..<min(16, last + 1) { picks.insert(i) }
        for i in (last >= 15 ? last - 15 : 0)...last { picks.insert(i) }
        let want = min(Int(last + 1), picks.count + count)
        while picks.count < want { picks.insert(UInt64.random(in: 0...last)) }
        return picks.sorted().map { $0 * mib }
    }

    static func sha(_ p: UnsafeMutableRawPointer, _ n: Int) -> Data {
        Data(SHA256.hash(data: UnsafeRawBufferPointer(start: p, count: n)))
    }

    // MARK: Durchgänge

    private func writePass(fd: Int32, buf: UnsafeMutableRawPointer, size: UInt64,
                           pattern: WipePattern, base: UInt64, samples: [UInt64] = []) -> StepResult {
        let chunk = WipeEngine.chunkSize
        if pattern == .zeros { memset(buf, 0, chunk) }
        let sampleSet = Set(samples)
        var offset: UInt64 = 0
        while offset < size {
            if cancelRequested() { return .cancelled }
            let n = Int(min(UInt64(chunk), size - offset))
            switch pattern {
            case .random: arc4random_buf(buf, n)
            case .seeded: fillSeeded(buf, count: n, offset: offset)
            case .zeros:  break
            }
            if !sampleSet.isEmpty {
                var s = offset
                while s < offset + UInt64(n) {
                    if sampleSet.contains(s) && s + WipeEngine.mib <= offset + UInt64(n) {
                        sampleHashes[s] = WipeEngine.sha(buf + Int(s - offset), Int(WipeEngine.mib))
                    }
                    s += WipeEngine.mib
                }
            }
            var done = 0
            while done < n {
                let r = pwrite(fd, buf + done, n - done, off_t(offset) + off_t(done))
                if r < 0 {
                    let e = errno
                    if e == EINTR { continue }
                    return .failed("Schreibfehler bei \(Fmt.bytes(offset + UInt64(done))) von \(Fmt.bytes(size)): \(String(cString: strerror(e))). Laufwerk getrennt oder defekt?")
                }
                if r == 0 { return .failed("Das Laufwerk nimmt bei \(Fmt.bytes(offset)) keine Daten mehr an.") }
                done += r
            }
            offset += UInt64(n)
            reportProgress(base + offset)
        }
        _ = fcntl(fd, F_FULLFSYNC)   // Schreib-Cache der Platte leeren
        return .ok
    }

    private func verifyPass(fd: Int32, buf: UnsafeMutableRawPointer, expect: UnsafeMutableRawPointer,
                            pattern: WipePattern, size: UInt64, base: UInt64) -> StepResult {
        let chunk = WipeEngine.chunkSize
        if pattern != .seeded { memset(expect, 0, chunk) }
        var offset: UInt64 = 0
        while offset < size {
            if cancelRequested() { return .cancelled }
            let n = Int(min(UInt64(chunk), size - offset))
            var done = 0
            while done < n {
                let r = pread(fd, buf + done, n - done, off_t(offset) + off_t(done))
                if r < 0 {
                    let e = errno
                    if e == EINTR { continue }
                    return .failed("Lesefehler bei der Prüfung (\(Fmt.bytes(offset + UInt64(done)))): \(String(cString: strerror(e))).")
                }
                if r == 0 { return .failed("Die Platte lieferte bei \(Fmt.bytes(offset)) keine Daten mehr.") }
                done += r
            }
            if pattern == .seeded { fillSeeded(expect, count: n, offset: offset) }
            if memcmp(buf, expect, n) != 0 {
                return .failed("Prüfung FEHLGESCHLAGEN: Im Bereich ab \(Fmt.bytes(offset)) stehen noch Daten, die nicht überschrieben wurden. Das Laufwerk (oder der Adapter) arbeitet nicht zuverlässig.")
            }
            offset += UInt64(n)
            reportProgress(base + offset)
        }
        return .ok
    }

    // MARK: TRIM

    /// Gibt alle Blöcke per DKIOCUNMAP frei (SCSI UNMAP / ATA TRIM / NVMe Deallocate).
    private func trimAll(fd: Int32, size: UInt64) -> String {
        let step: UInt64 = 1 << 30   // 1 GiB pro Aufruf
        var offset: UInt64 = 0
        var extent = DKExtent(offset: 0, length: 0)
        var lastWrite = Date()
        while offset < size {
            if cancelRequested() { return "abgebrochen" }
            extent.offset = offset
            extent.length = min(step, size - offset)
            let r: Int32 = withUnsafeMutablePointer(to: &extent) { ep -> Int32 in
                var req = DKUnmap(extents: UnsafeMutableRawPointer(ep), extentsCount: 1, options: 0)
                return withUnsafeMutablePointer(to: &req) { ioctl(fd, WipeEngine.DKIOCUNMAP, $0) }
            }
            if r != 0 {
                let reason = String(cString: strerror(errno))
                if offset == 0 {
                    return "vom Laufwerk bzw. USB-Adapter nicht unterstützt (\(reason)) – übersprungen"
                }
                return "teilweise durchgeführt (\(Fmt.bytes(offset)) freigegeben, dann: \(reason))"
            }
            offset += extent.length
            if Date().timeIntervalSince(lastWrite) >= 1 {
                lastWrite = Date()
                status.phase = "TRIM – Blöcke freigeben (\(Int(Double(offset) / Double(size) * 100)) %)"
                writeStatus()
            }
        }
        return "durchgeführt – alle Blöcke freigegeben"
    }

    // MARK: Hilfen

    /// Reproduzierbare Zufallsdaten (SplitMix64), abhängig von Startwert und Position.
    private func fillSeeded(_ buf: UnsafeMutableRawPointer, count: Int, offset: UInt64) {
        let words = count / 8
        let p = buf.bindMemory(to: UInt64.self, capacity: words)
        var state = seed ^ (offset &* 0x9E3779B97F4A7C15)
        for i in 0..<words {
            state &+= 0x9E3779B97F4A7C15
            var z = state
            z = (z ^ (z >> 30)) &* 0xBF58476D1CE4E5B9
            z = (z ^ (z >> 27)) &* 0x94D049BB133111EB
            p[i] = z ^ (z >> 31)
        }
    }

    private func alignedAlloc(_ n: Int) -> UnsafeMutableRawPointer? {
        var p: UnsafeMutableRawPointer? = nil
        return posix_memalign(&p, 4096, n) == 0 ? p : nil
    }

    private func cancelRequested() -> Bool {
        access(cancelPath, F_OK) == 0
    }

    private func reportProgress(_ total: UInt64) {
        status.bytesDoneTotal = total
        let now = Date()
        guard now.timeIntervalSince(lastStatusWrite) >= 1 else { return }
        samples.append((now, total))
        samples.removeAll { now.timeIntervalSince($0.0) > 60 }   // gleitendes 60-Sekunden-Fenster
        if let first = samples.first, now.timeIntervalSince(first.0) >= 3, total >= first.1 {
            status.speed = Double(total - first.1) / now.timeIntervalSince(first.0)
        } else {
            let elapsed = now.timeIntervalSince(status.startedAt)
            if elapsed > 0.5 { status.speed = Double(total) / elapsed }
        }
        writeStatus()
    }

    /// Schreibt status.json atomar (temporäre Datei im selben Ordner + rename).
    private func writeStatus() {
        status.updatedAt = Date()
        lastStatusWrite = status.updatedAt
        guard let data = try? JSONEncoder().encode(status) else { return }
        let finalPath = dir.appendingPathComponent("status.json").path
        let tmpPath = dir.appendingPathComponent(".status.json.tmp").path
        let fd = open(tmpPath, O_WRONLY | O_CREAT | O_TRUNC | O_NOFOLLOW, 0o644)
        guard fd >= 0 else {
            print("status.json konnte nicht geschrieben werden: \(String(cString: strerror(errno)))")
            return
        }
        let ok = data.withUnsafeBytes { buf -> Bool in
            guard let base = buf.baseAddress else { return false }
            return write(fd, base, buf.count) == buf.count
        }
        fchmod(fd, 0o644)
        close(fd)
        if ok {
            rename(tmpPath, finalPath)
        }
    }

    private func finish(_ state: JobState, _ message: String) -> Int32 {
        print("Ende: \(state.rawValue) – \(message)")
        if diskValidated {
            status.phase = "Gesundheitsdaten werden gelesen …"
            saveSmart("smart-after.json")
        }
        if hasAssertion {
            IOPMAssertionRelease(assertionID)   // ab jetzt darf der Mac wieder schlafen
            hasAssertion = false
        }
        status.state = state
        status.message = message
        status.finishedAt = Date()
        status.sleepPrevented = false
        if state == .done { status.bytesDoneTotal = status.bytesTotal }
        writeStatus()
        return state == .done ? 0 : 1
    }
}
