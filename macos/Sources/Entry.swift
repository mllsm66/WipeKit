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
            exit(WipeHelper.launch(arguments: Array(args.dropFirst(2))))
        }
        if args.count > 1 && args[1] == "--wipe-worker" {
            setvbuf(stdout, nil, _IOLBF, 0)   // Log zeilenweise schreiben
            exit(WipeHelper.run(arguments: Array(args.dropFirst(2))))
        }
        WipeKitApp.main()
    }
}
