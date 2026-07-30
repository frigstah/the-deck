; Inno Setup script for Deck.
;
; Deliberately a per-user install: PrivilegesRequired=lowest puts Deck in
; %LOCALAPPDATA%\Programs\Deck rather than Program Files. That means no UAC prompt to install,
; and - the reason that matters here - the built-in updater can replace the files without asking
; for administrator rights. An updater that needs elevation is one that gets cancelled.
;
; Build with:  ISCC /DAppVersion=1.0.0.42 /DSourceDir=..\publish\app installer\Deck.iss

#ifndef AppVersion
  #define AppVersion "1.0.0.0"
#endif

#ifndef SourceDir
  #define SourceDir "..\publish\app"
#endif

#ifndef OutputDir
  #define OutputDir "..\publish"
#endif

[Setup]
; Never change this GUID. It is how Windows and every future installer recognise an existing Deck and
; upgrade it in place rather than leaving two copies behind.
;
; It was changed exactly once, and the reason is worth keeping. The fork inherited SIRS's AppId
; verbatim, and an AppId is the whole of a product's identity to Windows - so Deck was not a new
; program, it *was* SIRS as far as the installer was concerned. UsePreviousAppDir defaults to yes, so
; setup looked up that id, found SIRS's install, ignored DefaultDirName and offered to install The Deck
; into a folder called SIRS. Accepting that put Deck.exe and SIRS.exe side by side in one directory
; under one uninstaller, with SIRS's own files orphaned inside it and its entry in Add or remove
; programs quietly replaced. Two products cannot share an AppId; that is what it is for.
;
; Changing it means an alpha installed under the old id is not recognised by this installer and has to
; be removed by hand. That was the right trade at this stage and would not be later.
AppId={{04CE5577-3EEC-4029-8E37-920BB4F18475}
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
; The setup program's own icon, and the mark on the wizard's pages. Both come from branding\IconGen,
; and SetupIconFile is the same file the executable carries - one icon, so the thing you download and
; the thing it installs are recognisably each other.
SetupIconFile=..\src\Deck.App\Deck.ico
WizardSmallImageFile=wizard-small.bmp
; Without this, Add or remove programs shows a blank page icon next to Deck. It reads the icon out of
; the installed executable, which is where ApplicationIcon put it.
UninstallDisplayIcon={app}\Deck.exe
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
