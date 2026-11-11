import Foundation
import IOKit.pwr_mgt

/// Läuft als root (gestartet über die Administrator-Passwortabfrage).
/// Schreibt Fortschritt nach <job-dir>/status.json, bricht ab, sobald <job-dir>/cancel existiert.
enum WipeHelper {
    static func run(arguments: [String]) -> Int32 {
        var opts: [String: String] = [:]
        var i = 0
        while i < arguments.count {
            if arguments[i].hasPrefix("--"), i + 1 < arguments.count {
                opts[String(arguments[i].dropFirst(2))] = arguments[i + 1]
                i += 2
            } else {
                i += 1
            }
        }
        guard let dirPath = opts["job-dir"], !dirPath.isEmpty else { return 2 }
        let engine = WipeEngine(
            disk: opts["disk"] ?? "",
            mode: WipeMode(rawValue: opts["mode"] ?? ""),
            dir: URL(fileURLWithPath: dirPath, isDirectory: true),
            expectedSize: UInt64(opts["expected-size"] ?? "") ?? 0
        )
        return engine.run()
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

    init(disk: String, mode: WipeMode?, dir: URL, expectedSize: UInt64) {
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

    func run() -> Int32 {
        signal(SIGHUP, SIG_IGN)
        writeStatus()

        guard geteuid() == 0 else { return finish(.failed, "Keine Administratorrechte.") }
        guard let mode = self.mode else { return finish(.failed, "Unbekannter Löschmodus.") }
        guard DiskService.isValidWholeDiskID(disk) else { return finish(.failed, "Ungültige Laufwerkskennung »\(disk)«.") }

        // --- Sicherheitsprüfungen -------------------------------------------------------------
        let protected = DiskService.protectedDisks()
        guard !protected.isEmpty else {
            return finish(.failed, "Das Startlaufwerk konnte nicht ermittelt werden – Abbruch aus Sicherheitsgründen.")
        }
        guard !protected.contains(disk) else {
            return finish(.failed, "\(disk) enthält das laufende macOS und wird niemals gelöscht.")
        }
        let externals = (Shell.diskutilPlist(["list", "-plist", "external", "physical"])?["WholeDisks"] as? [String]) ?? []
        guard externals.contains(disk) else {
            return finish(.failed, "\(disk) ist kein angeschlossenes externes Laufwerk (mehr).")
        }
        if let info = Shell.diskutilPlist(["info", "-plist", disk]), (info["Internal"] as? Bool) == true {
            return finish(.failed, "\(disk) ist ein internes Laufwerk – Abbruch.")
        }

        // --- Ruhezustand verhindern (Display darf ausgehen) -----------------------------------
        let reason = "WipeKit: Löschvorgang auf \(disk)" as CFString
        let r = IOPMAssertionCreateWithName("PreventUserIdleSystemSleep" as CFString,
                                            IOPMAssertionLevel(kIOPMAssertionLevelOn),
                                            reason, &assertionID)
        hasAssertion = (r == kIOReturnSuccess)
        status.sleepPrevented = hasAssertion

        // --- Aushängen (diskutil unmountDisk) --------------------------------------------------
        status.phase = "Laufwerk wird ausgehängt …"
        writeStatus()
        Shell.run("/usr/sbin/diskutil", ["unmountDisk", "force", "/dev/\(disk)"])

        // --- Raw-Device öffnen (/dev/rdiskN wie bei dd) -------------------------------------------
        let fd = open("/dev/r\(disk)", O_RDWR)
        guard fd >= 0 else {
            let e = errno
            var msg = "/dev/r\(disk) konnte nicht geöffnet werden: \(String(cString: strerror(e)))."
            if e == EPERM || e == EACCES {
                msg += " Tipp: WipeKit unter Systemeinstellungen › Datenschutz & Sicherheit › Festplattenvollzugriff erlauben."
            } else if e == EBUSY {
                msg += " Das Laufwerk wird noch benutzt – bitte in anderen Programmen auswerfen."
            }
            return finish(.failed, msg)
        }
        defer { close(fd) }

        // --- Größe ermitteln -----------------------------------------------------------------
        var blockSize: UInt32 = 0
        var blockCount: UInt64 = 0
        let okBS = (withUnsafeMutablePointer(to: &blockSize) { ioctl(fd, WipeEngine.DKIOCGETBLOCKSIZE, $0) }) == 0
        let okBC = (withUnsafeMutablePointer(to: &blockCount) { ioctl(fd, WipeEngine.DKIOCGETBLOCKCOUNT, $0) }) == 0
        var size: UInt64 = (okBS && okBC) ? UInt64(blockSize) * blockCount : 0
        if size == 0 { size = expectedSize }
        guard size > 0 else { return finish(.failed, "Die Größe des Laufwerks konnte nicht ermittelt werden.") }
        if expectedSize > 0 {
            let diff = size > expectedSize ? size - expectedSize : expectedSize - size
            if diff > 1024 * 1024 {
                return finish(.failed, "Die Größe von \(disk) hat sich geändert (\(Fmt.bytes(size)) statt \(Fmt.bytes(expectedSize))). Wurde das Laufwerk getauscht? Abbruch aus Sicherheitsgründen.")
            }
        }

        // --- Puffer ----------------------------------------------------------------------------
        let chunk = WipeEngine.chunkSize
        guard let writeBuf = alignedAlloc(chunk),
              let readBuf = alignedAlloc(chunk),
              let expectBuf = alignedAlloc(chunk) else {
            return finish(.failed, "Nicht genügend Arbeitsspeicher.")
        }
        defer { free(writeBuf); free(readBuf); free(expectBuf) }

        // --- Los geht's ----------------------------------------------------------------------
        status.diskSize = size
        status.stepCount = mode.stepCount
        status.bytesTotal = size * UInt64(mode.workUnits)
        status.state = .running
        status.startedAt = Date()
        var base: UInt64 = 0

        for (idx, pattern) in mode.passes.enumerated() {
            status.step = idx + 1
            status.phase = mode.passes.count > 1 ? "Durchgang \(idx + 1): \(pattern.label)" : pattern.label
            writeStatus()
            switch writePass(fd: fd, buf: writeBuf, size: size, pattern: pattern, base: base) {
            case .ok: break
            case .cancelled: return finish(.cancelled, "Vom Benutzer abgebrochen. Das Laufwerk ist nur teilweise überschrieben.")
            case .failed(let m): return finish(.failed, m)
            }
            base += size
        }

        if mode.verifies {
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

        return finish(.done, mode.verifies
            ? "Alle Sektoren wurden überschrieben und die Prüfung war erfolgreich."
            : "Alle Sektoren wurden mit Zufallsdaten überschrieben.")
    }

    // MARK: Durchgänge

    private func writePass(fd: Int32, buf: UnsafeMutableRawPointer, size: UInt64,
                           pattern: WipePattern, base: UInt64) -> StepResult {
        let chunk = WipeEngine.chunkSize
        if pattern == .zeros { memset(buf, 0, chunk) }
        var offset: UInt64 = 0
        while offset < size {
            if cancelRequested() { return .cancelled }
            let n = Int(min(UInt64(chunk), size - offset))
            switch pattern {
            case .random: arc4random_buf(buf, n)
            case .seeded: fillSeeded(buf, count: n, offset: offset)
            case .zeros:  break
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

    private func writeStatus() {
        status.updatedAt = Date()
        lastStatusWrite = status.updatedAt
        guard let data = try? JSONEncoder().encode(status) else { return }
        let url = dir.appendingPathComponent("status.json")
        if (try? data.write(to: url, options: .atomic)) != nil {
            chmod(url.path, 0o644)
        }
    }

    private func finish(_ state: JobState, _ message: String) -> Int32 {
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
