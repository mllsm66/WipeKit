# Hinweise zu Software von Dritten

WipeKit selbst steht unter der MIT-Lizenz (siehe `LICENSE`). Für die folgenden Bestandteile
anderer Projekte gelten deren eigene Lizenzen.

## smartmontools (smartctl) – GPL-2.0-or-later

- Projekt: https://www.smartmontools.org · https://github.com/smartmontools/smartmontools
- Copyright © 2002–2025 Bruce Allen, Christian Franke und weitere Mitwirkende
- Verwendung: WipeKit ruft das eigenständige Programm `smartctl` auf, um die Gesundheitsdaten
  (S.M.A.R.T.) der Laufwerke zu lesen und Selbsttests zu starten. WipeKit wird **nicht** mit
  smartmontools verlinkt; beide Programme kommunizieren nur über die Kommandozeile.
- **macOS:** `smartctl` (Version 7.5) wird unverändert aus dem offiziellen Quelltext gebaut und liegt
  in `WipeKit.app/Contents/MacOS/smartctl`.
- **Windows:** `smartctl.exe` (Version 7.5) stammt unverändert aus dem offiziellen Installationspaket
  des smartmontools-Projekts und ist in `WipeKit.exe` eingebettet.
- **Linux:** wird nicht mitgeliefert, sondern als Paket der Distribution installiert (`smartmontools`).
- Lizenztext: `licenses/GPL-2.0.txt` (liegt jedem Download bei).
- **Quelltext:** Der vollständige, zugehörige Quelltext (`smartmontools-7.5.tar.gz`) ist bei jedem
  WipeKit-Release auf GitHub als eigene Datei angehängt:
  https://github.com/mllsm66/WipeKit/releases

## hdparm und nvme-cli (nur Linux)

Werden nicht mitgeliefert. WipeKit ruft die als Paket der Distribution installierten Programme auf
(für die Firmware-Löschung und die Prüfung versteckter Bereiche).
- hdparm – © Mark Lord, BSD-artige Lizenz
- nvme-cli – GPL-2.0-only

## .NET Runtime (nur Windows) – MIT

Die Windows-Version enthält die .NET-Laufzeitumgebung (eigenständige Veröffentlichung).
Copyright © .NET Foundation and Contributors. MIT-Lizenz:
https://github.com/dotnet/runtime/blob/main/LICENSE.TXT ·
Hinweise zu deren Drittkomponenten: https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT

## Inno Setup (nur Windows-Installer)

Der Windows-Installer wird mit Inno Setup erstellt (© Jordan Russell und Martijn Laan). Die
Inno-Setup-Lizenz erlaubt das kostenlose Erstellen und Weitergeben von Installern.

## Helvetica-Zeichenbreiten in den PDF-Berichten

Die PDF-Berichte verwenden die Standardschrift Helvetica des PDF-Formats. Die Schrift selbst wird
**nicht** eingebettet; der Betrachter verwendet seine eigene. WipeKit enthält nur die Tabelle der
Zeichenbreiten, entnommen aus den Adobe Core-14-AFM-Dateien:

> Copyright (c) 1985, 1987, 1989, 1990, 1997 Adobe Systems Incorporated. All Rights Reserved.
>
> This file and the 14 PostScript(R) AFM files it accompanies may be used, copied, and distributed
> for any purpose and without charge, with or without modification, provided that all copyright
> notices are retained; that the AFM files are not distributed without this file; that all
> modifications to this file or any of the AFM files are prominently noted in the modified file(s);
> and that this paragraph is not modified. Adobe Systems has no responsibility or obligation to
> support the use of the AFM files.

Die AFM-Dateien selbst werden nicht weitergegeben, nur daraus abgeleitete Breitenwerte.

## Laufzeitbibliotheken der Linux-Version

GTK 4, libadwaita, PyGObject und Python werden nicht mitgeliefert, sondern von der Distribution
bereitgestellt (LGPL-2.1-or-later bzw. PSF-Lizenz).

## Marken

Herstellernamen (z. B. Western Digital, Samsung, Crucial) werden nur beschreibend verwendet, um
Laufwerke zu bezeichnen. WipeKit steht in keiner Verbindung zu diesen Herstellern oder zu anderen
Diagnoseprogrammen.
