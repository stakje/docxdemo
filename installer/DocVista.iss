#ifndef MyAppVersion
  #define MyAppVersion "0.1.5"
#endif

#ifndef VelopackSetupPath
  #error VelopackSetupPath must be provided by scripts/package.ps1
#endif

#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif

#ifndef MyChannel
  #define MyChannel "win"
#endif

[Setup]
AppId=DocVista.ClassicSetup
AppName=DocVista
AppVersion={#MyAppVersion}
AppPublisher=DocVista
AppPublisherURL=https://github.com/stakje/docxdemo
AppSupportURL=https://github.com/stakje/docxdemo/issues
DefaultDirName={localappdata}\DocVista
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableWelcomePage=no
InfoBeforeFile={#SourcePath}\readme.txt
InfoAfterFile={#SourcePath}\conclusion.txt
OutputDir={#OutputDir}
OutputBaseFilename=DocVista-{#MyChannel}-Setup
Compression=lzma2/max
SolidCompression=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
WizardStyle=classic
SetupLogging=yes
Uninstallable=no
CreateUninstallRegKey=no
CreateAppDir=no
CloseApplications=no
RestartApplications=no
ShowLanguageDialog=no
VersionInfoVersion={#MyAppVersion}
VersionInfoProductVersion={#MyAppVersion}
VersionInfoDescription=DocVista 经典安装向导
SetupIconFile={#SourcePath}\..\assets\DocVista.ico

[Languages]
Name: "chinesesimplified"; MessagesFile: "{#SourcePath}\ChineseSimplified.isl"

[Messages]
WelcomeLabel1=欢迎使用 DocVista 安装向导
WelcomeLabel2=此向导将安装 DocVista {#MyAppVersion}。%n%n建议在继续之前关闭正在运行的 DocVista。

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式(&D)"; GroupDescription: "附加任务："; Flags: checkedonce

[Files]
Source: "{#VelopackSetupPath}"; DestDir: "{tmp}"; DestName: "DocVista.Bootstrap.exe"; Flags: deleteafterinstall

[Icons]
Name: "{autodesktop}\DocVista"; Filename: "{localappdata}\DocVista\DocVista.exe"; WorkingDir: "{localappdata}\DocVista"; IconFilename: "{localappdata}\DocVista\DocVista.exe"; Tasks: desktopicon

[Run]
Filename: "{tmp}\DocVista.Bootstrap.exe"; Parameters: "--silent"; StatusMsg: "正在安装 DocVista，请稍候..."; Flags: runhidden waituntilterminated
Filename: "{localappdata}\DocVista\DocVista.exe"; Description: "启动 DocVista"; Flags: postinstall nowait skipifsilent unchecked

[Code]
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and
     (not FileExists(ExpandConstant('{localappdata}\DocVista\DocVista.exe'))) then
    RaiseException('DocVista 主程序没有成功安装。请查看安装日志后重试。');
end;
