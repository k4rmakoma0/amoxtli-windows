#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef PublishDirectory
  #error PublishDirectory is required
#endif
#ifndef ReleaseDirectory
  #error ReleaseDirectory is required
#endif

[Setup]
AppId={{D834F23E-61E5-4E02-8CB0-2DE1593AD3C7}
AppName=Amoxtli
AppVersion={#AppVersion}
AppPublisher=Alan Anaya
AppPublisherURL=https://amoxtli.alananaya.dev/
AppSupportURL=https://github.com/k4rmakoma0/amoxtli-windows/issues
AppUpdatesURL=https://github.com/k4rmakoma0/amoxtli-windows/releases
DefaultDirName={localappdata}\Programs\Amoxtli
DefaultGroupName=Amoxtli
AllowNoIcons=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#ReleaseDirectory}
OutputBaseFilename=Amoxtli-{#AppVersion}-windows-x64-setup
SetupIconFile=..\Assets\Amoxtli.ico
UninstallDisplayIcon={app}\Amoxtli.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ChangesAssociations=yes
CloseApplications=yes
RestartApplications=no
DisableProgramGroupPage=yes
VersionInfoProductName=Amoxtli
VersionInfoProductVersion={#AppVersion}
#ifdef ExternalSigning
SignedUninstaller=yes
SignedUninstallerDir={#SignedUninstallerDirectory}
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[CustomMessages]
english.FileAssociations=Offer Amoxtli in Open with for Markdown files
spanish.FileAssociations=Ofrecer Amoxtli en Abrir con para archivos Markdown

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked
Name: "fileassoc"; Description: "{cm:FileAssociations}"

[Files]
Source: "{#PublishDirectory}\*"; DestDir: "{app}"; Excludes: "*.pdb,*.msix,*.msixbundle,*.appxsym,*.appxrecipe"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Amoxtli"; Filename: "{app}\Amoxtli.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\Amoxtli"; Filename: "{app}\Amoxtli.exe"; WorkingDir: "{app}"; Tasks: desktopicon

; Register an independent GitHub ProgID. Never replace the user's default app.
[Registry]
Root: HKCU; Subkey: "Software\Classes\Amoxtli.GitHub.Markdown"; ValueType: string; ValueData: "Markdown Document"; Flags: uninsdeletekey; Tasks: fileassoc
Root: HKCU; Subkey: "Software\Classes\Amoxtli.GitHub.Markdown\DefaultIcon"; ValueType: string; ValueData: """{app}\Amoxtli.exe"",0"; Tasks: fileassoc
Root: HKCU; Subkey: "Software\Classes\Amoxtli.GitHub.Markdown\shell\open\command"; ValueType: string; ValueData: """{app}\Amoxtli.exe"" ""%1"""; Tasks: fileassoc
#sub RegisterExtension
Root: HKCU; Subkey: "Software\Classes\{#Extension}\OpenWithProgids"; ValueType: string; ValueName: "Amoxtli.GitHub.Markdown"; ValueData: ""; Flags: uninsdeletevalue; Tasks: fileassoc
Root: HKCU; Subkey: "Software\Amoxtli\GitHub\Capabilities\FileAssociations"; ValueType: string; ValueName: "{#Extension}"; ValueData: "Amoxtli.GitHub.Markdown"; Tasks: fileassoc
#endsub
#define Extension ".md"
#emit RegisterExtension
#define Extension ".markdown"
#emit RegisterExtension
#define Extension ".mdown"
#emit RegisterExtension
#define Extension ".mkd"
#emit RegisterExtension
#define Extension ".mkdn"
#emit RegisterExtension
Root: HKCU; Subkey: "Software\Amoxtli\GitHub"; Flags: uninsdeletekey; Tasks: fileassoc
Root: HKCU; Subkey: "Software\Amoxtli\GitHub\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "Amoxtli (GitHub)"; Tasks: fileassoc
Root: HKCU; Subkey: "Software\Amoxtli\GitHub\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "Markdown Viewer"; Tasks: fileassoc
Root: HKCU; Subkey: "Software\Amoxtli\GitHub\Capabilities"; ValueType: string; ValueName: "ApplicationIcon"; ValueData: """{app}\Amoxtli.exe"",0"; Tasks: fileassoc
Root: HKCU; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "Amoxtli (GitHub)"; ValueData: "Software\Amoxtli\GitHub\Capabilities"; Flags: uninsdeletevalue; Tasks: fileassoc

[Run]
Filename: "{app}\Amoxtli.exe"; WorkingDir: "{app}"; Description: "{cm:LaunchProgram,Amoxtli}"; Flags: nowait postinstall skipifsilent
