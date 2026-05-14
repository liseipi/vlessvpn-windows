; Inno Setup 安装脚本
; 下载 Inno Setup: https://jrsoftware.org/isinfo.php
; 用法: iscc scripts\installer\setup.iss

#define MyAppName "VLESS Client"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "VlessClient"
#define MyAppURL "https://github.com/your-repo"
#define MyAppExeName "VlessClient.exe"

[Setup]
AppId={{B8F42A1E-9D35-4E72-A1C6-8F8D35E2B19A}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
OutputBaseFilename=VlessClient-Setup-{#MyAppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
UninstallDisplayIcon={app}\{#MyAppExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Files]
Source: "..\..\VlessClient\bin\x64\Release\net8.0-windows10.0.22621.0\win-x64\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\..\VlessClient\bin\x64\Release\net8.0-windows10.0.22621.0\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall VLESS Client"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch VLESS Client"; Flags: nowait postinstall skipifsilent
