; Inno Setup script for SIRS.
;
; Deliberately a per-user install: PrivilegesRequired=lowest puts SIRS in
; %LOCALAPPDATA%\Programs\SIRS rather than Program Files. That means no UAC prompt to install,
; and - the reason that matters here - the built-in updater can replace the files without asking
; for administrator rights. An updater that needs elevation is one that gets cancelled.
;
; Build with:  ISCC /DAppVersion=1.3.0.42 /DSourceDir=..\publish\app installer\SIRS.iss

#ifndef AppVersion
  #define AppVersion "1.3.0.0"
#endif

#ifndef SourceDir
  #define SourceDir "..\publish\app"
#endif

#ifndef OutputDir
  #define OutputDir "..\publish"
#endif

[Setup]
; Never change this GUID. It is how Windows and every future installer recognise
; an existing SIRS and upgrade it in place rather than leaving two copies behind.
AppId={{8D3F1C6E-5A47-4C2B-9E88-1B7A2F0D6C13}
AppName=SIRS
AppVersion={#AppVersion}
AppVerName=SIRS {#AppVersion}
AppPublisher=SIRS contributors
AppSupportURL=https://github.com/frigstah/SIRS
AppUpdatesURL=https://github.com/frigstah/SIRS/releases
DefaultDirName={autopf}\SIRS
DefaultGroupName=SIRS
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
OutputDir={#OutputDir}
OutputBaseFilename=SIRS-{#AppVersion}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayName=SIRS
; Restart Manager: if SIRS is running, offer to close it rather than failing on a locked file.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\SIRS"; Filename: "{app}\SIRS.exe"
Name: "{group}\Uninstall SIRS"; Filename: "{uninstallexe}"
Name: "{autodesktop}\SIRS"; Filename: "{app}\SIRS.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\SIRS.exe"; Description: "Start SIRS"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; The staging folder the updater leaves behind. Settings, servers and logs in %APPDATA%\SIRS are
; deliberately NOT removed - uninstalling should not throw away someone's station list.
Type: filesandordirs; Name: "{localappdata}\SIRS\update"
