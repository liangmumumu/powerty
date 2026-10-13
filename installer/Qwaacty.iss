#define AppName "Qwaacty"
#define AppVersion "0.13.0"
#define AppPublisher "Qwaacty"
#define AppExeName "Qwaacty.App.exe"

[Setup]
AppId={{96C80203-6CBA-4627-B6A4-7A172772F2D7}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={localappdata}\Programs\Qwaacty
DefaultGroupName={#AppName}
PrivilegesRequired=lowest
OutputBaseFilename=Qwaacty-Setup
ArchitecturesInstallIn64BitMode=x64compatible
Compression=lzma
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\src\Qwaacty.App\app.ico
UninstallDisplayIcon={app}\{#AppExeName}

[Tasks]
Name: "autostart"; Description: "登录 Windows 时自动启动"; GroupDescription: "可选设置："; Flags: unchecked

[Files]
Source: "..\artifacts\publish\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Qwaacty"; ValueData: """{app}\{#AppExeName}"""; Tasks: autostart; Flags: uninsdeletevalue
; Unconditional cleanup: the in-app "start with Windows" toggle writes this value
; outside the installer, so uninstall must remove it even if the task was unchecked.
; ValueType "none" means Setup writes nothing at install time but still deletes the
; value at uninstall.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "Qwaacty"; Flags: uninsdeletevalue
