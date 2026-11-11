#!/bin/bash
# Baut WipeKit.app als Universal-App (Intel + Apple Silicon).
# Voraussetzung: Xcode Command Line Tools  ->  xcode-select --install
set -euo pipefail
cd "$(dirname "$0")"

APP_NAME="WipeKit"
BUILD="build"
APP="$BUILD/$APP_NAME.app"
MIN_MACOS="13.0"

# Swift-Compiler suchen: erst über xcrun, sonst direkt in den Command Line Tools
CLT="/Library/Developer/CommandLineTools"
if SWIFTC="$(xcrun --find swiftc 2>/dev/null)" && [ -x "$SWIFTC" ]; then
  SDK="$(xcrun --sdk macosx --show-sdk-path 2>/dev/null || true)"
elif [ -x "$CLT/usr/bin/swiftc" ]; then
  SWIFTC="$CLT/usr/bin/swiftc"
  SDK=""
else
  echo "❌ Swift-Compiler nicht gefunden."
  echo "   xcode-select zeigt auf: $(xcode-select -p 2>&1)"
  echo "   xcrun meldet:           $(xcrun --find swiftc 2>&1 | head -n 3)"
  echo ""
  echo "   Lösung: Command Line Tools neu installieren:"
  echo "     sudo rm -rf $CLT"
  echo "     xcode-select --install"
  exit 1
fi
if [ -z "$SDK" ] || [ ! -d "$SDK" ]; then
  SDK="$CLT/SDKs/MacOSX.sdk"
fi
# Makro-Plugins (z. B. SwiftUIMacros) suchen und dem Compiler bekannt machen
PLUGIN_FLAGS=()
for dir in \
  "$(dirname "$SWIFTC")/../lib/swift/host/plugins" \
  "$CLT/usr/lib/swift/host/plugins" \
  "$SDK/usr/lib/swift/host/plugins" \
  "$(xcode-select -p 2>/dev/null)/Platforms/MacOSX.platform/Developer/usr/lib/swift/host/plugins" \
  "$(xcode-select -p 2>/dev/null)/usr/lib/swift/host/plugins"; do
  if [ -d "$dir" ]; then PLUGIN_FLAGS+=(-plugin-path "$dir"); fi
done

echo "▶︎ Compiler: $SWIFTC"
echo "▶︎ SDK:      $SDK"
rm -rf "$BUILD"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

SOURCES=(Sources/*.swift)
FLAGS=(-O -parse-as-library -sdk "$SDK" -framework IOKit -framework DiskArbitration ${PLUGIN_FLAGS[@]+"${PLUGIN_FLAGS[@]}"})

built=()
for arch in x86_64 arm64; do
  echo "▶︎ Kompiliere für $arch …"
  if "$SWIFTC" "${FLAGS[@]}" -target "$arch-apple-macos$MIN_MACOS" "${SOURCES[@]}" -o "$BUILD/$APP_NAME-$arch"; then
    built+=("$BUILD/$APP_NAME-$arch")
  else
    echo "⚠️  Build für $arch fehlgeschlagen – wird übersprungen."
  fi
done

if [ ${#built[@]} -eq 0 ]; then
  echo "❌ Build fehlgeschlagen. Bitte die Fehlermeldungen oben an Claude schicken."
  exit 1
fi

lipo -create "${built[@]}" -output "$APP/Contents/MacOS/$APP_NAME"
rm -f "${built[@]}"
cp Info.plist "$APP/Contents/Info.plist"
# Versionsnummer setzen (z. B. VERSION=1.0.0 bash build.sh)
if [ -n "${VERSION:-}" ]; then
  plutil -replace CFBundleShortVersionString -string "$VERSION" "$APP/Contents/Info.plist"
  plutil -replace CFBundleVersion -string "$VERSION" "$APP/Contents/Info.plist"
fi

# App-Icon erzeugen
ICON_SRC="../assets/AppIcon.png"
if [ -f "$ICON_SRC" ]; then
  ICONSET="$BUILD/AppIcon.iconset"
  mkdir -p "$ICONSET"
  for s in 16 32 128 256 512; do
    sips -z $s $s "$ICON_SRC" --out "$ICONSET/icon_${s}x${s}.png" >/dev/null
    d=$((s * 2))
    sips -z $d $d "$ICON_SRC" --out "$ICONSET/icon_${s}x${s}@2x.png" >/dev/null
  done
  iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/AppIcon.icns" || echo "⚠️  Icon konnte nicht erzeugt werden (egal)."
  rm -rf "$ICONSET"
fi

codesign --force --sign - "$APP"

echo ""
echo "✅ Fertig: $(pwd)/$APP"
echo "   Architekturen: $(lipo -archs "$APP/Contents/MacOS/$APP_NAME")"
echo "   Tipp: Die App in den Ordner »Programme« ziehen und von dort starten."
if [ -z "${CI:-}" ]; then open "$BUILD"; fi
