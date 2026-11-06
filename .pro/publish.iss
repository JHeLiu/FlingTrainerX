
; 基于 InternetFashionedInstaller 的原始按钮、绘图及页面事件。
; 上游版本、许可证与本地兼容修改见 installer-template/README.md。
#ifndef AppName
  #define AppName "风灵月影宗藏宝阁"
#endif
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef PublishDir
  #define PublishDir AppVersion + "\publish"
#endif
#ifndef OutputDir
  #define OutputDir AppVersion
#endif
#ifndef InstallerBaseName
  #define InstallerBaseName AppName + "-安装包-" + AppVersion
#endif
#ifndef AppIconPath
  #define AppIconPath "..\FlingTrainerX\Assets\app.ico"
#endif
#define MyAppID "{6FBA2F09-30A8-4EB7-B6CB-B42A0FB7F90D}"
#define MyAppName AppName
#define MyAppVersion AppVersion
#define MyAppExeName AppName + ".exe"
#define MyAppLicenseURL ""
; 改名前的旧名字：升级时把残留的旧 exe / 旧快捷方式清掉。
; 安装目录不跟着改，老用户升级后 dbs\ 和 f\ 里的收藏、缓存照旧还在。
#define LegacyAppName "风灵月影宗储物袋"
#define x64Build

[Setup]
AppId={{#MyAppID}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppName}
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
OutputDir={#OutputDir}
OutputBaseFilename={#InstallerBaseName}
SetupIconFile={#AppIconPath}
UninstallDisplayIcon={app}\{#MyAppExeName}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.14393
PrivilegesRequired=lowest
Compression=lzma2
SolidCompression=yes
WizardStyle=classic
WizardResizable=no
DisableWelcomePage=no
DisableFinishedPage=no
DisableProgramGroupPage=yes
DisableDirPage=yes
DisableReadyMemo=yes
DisableReadyPage=yes
ShowLanguageDialog=no
AllowCancelDuringInstall=no
SetupMutex=FlingTrainerXInstaller-6FBA2F09

[Languages]
Name: "zh_CN"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Files]
; 先保存小型 UI 资源，避免启动界面前解压整个应用，提升启动响应。
Source: "installer-template\tmp\*"; DestDir: "{tmp}"; Flags: dontcopy solidbreak nocompression
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs solidbreak

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Check: CreateDesktopShortcut
Name: "{userappdata}\Microsoft\Internet Explorer\Quick Launch\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Check: CreateQuickLaunchShortcut

[InstallDelete]
; 改名（储物袋 → 藏宝阁）后升级：旧 exe 和旧快捷方式留着会变成两个入口，装完顺手清掉
Type: files; Name: "{app}\{#LegacyAppName}.exe"
Type: files; Name: "{autoprograms}\{#LegacyAppName}.lnk"
Type: files; Name: "{autodesktop}\{#LegacyAppName}.lnk"
Type: files; Name: "{userappdata}\Microsoft\Internet Explorer\Quick Launch\{#LegacyAppName}.lnk"

[Code]
#include "installer-template\include\Code.iss"

