#!/bin/bash
# Baut das .deb-Paket und ein .tar.gz für andere Distributionen.
# Aufruf:  bash linux/build.sh 1.0.0   ->  dist/
set -euo pipefail
cd "$(dirname "$0")/.."
VERSION="${1:-0.0.0}"
APP_ID=io.github.mllsm66.WipeKit
OUT="$(pwd)/dist"
mkdir -p "$OUT"

# Versionsnummer in common.py eintragen
STAGE="$(mktemp -d)"
cp -r linux/wipekit "$STAGE/wipekit"
sed -i "s/^VERSION = \".*\"/VERSION = \"$VERSION\"/" "$STAGE/wipekit/common.py"
rm -rf "$STAGE/wipekit/__pycache__"

# --- .deb ------------------------------------------------------------------
PKG="$STAGE/deb"
install -d "$PKG/DEBIAN" "$PKG/usr/lib/wipekit" "$PKG/usr/bin"
install -m 644 "$STAGE/wipekit/common.py" "$STAGE/wipekit/wipekit_gui.py" "$PKG/usr/lib/wipekit/"
install -m 755 "$STAGE/wipekit/wipekit-helper" "$PKG/usr/lib/wipekit/"
install -d "$PKG/usr/lib/wipekit/icons/hicolor/scalable/actions"
install -m 644 "$STAGE"/wipekit/icons/hicolor/scalable/actions/*.svg "$PKG/usr/lib/wipekit/icons/hicolor/scalable/actions/"
install -m 755 linux/packaging/wipekit "$PKG/usr/bin/wipekit"
install -D -m 644 linux/packaging/$APP_ID.desktop "$PKG/usr/share/applications/$APP_ID.desktop"
install -D -m 644 linux/packaging/$APP_ID.policy "$PKG/usr/share/polkit-1/actions/$APP_ID.policy"
install -D -m 644 assets/icon-256.png "$PKG/usr/share/icons/hicolor/256x256/apps/$APP_ID.png"
install -D -m 644 assets/icon-512.png "$PKG/usr/share/icons/hicolor/512x512/apps/$APP_ID.png"
install -D -m 644 LICENSE "$PKG/usr/share/doc/wipekit/copyright"
cat > "$PKG/DEBIAN/control" <<CONTROL
Package: wipekit
Version: $VERSION
Section: utils
Priority: optional
Architecture: all
Depends: python3 (>= 3.9), python3-gi, python3-gi-cairo, gir1.2-gtk-4.0, gir1.2-adw-1 (>= 1.4), adwaita-icon-theme, librsvg2-common, pkexec | policykit-1, util-linux
Maintainer: mllsm66 <mllsm66@users.noreply.github.com>
Homepage: https://github.com/mllsm66/WipeKit
Description: Externe Laufwerke sicher löschen
 WipeKit überschreibt externe Festplatten, SSDs und USB-Sticks vollständig,
 prüft das Ergebnis und erstellt ein Löschprotokoll.
CONTROL
dpkg-deb --root-owner-group --build "$PKG" "$OUT/WipeKit-Linux-$VERSION.deb"

# --- .tar.gz ---------------------------------------------------------------
TAR="$STAGE/WipeKit-$VERSION"
install -d "$TAR/icons" "$TAR/packaging"
cp -r "$STAGE/wipekit" "$TAR/wipekit"
cp linux/packaging/* "$TAR/packaging/"
cp assets/icon-256.png assets/icon-512.png "$TAR/icons/"
install -m 755 linux/install.sh "$TAR/install.sh"
cp LICENSE "$TAR/"
tar -C "$STAGE" -czf "$OUT/WipeKit-Linux-$VERSION.tar.gz" "WipeKit-$VERSION"

rm -rf "$STAGE"
ls -la "$OUT"
