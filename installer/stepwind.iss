; StepWind Inno Setup script.
; Build with:  iscc installer\stepwind.iss
; Expects published binaries in dist\win-x64 (run build\publish.ps1 first).

#define AppName "StepWind"
#define AppVersion "1.0.1"
#define AppPublisher "StepWind Contributors"
#define AppURL "https://stepwind.app"
#define RepoURL "https://github.com/pwnapplehat/StepWind"
#define DistDir "..\dist\win-x64"

[Setup]
AppId={{B8E2B7F4-3C6A-4E2D-9E1A-7F2C5D8A6B10}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#RepoURL}/issues
; StepWind installs a system service (USN journal + ETW need SYSTEM), so it's a per-machine
; install under Program Files and requires elevation. The SYSTEM service later applies updates
; silently with no further prompts.
DefaultDirName={autopf}\{#AppName}
UsePreviousAppDir=yes
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
OutputDir=Output
OutputBaseFilename=StepWind-{#AppVersion}-setup
SetupIconFile=..\assets\icon.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
UninstallDisplayIcon={app}\StepWind.exe
; The auto-updater runs setup with /VERYSILENT; close + restart the GUI around the file swap.
AppMutex=StepWind.App.SingleInstance
CloseApplications=yes
RestartApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; The whole self-contained publish output (service + GUI + CLI + runtime).
Source: "{#DistDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\StepWind.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\StepWind.exe"; Tasks: desktopicon

; Start the tray GUI for all users at logon (unelevated; the service does the privileged work).
[Registry]
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
  ValueName: "StepWind"; ValueData: """{app}\StepWind.exe"" --minimized"; Flags: uninsdeletevalue

[Run]
; Stop any prior service instance before we (re)register — makes upgrades clean.
Filename: "{app}\StepWind.Service.exe"; Parameters: "stop-service"; Flags: runhidden waituntilterminated
; Register + start the background service.
Filename: "{app}\StepWind.Service.exe"; Parameters: "install-service"; Flags: runhidden waituntilterminated; StatusMsg: "Installing StepWind protection service..."
; Launch the tray app now (as the invoking user, unelevated where possible).
Filename: "{app}\StepWind.exe"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
; Stop + remove the service before files are deleted.
Filename: "{app}\StepWind.Service.exe"; Parameters: "uninstall-service"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveStepWindSvc"

[UninstallDelete]
; Leave the user's version history (%ProgramData%\StepWind) intact on uninstall.
Type: filesandordirs; Name: "{app}\logs"
