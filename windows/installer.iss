; Inno-Setup-Skript für den WipeKit-Installer
; Aufruf (aus dem Repository-Ordner):  iscc /DMyAppVersion=1.0.0 windows\installer.iss

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif

[Setup]
AppId={{8F6B2C4E-5A1D-4C3B-9E7F-2D4A6B8C0E1F}
AppName=WipeKit
AppVersion={#MyAppVersion}
AppVerName=WipeKit {#MyAppVersion}
AppPublisher=mllsm66
AppPublisherURL=https://github.com/mllsm66/WipeKit
AppSupportURL=https://github.com/mllsm66/WipeKit/issues
DefaultDirName={autopf}\WipeKit
DefaultGroupName=WipeKit
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=WipeKit-Windows-Setup-{#MyAppVersion}
SetupIconFile=..\assets\WipeKit.ico
UninstallDisplayIcon={app}\WipeKit.exe
LicenseFile=..\LICENSE
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin

[Languages]
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\dist\win\WipeKit.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\WipeKit"; Filename: "{app}\WipeKit.exe"
Name: "{autodesktop}\WipeKit"; Filename: "{app}\WipeKit.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\WipeKit.exe"; Description: "{cm:LaunchProgram,WipeKit}"; Flags: nowait postinstall skipifsilent shellexec
