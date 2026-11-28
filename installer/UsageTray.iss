#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
AppId={{72466F56-DB53-4947-A923-ED6639B70539}
AppName=AI Usage Tray
AppVersion={#AppVersion}
AppPublisher=pey-45
AppPublisherURL=https://github.com/pey-45/ai-usage-tray
DefaultDirName={localappdata}\Programs\AI Usage Tray
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
DisableProgramGroupPage=yes
UsePreviousTasks=yes
OutputDir=..\artifacts\installer
OutputBaseFilename=AI-Usage-Tray-{#AppVersion}-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\UsageTray.exe
CloseApplications=yes
RestartApplications=no
SetupMutex=AIUsageTraySetup
AppMutex=UsageTray

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "autostart"; Description: "Arrancar con Windows"; GroupDescription: "Opciones:"

[Files]
Source: "..\artifacts\publish\UsageTray.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\AI Usage Tray"; Filename: "{app}\UsageTray.exe"; WorkingDir: "{app}"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "UsageTray"; ValueData: """{app}\UsageTray.exe"""; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\UsageTray.exe"; Description: "Abrir AI Usage Tray"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurStepChanged(CurStep: TSetupStep);
begin
  // Also remove an old startup entry when the task is deselected on an upgrade.
  if (CurStep = ssPostInstall) and not WizardIsTaskSelected('autostart') then
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'UsageTray');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  // The application can enable startup after installation, even if the task was off.
  if CurUninstallStep = usUninstall then
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'UsageTray');
end;
