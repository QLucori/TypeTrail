#ifndef PayloadDir

  #error PayloadDir is required

#endif

#ifndef OutputDir

  #error OutputDir is required

#endif

#define AppVersion "1.0.1"


  #define AppIdentity "TypeTrail.Desktop"

  #define ProductName "{cm:AppName}"

  #define InstallFolder "TypeTrail"

  #define ProfileFolder "TypeTrail"

  #define ShortcutName "{cm:AppName}"

  #define DesktopName "{cm:AppName}"

  #define StartupName "拾字"

  #define OutputName "TypeTrail-Setup-" + AppVersion + "-x64"




[Setup]

AppId={#AppIdentity}

AppName={#ProductName}

AppVersion={#AppVersion}

AppPublisher=QLucori

AppVerName={#ProductName} {#AppVersion}

DefaultDirName={localappdata}\Programs\{#InstallFolder}

PrivilegesRequired=lowest

ArchitecturesAllowed=x64compatible

ArchitecturesInstallIn64BitMode=x64compatible

MinVersion=10.0

DisableProgramGroupPage=yes

DisableDirPage=no

UsePreviousAppDir=yes

UsePreviousTasks=no

UninstallDisplayName={#ProductName}

UninstallDisplayIcon={app}\TypeTrail.exe

SetupIconFile={#SourcePath}\..\assets\TypeTrail.ico

WizardStyle=modern dynamic

WizardImageFile={#PayloadDir}\wizard-brand.png

WizardImageFileDynamicDark={#PayloadDir}\wizard-brand.png

WizardSmallImageFile={#SourcePath}\..\assets\TypeTrail.png

WizardSmallImageFileDynamicDark={#SourcePath}\..\assets\TypeTrail.png

WizardSizePercent=110

DisableWelcomePage=no

Compression=lzma2/max

SolidCompression=yes

CloseApplications=no

RestartApplications=no

SetupLogging=yes

OutputDir={#OutputDir}

OutputBaseFilename={#OutputName}

VersionInfoVersion={#AppVersion}.0

VersionInfoDescription=TypeTrail Setup

VersionInfoProductName=TypeTrail



[Languages]

Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

Name: "english"; MessagesFile: "compiler:Default.isl"



[CustomMessages]

chinesesimp.AppName=拾字

english.AppName=TypeTrail

chinesesimp.DesktopTask=创建桌面图标

english.DesktopTask=Create a desktop icon

chinesesimp.StartupTask=登录 Windows 时在后台启动

english.StartupTask=Start in the background when I sign in to Windows

chinesesimp.LaunchApp=打开拾字

english.LaunchApp=Open TypeTrail

chinesesimp.StopFailed=拾字暂时未能正常退出。请从托盘菜单退出拾字，再重试安装。

english.StopFailed=TypeTrail could not exit cleanly. Please exit it from its tray menu and try again.

chinesesimp.DotNetRequired=拾字需要 Microsoft .NET Framework 4.8 或更高版本。请先从 Microsoft 官网安装运行环境，再重新运行此安装包。尚未更改现有程序和历史。

english.DotNetRequired=TypeTrail requires Microsoft .NET Framework 4.8 or later. Install the runtime from Microsoft's website, then run this installer again. Your existing application and history have not been changed.

chinesesimp.WelcomeLabel2=拾字会保存你输入过的文字，方便随时找回和复制。%n%n安装后可从开始菜单打开，也可用 Alt + ` 呼出历史。%n%n升级会保留本机历史和设置；卸载也会保留历史，便于以后重新安装。

english.WelcomeLabel2=TypeTrail remembers the text you type so you can find and copy it later.%n%nOpen it from Start or press Alt + ` to show your history.%n%nUpdates keep your local history and settings. Uninstalling also keeps them for a later reinstall.



[Messages]

chinesesimp.WelcomeLabel2=拾字会保存你输入过的文字，方便随时找回和复制。%n%n安装后可从开始菜单打开，也可用 Alt + ` 呼出历史。%n%n升级会保留本机历史和设置；卸载也会保留历史，便于以后重新安装。

english.WelcomeLabel2=TypeTrail remembers the text you type so you can find and copy it later.%n%nOpen it from Start or press Alt + ` to show your history.%n%nUpdates keep your local history and settings. Uninstalling also keeps them for a later reinstall.



[Tasks]

Name: "desktopicon"; Description: "{cm:DesktopTask}"; Flags: unchecked

Name: "startup"; Description: "{cm:StartupTask}"



[Files]

Source: "{#PayloadDir}\TypeTrail.exe"; DestDir: "{app}"; Flags: ignoreversion

Source: "{#PayloadDir}\TypeTrail.exe.config"; DestDir: "{app}"; Flags: ignoreversion

Source: "{#PayloadDir}\TypeTrail.InputBridge.1.7.13.r6.dll"; DestDir: "{app}"; Flags: ignoreversion; Check: NativeBridgeNeedsCopy

Source: "{#PayloadDir}\TypeTrail.InputBridge.1.7.13.r6.x86.dll"; DestDir: "{app}"; Flags: ignoreversion; Check: X86BridgeNeedsCopy

Source: "{#PayloadDir}\TypeTrail.InputHost.x86.exe"; DestDir: "{app}"; Flags: ignoreversion

Source: "{#PayloadDir}\TypeTrail.SetupHelper.exe"; DestDir: "{app}"; Flags: ignoreversion

Source: "{#PayloadDir}\使用说明.txt"; DestDir: "{app}"; Flags: ignoreversion

Source: "{#PayloadDir}\README-English.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\UNICODE-LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion



[InstallDelete]

; Remove the previous display-name shortcut when upgrading.
Type: files; Name: "{userprograms}\拾字 TypeTrail.lnk"
Type: files; Name: "{userprograms}\拾字.lnk"
Type: files; Name: "{userprograms}\TypeTrail.lnk"
Type: files; Name: "{userdesktop}\拾字.lnk"; Tasks: desktopicon
Type: files; Name: "{userdesktop}\TypeTrail.lnk"; Tasks: desktopicon

[Icons]

Name: "{userprograms}\{#ShortcutName}"; Filename: "{app}\TypeTrail.exe"; Parameters: "{code:ShowArguments}"; WorkingDir: "{app}"; AppUserModelID: "{#AppIdentity}"

Name: "{userdesktop}\{#DesktopName}"; Filename: "{app}\TypeTrail.exe"; Parameters: "{code:ShowArguments}"; WorkingDir: "{app}"; AppUserModelID: "{#AppIdentity}"; Tasks: desktopicon

; Uninstall removes only a startup link that still targets this installation.

Name: "{userstartup}\{#StartupName}"; Filename: "{app}\TypeTrail.exe"; Parameters: "{code:BackgroundArguments}"; WorkingDir: "{app}"; Tasks: startup; Flags: uninsneveruninstall



[Registry]

Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\{#InstallFolder}.exe"; ValueType: string; ValueName: ""; ValueData: "{app}\TypeTrail.exe"; Flags: uninsdeletekey



[Run]

Filename: "{app}\TypeTrail.exe"; Parameters: "{code:ShowArguments}"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent



[Code]

function ProfilePath: String;

begin

  Result := ExpandConstant('{localappdata}\{#ProfileFolder}');

end;



function ShowArguments(Param: String): String;

begin

  Result := '--show';


end;



function BackgroundArguments(Param: String): String;

begin

  Result := '--background';


end;



function NativeBridgeNeedsCopy: Boolean;

var

  Path: String;

begin

  Path := ExpandConstant('{app}\TypeTrail.InputBridge.1.7.13.r6.dll');

  Result := True;

  if FileExists(Path) then

    Result := CompareText(GetSHA256OfFile(Path), '{#GetSHA256OfFile(PayloadDir + "\TypeTrail.InputBridge.1.7.13.r6.dll")}') <> 0;

end;



function X86BridgeNeedsCopy: Boolean;

var

  Path: String;

begin

  Path := ExpandConstant('{app}\TypeTrail.InputBridge.1.7.13.r6.x86.dll');

  Result := True;

  if FileExists(Path) then

    Result := CompareText(GetSHA256OfFile(Path), '{#GetSHA256OfFile(PayloadDir + "\TypeTrail.InputBridge.1.7.13.r6.x86.dll")}') <> 0;

end;



procedure InitializeWizard;

var

  ExistingProfile, ExistingStartup, ExistingDesktop: Boolean;

begin

  ExistingProfile := FileExists(ProfilePath + '\settings.json');

  ExistingStartup := FileExists(ExpandConstant('{userstartup}\{#StartupName}.lnk'));

  ExistingDesktop := FileExists(ExpandConstant('{userdesktop}\{#DesktopName}.lnk'));

  if ExistingProfile and not ExistingStartup then WizardSelectTasks('!startup');

  if ExistingDesktop then WizardSelectTasks('desktopicon');

end;



function PrepareToInstall(var NeedsRestart: Boolean): String;

var

  ExitCode: Integer;

begin

  Result := '';

  if not IsDotNetInstalled(net48, 0) then
  begin
    Result := CustomMessage('DotNetRequired');
    Exit;
  end;

  ExtractTemporaryFile('TypeTrail.SetupHelper.exe');

  if not Exec(ExpandConstant('{tmp}\TypeTrail.SetupHelper.exe'),

    'stop "' + ProfilePath + '" "' + ExpandConstant('{app}\TypeTrail.exe') + '"', '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then

    Result := CustomMessage('StopFailed')

  else if ExitCode <> 0 then Result := CustomMessage('StopFailed');

end;



procedure CurStepChanged(CurStep: TSetupStep);

var

  ExitCode: Integer;

begin

  if (CurStep = ssPostInstall) and not WizardIsTaskSelected('startup') then

    Exec(ExpandConstant('{app}\TypeTrail.SetupHelper.exe'),

      'remove-startup "' + ExpandConstant('{userstartup}\{#StartupName}.lnk') +

      '" "' + ExpandConstant('{app}\TypeTrail.exe') + '"', '', SW_HIDE, ewWaitUntilTerminated, ExitCode);

end;



function InitializeUninstall: Boolean;

var

  ExitCode: Integer;

begin

  Result := Exec(ExpandConstant('{app}\TypeTrail.SetupHelper.exe'),

    'stop "' + ProfilePath + '" "' + ExpandConstant('{app}\TypeTrail.exe') + '"', '', SW_HIDE, ewWaitUntilTerminated, ExitCode);

  if Result then Result := ExitCode = 0;

  if not Result then MsgBox(CustomMessage('StopFailed'), mbError, MB_OK);

end;



procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);

var

  ExitCode: Integer;

begin

  if CurUninstallStep = usUninstall then

    Exec(ExpandConstant('{app}\TypeTrail.SetupHelper.exe'),

      'remove-startup "' + ExpandConstant('{userstartup}\{#StartupName}.lnk') +

      '" "' + ExpandConstant('{app}\TypeTrail.exe') + '"', '', SW_HIDE, ewWaitUntilTerminated, ExitCode);

end;

