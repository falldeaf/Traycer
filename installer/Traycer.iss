#ifndef MyAppVersion
  #define MyAppVersion "0.1.0"
#endif
#ifndef MySourceDir
  #define MySourceDir "..\dist\Traycer"
#endif
#define MyAppName "Traycer HUD"
#define MyExeName "Traycer.exe"
#define MyPublisher "Thomas Mardis"
#define MyPublisherUrl "https://github.com/thomas-mardis/Traycer"
#define MyUpdatesUrl "https://github.com/thomas-mardis/Traycer/releases"

[Setup]
AppId={{A6E7DA0E-432A-4D5A-96E7-5C4304C9BD79}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyPublisher}
AppPublisherURL={#MyPublisherUrl}
AppSupportURL={#MyPublisherUrl}
AppUpdatesURL={#MyUpdatesUrl}
DefaultDirName={autopf}\{#MyPublisher}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#MyExeName}
ArchitecturesInstallIn64BitMode=x64
PrivilegesRequired=admin
Compression=lzma
SolidCompression=yes
WizardStyle=modern
OutputDir=..\dist\installer
OutputBaseFilename=TraycerSetup_{#MyAppVersion}
SetupLogging=yes
UsePreviousAppDir=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"; Flags: unchecked
Name: "autostart"; Description: "Launch Traycer automatically when you sign in"; GroupDescription: "Startup options:"; Flags: checkedonce

[Files]
Source: "{#MySourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyExeName}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#MyAppName}"; ValueData: """{app}\{#MyExeName}"""; Tasks: autostart; Flags: uninsdeletevalue
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#MyAppName}"; ValueData: """{app}\{#MyExeName}"""; Tasks: autostart; Flags: uninsdeletevalue noerror

[Run]
Filename: "{app}\{#MyExeName}"; Description: "Launch Traycer HUD"; Flags: nowait postinstall skipifsilent
