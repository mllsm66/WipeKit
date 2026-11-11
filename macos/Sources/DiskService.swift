import Foundation
import DiskArbitration

// MARK: - Shell

enum Shell {
    @discardableResult
    static func run(_ path: String, _ args: [String]) -> (status: Int32, out: Data) {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: path)
        p.arguments = args
        let pipe = Pipe()
        p.standardOutput = pipe
        p.standardError = FileHandle.nullDevice
        do { try p.run() } catch { return (-1, Data()) }
        let data = pipe.fileHandleForReading.readDataToEndOfFile()
        p.waitUntilExit()
        return (p.terminationStatus, data)
    }

    static func diskutilPlist(_ args: [String]) -> [String: Any]? {
        let r = run("/usr/sbin/diskutil", args)
        guard r.status == 0, !r.out.isEmpty else { return nil }
        let obj = try? PropertyListSerialization.propertyList(from: r.out, options: [], format: nil)
        return obj as? [String: Any]
    }
}

// MARK: - Laufwerke

enum DiskService {

    /// "disk3s2" -> "disk3"
    static func wholeDiskID(_ s: String) -> String {
        guard s.hasPrefix("disk") else { return s }
        var result = "disk"
        for c in s.dropFirst(4) {
            if c.isASCII && c.isNumber { result.append(c) } else { break }
        }
        return result
    }

    static func isValidWholeDiskID(_ s: String) -> Bool {
        guard s.hasPrefix("disk"), s.count > 4 else { return false }
        return s.dropFirst(4).allSatisfy { $0.isASCII && $0.isNumber }
    }

    /// Alle physischen Laufwerke, auf denen das laufende macOS liegt (auch wenn von extern gebootet).
    static func protectedDisks() -> Set<String> {
        var result = Set<String>()
        for mount in ["/", "/System/Volumes/Data"] {
            guard let info = Shell.diskutilPlist(["info", "-plist", mount]) else { continue }
            if let parent = info["ParentWholeDisk"] as? String { result.insert(parent) }
            if let stores = info["APFSPhysicalStores"] as? [[String: Any]] {
                for st in stores {
                    if let id = st["APFSPhysicalStore"] as? String { result.insert(wholeDiskID(id)) }
                }
            }
        }
        if let apfs = Shell.diskutilPlist(["apfs", "list", "-plist"]),
           let containers = apfs["Containers"] as? [[String: Any]] {
            for c in containers {
                guard let ref = c["ContainerReference"] as? String, result.contains(ref) else { continue }
                for st in c["PhysicalStores"] as? [[String: Any]] ?? [] {
                    if let id = st["DeviceIdentifier"] as? String { result.insert(wholeDiskID(id)) }
                }
            }
        }
        return result
    }

    /// Entspricht `diskutil list external physical` – plus Details für die Anzeige.
    static func listExternalDisks() -> [ExternalDisk] {
        guard let list = Shell.diskutilPlist(["list", "-plist", "external", "physical"]) else { return [] }
        let protected = protectedDisks()

        // APFS-Volumes gehören zu synthetischen Disks – über die Container den physischen Platten zuordnen
        var apfsVolumes: [String: [String]] = [:]
        if let apfs = Shell.diskutilPlist(["apfs", "list", "-plist"]),
           let containers = apfs["Containers"] as? [[String: Any]] {
            for c in containers {
                let names = (c["Volumes"] as? [[String: Any]] ?? []).compactMap { $0["Name"] as? String }
                for st in c["PhysicalStores"] as? [[String: Any]] ?? [] {
                    if let id = st["DeviceIdentifier"] as? String {
                        apfsVolumes[wholeDiskID(id), default: []].append(contentsOf: names)
                    }
                }
            }
        }

        var disks: [ExternalDisk] = []
        for entry in list["AllDisksAndPartitions"] as? [[String: Any]] ?? [] {
            guard let id = entry["DeviceIdentifier"] as? String, isValidWholeDiskID(id) else { continue }
            let info = Shell.diskutilPlist(["info", "-plist", id]) ?? [:]
            if (info["Internal"] as? Bool) == true { continue }

            var volumes: [String] = []
            if let n = entry["VolumeName"] as? String, !n.isEmpty { volumes.append(n) }
            for part in entry["Partitions"] as? [[String: Any]] ?? [] {
                if let n = part["VolumeName"] as? String, !n.isEmpty { volumes.append(n) }
            }
            volumes.append(contentsOf: apfsVolumes[id] ?? [])

            var name = (info["MediaName"] as? String)?.trimmingCharacters(in: .whitespaces) ?? ""
            if name.isEmpty { name = (info["IORegistryEntryName"] as? String) ?? "Unbekanntes Laufwerk" }

            let size = (info["TotalSize"] as? NSNumber)?.uint64Value
                ?? (info["Size"] as? NSNumber)?.uint64Value
                ?? (entry["Size"] as? NSNumber)?.uint64Value
                ?? 0

            disks.append(ExternalDisk(
                id: id,
                name: name,
                size: size,
                bus: (info["BusProtocol"] as? String) ?? "?",
                isSSD: info["SolidState"] as? Bool,
                volumes: volumes,
                isProtected: protected.contains(id)
            ))
        }
        return disks.sorted { $0.id.localizedStandardCompare($1.id) == .orderedAscending }
    }
}

// MARK: - Automatische Aktualisierung beim An-/Abstecken

final class DiskWatcher {
    private var session: DASession?
    private let onChange: () -> Void

    init(onChange: @escaping () -> Void) {
        self.onChange = onChange
        guard let s = DASessionCreate(kCFAllocatorDefault) else { return }
        session = s
        let context = Unmanaged.passUnretained(self).toOpaque()
        let callback: DADiskAppearedCallback = { _, ctx in
            guard let ctx = ctx else { return }
            Unmanaged<DiskWatcher>.fromOpaque(ctx).takeUnretainedValue().onChange()
        }
        DARegisterDiskAppearedCallback(s, nil, callback, context)
        DARegisterDiskDisappearedCallback(s, nil, callback, context)
        DASessionScheduleWithRunLoop(s, CFRunLoopGetMain(), CFRunLoopMode.defaultMode.rawValue)
    }
}
