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
#define MyAppVersion "2.1.0"
#define MyAppPublisher "DWG Translator"
#define MyAppExeName "DwgTranslator.exe"
#define PublishDir "..\artifacts\publish"

[Setup]
AppId={{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
OutputDir=..\artifacts
OutputBaseFilename=DwgTranslator_Setup_{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
SetupLogging=yes
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#MyAppExeName}
InfoBeforeFile=使用说明.txt

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startmenu"; Description: "创建开始菜单快捷方式"; GroupDescription: "{cm:AdditionalIcons}"; Flags: checked

[Files]
; The complete self-contained application: the exe, the CAD plugin folder, prompts, glossary and a
; valid settings.json. No .NET runtime has to be installed on the target machine.
Source: "{#PublishDir}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\settings.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\CadPlugin\*"; DestDir: "{app}\CadPlugin"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishDir}\prompts\*"; DestDir: "{app}\prompts"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishDir}\glossaries\*"; DestDir: "{app}\glossaries"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "使用说明.txt"; DestDir: "{app}"; Flags: ignoreversion

[Dirs]
Name: "{userappdata}\DwgTranslator"; Flags: uninsneveruninstall
Name: "{userappdata}\DwgTranslator\logs"; Flags: uninsneveruninstall
Name: "{userappdata}\DwgTranslator\exports"; Flags: uninsneveruninstall
Name: "{userappdata}\DwgTranslator\glossaries"; Flags: uninsneveruninstall

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\环境自检与安装 CAD 插件"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\使用说明"; Filename: "{app}\使用说明.txt"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; The installer finishes by offering the one step that has to run on this machine: copying the CAD
; plugin into the CAD installation. --env-check lands the program directly on that screen.
Filename: "{app}\{#MyAppExeName}"; Parameters: "--env-check"; Description: "立即启动 {#MyAppName} 并安装 CAD 插件"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurStepChanged(CurStep: TSetupStep);
var
  AppDataDir, SettingsSrc, SettingsDst: String;
begin
  if CurStep = ssPostInstall then
  begin
    AppDataDir := ExpandConstant('{userappdata}\DwgTranslator');

    // Seed the user settings only when the user has none yet, and only from a non-empty file:
    // copying an empty settings.json makes the program fall back to defaults silently.
    SettingsSrc := ExpandConstant('{app}\settings.json');
    SettingsDst := AppDataDir + '\settings.json';
    if FileExists(SettingsSrc) and (FileSize(SettingsSrc) > 0) and not FileExists(SettingsDst) then
      FileCopy(SettingsSrc, SettingsDst, False);

    if DirExists(ExpandConstant('{app}\glossaries')) then
      CopyDir(ExpandConstant('{app}\glossaries'), AppDataDir + '\glossaries');
  end;
end;
