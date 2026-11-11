import Foundation

/// Einstiegspunkt.
/// Die gleiche Binärdatei hat zwei Rollen:
///  1. normal gestartet  -> SwiftUI-Oberfläche
///  2. mit --wipe-helper -> Lösch-Prozess, läuft mit Administratorrechten (root)
@main
struct Entry {
    @MainActor static func main() {
        let args = CommandLine.arguments
        if args.count > 1 && args[1] == "--wipe-helper" {
            exit(WipeHelper.run(arguments: Array(args.dropFirst(2))))
        }
        WipeKitApp.main()
    }
}
