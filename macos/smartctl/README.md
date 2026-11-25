# smartctl für macOS – WipeKit-Ergänzung „usbcap“

WipeKit liefert unter macOS ein selbst gebautes `smartctl` aus **smartmontools 7.5**
(GPL-2.0-or-later, https://www.smartmontools.org) mit. Es enthält genau eine Ergänzung:

| Datei | Inhalt |
| --- | --- |
| `os_darwin_usbcap.inc` | Neuer Gerätetyp `usbcap:` (vollständig neu geschrieben, GPL-2.0-or-later) |
| `os_darwin-usbcap.patch` | Bindet die Datei in `os_darwin.cpp` ein (3 kleine Stellen) |

## Warum

macOS gibt Programmen für USB-Laufwerke keinen SCSI-Durchgriff. Dadurch erreichen die Befehle für
Gesundheitsdaten (SAT, JMicron, ASMedia, Realtek …) das Laufwerk nie – egal, ob der USB-Adapter sie
beherrscht. Unter Windows und Linux funktioniert das mit denselben Adaptern problemlos.

## Wie

```
sudo smartctl -x usbcap:/dev/disk4      # oder usbcap:disk4 oder usbcap:0x14c00000 (USB-Location-ID)
```

1. Nur mit Administratorrechten (root) und nur, wenn **kein Volume des Laufwerks eingebunden** ist.
2. smartctl übernimmt das ganze USB-Gerät vom macOS-Treiber („Capture“, wie USB-Durchreichung an
   virtuelle Maschinen). Das Laufwerk verschwindet dabei kurz aus dem Finder.
3. Die Befehle gehen über das USB-Protokoll „Bulk-Only Transport“ direkt an den Adapter.
   UAS-Gehäuse werden dafür auf ihre Bulk-Only-Einstellung umgeschaltet.
4. Der Adapter-Typ wird wie unter Linux über die USB-ID aus der Datenbank von smartmontools
   bestimmt; unbekannte Adapter werden mit SAT versucht. `-d sat`, `-d sntjmicron` usw. gehen auch.
5. Beim Schließen bekommt macOS das Gerät zurück; es meldet sich neu an und erscheint wieder.

Mit `WIPEKIT_USBCAP_KEEP=1` bleibt das Gerät nach dem Schließen übernommen, damit mehrere Aufrufe
(z. B. verschiedene `-d`-Typen) ohne erneutes An- und Abmelden möglich sind. Der letzte Aufruf
muss ohne diese Variable erfolgen.

## Quelltext

Jedes WipeKit-Release enthält den unveränderten Originalquelltext (`smartmontools-7.5.tar.gz`)
und den fertig geänderten, baubaren Quelltext (`smartmontools-7.5-wipekit.tar.gz`).
Gebaut wird mit `macos/build-smartctl.sh`.
