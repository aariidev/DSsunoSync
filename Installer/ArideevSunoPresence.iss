#define MyAppName "Arideev Suno Presence"
#ifndef MyAppVersion
#define MyAppVersion "0.3.0"
#endif
#define MyAppPublisher "Arideev"
#define MyAppExeName "ArideevSunoPresence.exe"

[Setup]
AppId={{A2E3149B-DB2A-4B5A-A30E-0A2D3E0E0200}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\Arideev\Suno Presence
DefaultGroupName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
OutputDir=..\dist
OutputBaseFilename=ArideevSunoPresence-Setup-x64-{#MyAppVersion}
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
PrivilegesRequired=lowest
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\Assets\Arideev.ico

[Tasks]
Name: "desktopicon"; Description: "Crear acceso directo en el escritorio"; GroupDescription: "Accesos directos:"; Flags: unchecked

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Arideev Suno Presence"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\Arideev Suno Presence"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Abrir Arideev Suno Presence"; Flags: postinstall nowait skipifsilent

[Code]
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if (CurStep = ssPostInstall) and WizardSilent then
    Exec(ExpandConstant('{app}\{#MyAppExeName}'), '--background', '', SW_SHOWNORMAL, ewNoWait, ResultCode);
end;
