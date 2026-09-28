; Inno Setup 6 script for Magpie Mail
; Compile: ISCC.exe /DMyAppVersion=1.0.0 /DSourceDir=..\publish Magpie.iss  (GitHub Actions does this)

#define MyAppName "Magpie Mail"
#ifndef MyAppVersion
  #define MyAppVersion "1.1.2"
#endif
#define MyAppPublisher "Krishna Bhunia"
#define MyAppURL "https://github.com/krishnabhunia/Magpie-email-client"
#define MyAppExeName "Magpie.exe"
#ifndef SourceDir
  #define SourceDir "..\publish"
#endif

[Setup]
AppId={{6A1D5E3C-8B2F-4C71-9E0A-3F5B7D2C9A48}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
VersionInfoVersion={#MyAppVersion}
DefaultDirName={autopf}\Magpie
DefaultGroupName=Magpie
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=Output
OutputBaseFilename=Magpie-Setup-{#MyAppVersion}
SetupIconFile=..\src\Magpie.App\Assets\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
CloseApplications=yes
RestartApplications=no
ShowLanguageDialog=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "autostart"; Description: "Start Magpie with Windows (in the notification area, so snoozes, reminders and scheduled sends fire on time)"; GroupDescription: "Windows integration:"

[Files]
Source: "{#SourceDir}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\CHANGELOG.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
Root: HKA; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Magpie"; ValueData: """{app}\{#MyAppExeName}"" --tray"; Tasks: autostart; Flags: uninsdeletevalue

[UninstallDelete]
; Left behind by in-app updates (design U1)
Type: files; Name: "{app}\Magpie.previous.exe"
Type: files; Name: "{app}\Magpie.exe.failed"
Type: files; Name: "{app}\.magpie-write-test-*"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent
