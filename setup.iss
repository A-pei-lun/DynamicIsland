; DynamicIsland 安装程序 — Inno Setup 6
; 用法: ISCC.exe setup.iss
; 或通过 build-release.ps1 自动调用

#define MyAppName "DynamicIsland"
#define MyAppPublisher "APIRL"
#define MyAppURL "https://github.com/YOUR_USERNAME/DynamicIsland"
#define MyAppExeName "DynamicIsland.exe"

#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif

#ifndef MyAppOutputDir
  #define MyAppOutputDir "publish"
#endif

[Setup]
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
OutputDir={#MyAppOutputDir}
OutputBaseFilename=DynamicIsland-Setup-{#MyAppVersion}
SetupIconFile=DynamicIsland\AppIcon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes
PrivilegesRequired=none

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式:"; Flags: checkedonce
Name: "autostart"; Description: "开机自动启动"; GroupDescription: "启动选项:";

[Files]
Source: "{#MyAppOutputDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{userstartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: autostart

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "运行 {#MyAppName}"; Flags: postinstall nowait skipifsilent shellexec

[UninstallRun]
; 确保进程退出后再卸载文件
Filename: "{cmd}"; Parameters: "/c taskkill /f /im {#MyAppExeName} 2>nul"; Flags: runhidden
