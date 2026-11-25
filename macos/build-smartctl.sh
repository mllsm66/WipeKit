#!/bin/bash
# Baut smartctl aus smartmontools 7.5 (GPL-2.0-or-later) aus dem offiziellen Quelltext –
# als Universal-Programm (Intel + Apple Silicon). Ergebnis: vendor/smartctl
# WipeKit-Ergänzung (smartctl/os_darwin_usbcap.inc + smartctl/os_darwin-usbcap.patch, ebenfalls GPL):
# Gesundheitsdaten von USB-Laufwerken über »usbcap:« (macOS selbst reicht sie nicht durch).
# Der vollständige, geänderte Quelltext (vendor/smartmontools-7.5-wipekit.tar.gz) und der
# Originalquelltext (vendor/smartmontools-7.5.tar.gz) werden jedem Release beigelegt (GPL).
set -euo pipefail
cd "$(dirname "$0")"
VER="7.5"
URL="https://github.com/smartmontools/smartmontools/releases/download/RELEASE_7_5/smartmontools-$VER.tar.gz"
MIN_MACOS="11.0"
mkdir -p vendor
STAMP="$(cat smartctl/os_darwin_usbcap.inc smartctl/os_darwin-usbcap.patch | shasum -a 256 | awk '{print $1}')"
if [ -x vendor/smartctl ] && [ -f vendor/smartmontools-COPYING.txt ] && [ "$(cat vendor/smartctl.stamp 2>/dev/null)" = "$STAMP" ]; then
  echo "▶︎ smartctl ist bereits gebaut."; exit 0
fi

TARBALL="vendor/smartmontools-$VER.tar.gz"
if [ ! -f "$TARBALL" ]; then
  echo "▶︎ Lade smartmontools $VER (Quelltext) …"
  curl -fsSL --retry 3 -o "$TARBALL.part" "$URL"
  curl -fsSL --retry 3 -o "$TARBALL.md5" "$URL.md5" || true
  mv "$TARBALL.part" "$TARBALL"
fi
if [ -s "$TARBALL.md5" ]; then
  WANT=$(awk '{print $1}' "$TARBALL.md5")
  HAVE=$(md5 -q "$TARBALL" 2>/dev/null || md5sum "$TARBALL" | awk '{print $1}')
  if [ "$WANT" != "$HAVE" ]; then echo "❌ Prüfsumme des Quelltexts stimmt nicht."; rm -f "$TARBALL"; exit 1; fi
  echo "▶︎ Prüfsumme in Ordnung (MD5 $HAVE)"
fi
echo "▶︎ SHA-256: $(shasum -a 256 "$TARBALL" | awk '{print $1}')"

OLDPWD_SRC="$(pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
built=()
for arch in arm64 x86_64; do
  echo "▶︎ Baue smartctl für $arch …"
  mkdir -p "$WORK/$arch"
  tar -xzf "$TARBALL" -C "$WORK/$arch"
  (
    cd "$WORK/$arch/smartmontools-$VER"
    cp "$OLDPWD_SRC/smartctl/os_darwin_usbcap.inc" .
    patch -p1 -s < "$OLDPWD_SRC/smartctl/os_darwin-usbcap.patch"
    FLAGS="-O2 -arch $arch -mmacosx-version-min=$MIN_MACOS"
    HOST_ARGS=()
    if [ "$arch" != "$(uname -m)" ]; then HOST_ARGS=(--host="$arch-apple-darwin"); fi
    ./configure --disable-dependency-tracking ${HOST_ARGS[@]+"${HOST_ARGS[@]}"} \
      CC="clang -arch $arch" CXX="clang++ -arch $arch" \
      CFLAGS="$FLAGS" CXXFLAGS="$FLAGS" LDFLAGS="-arch $arch -mmacosx-version-min=$MIN_MACOS" > configure.log 2>&1 \
      || { tail -40 configure.log; exit 1; }
    make -j"$(sysctl -n hw.ncpu 2>/dev/null || echo 4)" smartctl > make.log 2>&1 || { tail -40 make.log; exit 1; }
  ) && built+=("$WORK/$arch/smartmontools-$VER/smartctl") || echo "⚠️  smartctl für $arch konnte nicht gebaut werden."
done
if [ ${#built[@]} -eq 0 ]; then echo "❌ smartctl konnte nicht gebaut werden."; exit 1; fi
lipo -create "${built[@]}" -output vendor/smartctl
cp "$WORK/arm64/smartmontools-$VER/COPYING" vendor/smartmontools-COPYING.txt 2>/dev/null \
  || cp "$WORK/x86_64/smartmontools-$VER/COPYING" vendor/smartmontools-COPYING.txt
# Geänderter Quelltext für die GPL (vollständig, baubar)
SRC="$WORK/src"; mkdir -p "$SRC"
tar -xzf "$TARBALL" -C "$SRC"
cp smartctl/os_darwin_usbcap.inc "$SRC/smartmontools-$VER/"
(cd "$SRC/smartmontools-$VER" && patch -p1 -s < "$OLDPWD_SRC/smartctl/os_darwin-usbcap.patch")
cp smartctl/README.md "$SRC/smartmontools-$VER/WIPEKIT-CHANGES.md"
(cd "$SRC" && mv "smartmontools-$VER" "smartmontools-$VER-wipekit" && tar -czf "$OLDPWD_SRC/vendor/smartmontools-$VER-wipekit.tar.gz" "smartmontools-$VER-wipekit")
echo "$STAMP" > vendor/smartctl.stamp
echo "✅ smartctl: $(lipo -archs vendor/smartctl) – $(vendor/smartctl --version | head -1)"
