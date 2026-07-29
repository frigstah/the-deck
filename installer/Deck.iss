; Inno Setup script for Deck.
;
; Deliberately a per-user install: PrivilegesRequired=lowest puts Deck in
; %LOCALAPPDATA%\Programs\Deck rather than Program Files. That means no UAC prompt to install,
; and - the reason that matters here - the built-in updater can replace the files without asking
; for administrator rights. An updater that needs elevation is one that gets cancelled.
;
; Build with:  ISCC /DAppVersion=1.3.0.42 /DSourceDir=..\publish\app installer\Deck.iss

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
; an existing Deck and upgrade it in place rather than leaving two copies behind.
AppId={{8D3F1C6E-5A47-4C2B-9E88-1B7A2F0D6C13}
AppName=The Deck
AppVersion={#AppVersion}
AppVerName=The Deck {#AppVersion}
AppPublisher=The Deck contributors
AppSupportURL=https://github.com/frigstah/the-deck
AppUpdatesURL=https://github.com/frigstah/the-deck/releases
DefaultDirName={autopf}\Deck
DefaultGroupName=Deck
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
OutputDir={#OutputDir}
OutputBaseFilename=Deck-{#AppVersion}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayName=Deck
; Restart Manager: if Deck is running, offer to close it rather than failing on a locked file.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Deck"; Filename: "{app}\Deck.exe"
Name: "{group}\Uninstall Deck"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Deck"; Filename: "{app}\Deck.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Deck.exe"; Description: "Start Deck"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; The staging folder the updater leaves behind. Settings, servers and logs in %APPDATA%\Deck are
; deliberately NOT removed - uninstalling should not throw away someone's station list.
Type: filesandordirs; Name: "{localappdata}\Deck\update"
