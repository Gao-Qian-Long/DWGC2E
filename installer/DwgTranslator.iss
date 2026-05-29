; DWG Translator Installer Script
; Requires Inno Setup 6.x

#define MyAppName "DWG Translator"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "DWG Translator"
#define MyAppExeName "DwgTranslator.exe"

[Setup]
AppId={{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
OutputDir=..\output
OutputBaseFilename=DwgTranslator_Setup_{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
PrivilegesRequired=lowest
SetupLogging=yes

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startmenu"; Description: "Create Start Menu shortcut"; GroupDescription: "{cm:AdditionalIcons}"; Flags: checked

[Files]
; Main application (self-contained single-file publish)
Source: "..\src\DwgTranslator.App\bin\Release\net8.0-windows\win-x64\publish\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
; Bundled resources
Source: "..\settings.json"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\prompts\*"; DestDir: "{app}\prompts"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist
Source: "..\glossaries\*"; DestDir: "{app}\glossaries"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist

[Dirs]
; AppData directories (created at runtime, but ensure they exist)
Name: "{userappdata}\DwgTranslator"; Flags: uninsneveruninstall
Name: "{userappdata}\DwgTranslator\logs"; Flags: uninsneveruninstall
Name: "{userappdata}\DwgTranslator\exports"; Flags: uninsneveruninstall
Name: "{userappdata}\DwgTranslator\glossaries"; Flags: uninsneveruninstall

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
// Copy bundled settings to AppData on first install
procedure CurStepChanged(CurStep: TSetupStep);
var
  AppDataDir, SettingsSrc, SettingsDst: String;
begin
  if CurStep = ssPostInstall then
  begin
    AppDataDir := ExpandConstant('{userappdata}\DwgTranslator');
    SettingsSrc := ExpandConstant('{app}\settings.json');
    SettingsDst := AppDataDir + '\settings.json';

    // Copy settings to AppData if not already present
    if FileExists(SettingsSrc) and not FileExists(SettingsDst) then
      FileCopy(SettingsSrc, SettingsDst, False);

    // Copy glossaries to AppData
    if DirExists(ExpandConstant('{app}\glossaries')) then
      CopyDir(ExpandConstant('{app}\glossaries'), AppDataDir + '\glossaries');
  end;
end;
