#!/bin/sh
# Installiert WipeKit systemweit (für Distributionen ohne .deb, z. B. Fedora oder Arch).
# Aufruf:  sudo ./install.sh        Entfernen:  sudo ./install.sh --uninstall
set -e
cd "$(dirname "$0")"
APP_ID=io.github.mllsm66.WipeKit

if [ "$(id -u)" -ne 0 ]; then
  echo "Bitte mit sudo ausführen:  sudo ./install.sh"; exit 1
fi

if [ "$1" = "--uninstall" ]; then
  rm -rf /usr/lib/wipekit /usr/bin/wipekit \
         /usr/share/applications/$APP_ID.desktop \
         /usr/share/polkit-1/actions/$APP_ID.policy \
         /usr/share/icons/hicolor/256x256/apps/$APP_ID.png \
         /usr/share/icons/hicolor/512x512/apps/$APP_ID.png
  echo "WipeKit wurde entfernt."; exit 0
fi

install -d /usr/lib/wipekit
install -m 644 wipekit/common.py wipekit/wipekit_gui.py /usr/lib/wipekit/
install -m 755 wipekit/wipekit-helper /usr/lib/wipekit/
install -d /usr/lib/wipekit/icons
install -m 644 wipekit/icons/*.svg /usr/lib/wipekit/icons/
install -m 755 packaging/wipekit /usr/bin/wipekit
install -D -m 644 packaging/$APP_ID.desktop /usr/share/applications/$APP_ID.desktop
install -D -m 644 packaging/$APP_ID.policy /usr/share/polkit-1/actions/$APP_ID.policy
install -D -m 644 icons/icon-256.png /usr/share/icons/hicolor/256x256/apps/$APP_ID.png
install -D -m 644 icons/icon-512.png /usr/share/icons/hicolor/512x512/apps/$APP_ID.png
gtk-update-icon-cache -q /usr/share/icons/hicolor 2>/dev/null || true
echo "WipeKit wurde installiert. Start über das App-Menü oder mit:  wipekit"
