; DWG Translator — Inno Setup 6.x script
;
; Build the publish output first (publish.bat), then compile this script with ISCC.
; On a machine without Inno Setup use tools\New-ReleasePackage.ps1 instead: it produces the same
; deliverable as a portable folder plus a one-click 安装.cmd bootstrap.
;
; Everything is packed from the PUBLISH OUTPUT rather than from the repository, because that folder
; is the only one that is known to be complete: it carries the CAD plugin, the prompts, the glossary
; and a valid settings.json. An earlier version of this script packed ..\settings.json from the
; repository root, which is a 0-byte placeholder there and made the first start fail.

#define MyAppName "DWG Translator"
#define MyAppDirName "DWGC2E"
// Version is read from the verified executable below.
#define MyAppPublisher "DWG Translator"
#define MyAppExeName "DwgTranslator.exe"
#ifndef PublishDir
  #error Supply /DPublishDir=<verified artifacts publish directory>
#endif
#define MyAppVersion GetVersionNumbersString(PublishDir + "\DwgTranslator.exe")

; Isolated acceptance build only. Normal builds retain the production paths.
#ifdef TestRoot
  #define UserDataDir TestRoot + "\user-data"
  #define MenuDir TestRoot + "\shortcuts\menu"
  #define DesktopDir TestRoot + "\shortcuts\desktop"
#else
  #define UserDataDir "{userappdata}\DwgTranslator"
  #define MenuDir "{group}"
  #define DesktopDir "{autodesktop}"
#endif

[Setup]
#ifdef TestRoot
AppId=DWGC2E-Isolated-Acceptance
CreateUninstallRegKey=no
UsePreviousAppDir=no
UsePreviousTasks=no
DefaultDirName={#TestRoot}\app
PrivilegesRequiredOverridesAllowed=commandline
#else
AppId={{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}
DefaultDirName={code:GetDefaultInstallDir}
UsePreviousAppDir=yes
DisableDirPage=no
PrivilegesRequiredOverridesAllowed=dialog
#endif
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultGroupName={#MyAppName}
OutputDir=..\artifacts
OutputBaseFilename=DwgTranslator_Setup_{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
SetupLogging=yes
DisableProgramGroupPage=yes
SetupIconFile=..\assets\icons\icon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
InfoBeforeFile=使用说明.txt

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startmenu"; Description: "创建开始菜单快捷方式"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; The complete self-contained application: the exe, the CAD plugin folder, prompts, glossary and a
; valid settings.json. No .NET runtime has to be installed on the target machine.
Source: "{#PublishDir}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\settings.json"; DestDir: "{app}"; Flags: onlyifdoesntexist uninsneveruninstall
Source: "{#PublishDir}\CadPlugin\*"; DestDir: "{app}\CadPlugin"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishDir}\prompts\*"; DestDir: "{app}\prompts"; Flags: onlyifdoesntexist uninsneveruninstall recursesubdirs createallsubdirs
Source: "{#PublishDir}\glossaries\*"; DestDir: "{app}\glossaries"; Flags: onlyifdoesntexist uninsneveruninstall recursesubdirs createallsubdirs
Source: "{#PublishDir}\assets\*"; DestDir: "{app}\assets"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishDir}\settings.json"; DestDir: "{#UserDataDir}"; Flags: onlyifdoesntexist uninsneveruninstall
Source: "{#PublishDir}\glossaries\*"; DestDir: "{#UserDataDir}\glossaries"; Flags: onlyifdoesntexist uninsneveruninstall recursesubdirs createallsubdirs
Source: "使用说明.txt"; DestDir: "{app}"; Flags: ignoreversion

[Dirs]
Name: "{#UserDataDir}"; Flags: uninsneveruninstall
Name: "{#UserDataDir}\logs"; Flags: uninsneveruninstall
Name: "{#UserDataDir}\exports"; Flags: uninsneveruninstall
Name: "{#UserDataDir}\glossaries"; Flags: uninsneveruninstall

[Icons]
Name: "{#MenuDir}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: startmenu
Name: "{#MenuDir}\环境自检与安装 CAD 插件"; Filename: "{app}\{#MyAppExeName}"; Parameters: "--env-check"; Tasks: startmenu
Name: "{#MenuDir}\使用说明"; Filename: "{app}\使用说明.txt"; Tasks: startmenu
Name: "{#MenuDir}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"; IconFilename: "{app}\{#MyAppExeName}"; Tasks: startmenu
Name: "{#DesktopDir}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

#ifndef TestRoot
[Run]
; The installer finishes by offering the one step that has to run on this machine: copying the CAD
; plugin into the CAD installation. --env-check lands the program directly on that screen.
Filename: "{app}\{#MyAppExeName}"; Parameters: "--env-check"; Description: "立即启动 {#MyAppName} 并安装 CAD 插件"; Flags: nowait postinstall skipifsilent

; User configuration and glossary seeding is declarative above; existing data is preserved.

#endif

[Code]
function SelectInstallDir(HasDDrive: Boolean): String;
begin
  if HasDDrive then
    Result := 'D:\{#MyAppDirName}'
  else
    Result := 'C:\{#MyAppDirName}';
end;

function GetDefaultInstallDir(Param: String): String;
begin
  Result := SelectInstallDir(DirExists('D:\'));
end;

function IsDriveRoot(Path: String): Boolean;
begin
  Result := ((Length(Path) = 2) and (Copy(Path, 2, 1) = ':')) or
    ((Length(Path) = 3) and (Copy(Path, 2, 1) = ':') and
     ((Copy(Path, 3, 1) = '\') or (Copy(Path, 3, 1) = '/')));
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if IsDriveRoot(ExpandConstant('{app}')) then
  begin
    Result := 'Cannot install into a drive root. Choose a separate installation directory.';
    Exit;
  end;
  if FileExists(ExpandConstant('{app}\DwgTranslator.sln')) or
     DirExists(ExpandConstant('{app}\.git')) or
     FileExists(ExpandConstant('{app}\.git')) then
  begin
    Result := 'Cannot install into a source workspace. Choose a separate installation directory.';
    Exit;
  end;
#ifdef TestRoot
  if (SelectInstallDir(True) <> 'D:\{#MyAppDirName}') or
     (SelectInstallDir(False) <> 'C:\{#MyAppDirName}') then
  begin
    Result := 'Default install directory regression failed.';
    Exit;
  end;
  Log('Default directory branches passed: D present / D absent');
  if (not IsDriveRoot('C:\')) or (not IsDriveRoot('D:\')) or
     (not IsDriveRoot('D:')) or (not IsDriveRoot('C:/')) or
     IsDriveRoot('D:\DWGC2E') or IsDriveRoot('C:\DWGC2E') then
  begin
    Result := 'Drive root guard regression failed.';
    Exit;
  end;
  Log('Drive root guard branches passed');
  if CompareText(ExpandConstant('{app}'), ExpandConstant('{#TestRoot}\app')) <> 0 then
    Result := 'Isolated acceptance build: destination override rejected.';
#endif
end;