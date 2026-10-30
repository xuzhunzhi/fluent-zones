; Fluent Zones —— Inno Setup 6 安装包脚本
; 由 packaging/build-release.ps1 调用（会传入 /DAppVersion=x.y.z）。
; 产物：dist\FluentZones-<版本>-Setup-x64.exe

#define AppName "Fluent Zones"
#define AppPublisher "XuZhunzhi"
#define AppUrl "https://github.com/xuzhunzhi/fluent-zones"

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

#ifndef PayloadDir
  #define PayloadDir "..\..\dist\FluentZones"
#endif

#ifndef OutputDir
  #define OutputDir "..\..\dist"
#endif

[Setup]
; 固定的 AppId：升级/卸载靠它认人，改了会被当成另一个程序
AppId={{429C1D80-B7DF-4C57-A16B-E4269205C6C2}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
DefaultDirName={localappdata}\Programs\Fluent Zones
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir={#OutputDir}
OutputBaseFilename=FluentZones-{#AppVersion}-Setup-x64
SetupIconFile=..\..\src\FluentZones.UI\Assets\app.ico
UninstallDisplayIcon={app}\FluentZones.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
MinVersion=10.0.19041

[Languages]
; 官方仓库的简体中文翻译（ChineseSimplified.isl）是可选的：放进本目录就会自动启用。
; 没有该文件时只有英文向导，不影响安装。
#if FileExists(AddBackslash(SourcePath) + "ChineseSimplified.isl")
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"
#endif
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: checkedonce

[Files]
; 自包含运行时的完整目录（由 build-release.ps1 的 dotnet publish 生成）
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Fluent Zones"; Filename: "{app}\FluentZones.exe"; WorkingDir: "{app}"
; 管理员模式：用 PowerShell 以 RunAs 启动主程序（MSIX 渠道做不到这一点）
Name: "{group}\{#AppName}（管理员）"; Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command ""Start-Process -FilePath '{app}\FluentZones.exe' -Verb RunAs"""; IconFilename: "{app}\FluentZones.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\FluentZones.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\FluentZones.exe"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"
