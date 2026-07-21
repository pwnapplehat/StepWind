; StepWind Inno Setup script.
; Build with:  iscc installer\stepwind.iss
; Expects published binaries in dist\win-x64 (run build\publish.ps1 first).

#define AppName "StepWind"
#define AppVersion "1.0.0"
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
; Let Restart Manager close + reopen the tray GUI around the file swap so its locked
; StepWind.exe can be replaced. Deliberately NO AppMutex: with /SUPPRESSMSGBOXES the AppMutex
; "app is running" prompt auto-answers Cancel and ABORTS the whole (silent auto-)update before
; any file is copied — the exact bug that left the service binary stale. The service itself is
; stopped in code (CurStepChanged, ssInstall) before the copy.
CloseApplications=yes
RestartApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; The whole self-contained publish output (service + GUI + CLI + MCP server + runtime + web UI).
Source: "{#DistDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
; SPACELESS copy of the MCP server. Several MCP clients (Cursor included -- verified from its
; logs) spawn the stdio command through cmd.exe WITHOUT quoting, so a "C:\Program Files\..."
; command executes 'C:\Program' and dies. {commonappdata}\StepWind\bin has no space in it and
; inherits Users read+execute (only the store\ subdir is ACL-hardened), so every user's AI
; tool can run it. The app writes THIS path into AI tools' MCP configs.
Source: "{#DistDir}\StepWind.Mcp.exe"; DestDir: "{commonappdata}\StepWind\bin"; Flags: ignoreversion
; WebView2 Evergreen bootstrapper (Microsoft-signed, ~1.7 MB): the GUI renders in WebView2.
; Win11 and current Win10 ship the runtime; this covers older Win10/LTSC where it's absent.
; Extracted to {tmp} and run ONLY when the runtime registry key is missing (see [Run]).
Source: "redist\MicrosoftEdgeWebView2Setup.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\StepWind.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\StepWind.exe"; Tasks: desktopicon

; Start the tray GUI for all users at logon (unelevated; the service does the privileged work).
[Registry]
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
  ValueName: "StepWind"; ValueData: """{app}\StepWind.exe"" --minimized"; Flags: uninsdeletevalue

[Code]
// The service holds its binaries locked while running. Files are copied at ssInstall, so the
// service MUST be fully STOPPED before that -- otherwise the copy races a live service, and
// its crash-recovery restart can bring a NEW instance up on top of half-copied DLLs (observed
// live: the running service then threw "Could not load System.IO.Pipes.AccessControl" on
// every pipe accept until the next clean restart, so the GUI read "service not reachable").
//
// We do NOT blind-sleep: "sc stop" is asynchronous, so a fixed Sleep can return before the
// service is actually down. Instead we first neutralize the crash-recovery action (so a stop
// can't be mistaken for a crash and auto-restarted mid-copy), then issue the stop and POLL
// "sc query" until it reports STOPPED (or is already gone). Only then does the copy proceed.
procedure StopServiceAndWait;
var
  ResultCode, I: Integer;
  Output: AnsiString;
  TmpFile: string;
begin
  // Disarm failure actions for the duration of the copy (reset delay 0, no restart commands).
  Exec(ExpandConstant('{sys}\sc.exe'), 'failure StepWind reset= 0 actions= ///', '',
    SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop StepWind', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  TmpFile := ExpandConstant('{tmp}\sw_scq.txt');
  for I := 0 to 40 do
  begin
    Exec(ExpandConstant('{cmd}'), '/c sc query StepWind > "' + TmpFile + '" 2>&1', '',
      SW_HIDE, ewWaitUntilTerminated, ResultCode);
    if LoadStringFromFile(TmpFile, Output) then
    begin
      // 1060 = service not installed (fresh install / already removed); STOPPED = down.
      if (Pos('1060', Output) > 0) or (Pos('STOPPED', Output) > 0) then
        Break;
    end;
    Sleep(500);
  end;

  // The unelevated tray GUI has no failure action, so a forced kill is safe and frees StepWind.exe.
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM StepWind.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(400);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
    StopServiceAndWait;
end;

// The GUI renders in WebView2. Win11 + current Win10 already have the Evergreen runtime;
// this returns true only where it's genuinely absent (old Win10/LTSC), so the bundled
// Microsoft bootstrapper runs just for them (checks both per-machine locations, x64+x86).
function WebView2RuntimeMissing: Boolean;
var
  Version: string;
begin
  Result := not (
    RegQueryStringValue(HKLM,
      'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}',
      'pv', Version) or
    RegQueryStringValue(HKLM,
      'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}',
      'pv', Version));
  if not Result then
    Result := (Version = '') or (Version = '0.0.0.0');
end;

[Run]
; Ensure the WebView2 runtime exists before anything launches the GUI (silent, ~2 MB download
; via Microsoft's own bootstrapper; skipped entirely on machines that already have it).
Filename: "{tmp}\MicrosoftEdgeWebView2Setup.exe"; Parameters: "/silent /install"; \
  Flags: runhidden waituntilterminated; StatusMsg: "Installing Microsoft WebView2 runtime..."; \
  Check: WebView2RuntimeMissing
; Register + start the background service (its verb does stop/delete/create/start).
Filename: "{app}\StepWind.Service.exe"; Parameters: "install-service"; Flags: runhidden waituntilterminated; StatusMsg: "Installing StepWind protection service..."
; Launch the tray app now (as the invoking user, unelevated where possible).
Filename: "{app}\StepWind.exe"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
; Stop + remove the service before files are deleted.
Filename: "{app}\StepWind.Service.exe"; Parameters: "uninstall-service"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveStepWindSvc"

[UninstallDelete]
; Leave the user's version history (%ProgramData%\StepWind) intact on uninstall, but remove
; the MCP server copy (a dead exe path in AI tools' configs is useless without the service).
Type: filesandordirs; Name: "{app}\logs"
Type: filesandordirs; Name: "{commonappdata}\StepWind\bin"
