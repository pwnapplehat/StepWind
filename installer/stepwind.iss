; StepWind Inno Setup script.
; Build with:  iscc installer\stepwind.iss
; Expects published binaries in dist\win-x64 (run build\publish.ps1 first).

#define AppName "StepWind"
#define AppVersion "1.0.1"
#define AppPublisher "StepWind Contributors"
#define AppURL "https://stepwind.app"
#define RepoURL "https://github.com/pwnapplehat/StepWind"
; Target architecture — pass -DArch=arm64 to build the ARM64 installer (defaults to x64).
; The x64 installer keeps the plain name so the existing auto-updater finds it; arm64 gets an
; -arm64 suffix, which the updater's arch-aware asset picker prefers on ARM machines.
#ifndef Arch
  #define Arch "x64"
#endif
#define DistDir "..\dist\win-" + Arch
#if Arch == "arm64"
  #define ArchAllowed "arm64"
  #define SetupSuffix "-arm64-setup"
#else
  #define ArchAllowed "x64compatible"
  #define SetupSuffix "-setup"
#endif

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
OutputBaseFilename=StepWind-{#AppVersion}{#SetupSuffix}
SetupIconFile=..\assets\icon.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed={#ArchAllowed}
ArchitecturesInstallIn64BitMode={#ArchAllowed}
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
// ── Update rollback: THE INSTALLER IS THE ROLLBACK ACTOR ────────────────────────────────────
// The SYSTEM service cannot roll back its own update: this installer STOPS that service before
// swapping files, so the process that launched us is gone by the time a bad swap would need
// undoing. Therefore the transactional safety lives here, in Inno [Code]:
//   ssInstall     -> if this is an upgrade, stop the service and BACK UP the current install;
//   (files copied by Inno; [Run] re-registers + starts the service)
//   ssPostInstall -> HEALTH-GATE: confirm the service reaches RUNNING; if it doesn't, RESTORE
//                    the backup and re-register it, so a broken release can never leave the
//                    machine without protection. On success the backup is discarded.
// Everything is logged to {commonappdata}\StepWind\logs\update-install.log for diagnostics.

var
  PrevInstallExisted: Boolean;

function BackupDir: string;
begin
  Result := ExpandConstant('{commonappdata}\StepWind\update-backup');
end;

function InstallLogPath: string;
begin
  Result := ExpandConstant('{commonappdata}\StepWind\logs\update-install.log');
end;

procedure LogInstall(Msg: string);
var
  Existing: AnsiString;
begin
  ForceDirectories(ExpandConstant('{commonappdata}\StepWind\logs'));
  if LoadStringFromFile(InstallLogPath, Existing) then
    SaveStringToFile(InstallLogPath, Existing + Msg + #13#10, False)
  else
    SaveStringToFile(InstallLogPath, Msg + #13#10, False);
end;

// Mirror one directory tree onto another with robocopy (exit codes 0..7 are success).
procedure MirrorTree(FromDir, ToDir: string);
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{cmd}'),
    '/c robocopy "' + FromDir + '" "' + ToDir + '" /MIR /NFL /NDL /NJH /NJS /NP /R:1 /W:1',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

// Poll "sc query StepWind" until it reports the wanted state (RUNNING or STOPPED), or time out.
function WaitForServiceState(Wanted: string; MaxTries: Integer): Boolean;
var
  ResultCode, I: Integer;
  Output: AnsiString;
  TmpFile: string;
begin
  Result := False;
  TmpFile := ExpandConstant('{tmp}\sw_scq.txt');
  for I := 0 to MaxTries do
  begin
    Exec(ExpandConstant('{cmd}'), '/c sc query StepWind > "' + TmpFile + '" 2>&1', '',
      SW_HIDE, ewWaitUntilTerminated, ResultCode);
    if LoadStringFromFile(TmpFile, Output) then
    begin
      if Pos(Wanted, Output) > 0 then
      begin
        Result := True;
        Exit;
      end;
      // 1060 = service not installed; treat as "stopped" when that's what we're waiting for.
      if (Wanted = 'STOPPED') and (Pos('1060', Output) > 0) then
      begin
        Result := True;
        Exit;
      end;
    end;
    Sleep(500);
  end;
end;

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
  ResultCode: Integer;
begin
  // Disarm failure actions for the duration of the copy (reset delay 0, no restart commands).
  Exec(ExpandConstant('{sys}\sc.exe'), 'failure StepWind reset= 0 actions= ///', '',
    SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop StepWind', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  WaitForServiceState('STOPPED', 40);

  // The unelevated tray GUI has no failure action, so a forced kill is safe and frees StepWind.exe.
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM StepWind.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(400);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssInstall then
  begin
    // Is a previous install present? (Decides whether there's anything to back up / roll back to.)
    PrevInstallExisted := FileExists(ExpandConstant('{app}\StepWind.Service.exe'));
    StopServiceAndWait;
    if PrevInstallExisted then
    begin
      LogInstall('[' + GetDateTimeString('yyyy-mm-dd hh:nn:ss', '-', ':') + '] upgrade: backing up current install before swap');
      MirrorTree(ExpandConstant('{app}'), BackupDir);
    end;
  end;

  if CurStep = ssPostInstall then
  begin
    // Health-gate the freshly installed service. [Run]'s install-service verb already tried to
    // start it; give it a moment to reach RUNNING.
    if WaitForServiceState('RUNNING', 30) then
    begin
      LogInstall('[' + GetDateTimeString('yyyy-mm-dd hh:nn:ss', '-', ':') + '] post-install: service RUNNING; update healthy');
      if DirExists(BackupDir) then
        DelTree(BackupDir, True, True, True); // discard the backup — the new build is healthy
    end
    else if PrevInstallExisted and DirExists(BackupDir) then
    begin
      // The new build won't start. ROLL BACK to the backed-up install so protection survives.
      LogInstall('[' + GetDateTimeString('yyyy-mm-dd hh:nn:ss', '-', ':') + '] post-install: service did NOT reach RUNNING; ROLLING BACK to previous install');
      Exec(ExpandConstant('{sys}\sc.exe'), 'stop StepWind', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      WaitForServiceState('STOPPED', 40);
      Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM StepWind.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      Sleep(400);
      MirrorTree(BackupDir, ExpandConstant('{app}'));
      // Re-register + start the restored service (its verb does stop/delete/create/start).
      Exec(ExpandConstant('{app}\StepWind.Service.exe'), 'install-service', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      if WaitForServiceState('RUNNING', 30) then
        LogInstall('[' + GetDateTimeString('yyyy-mm-dd hh:nn:ss', '-', ':') + '] rollback complete: previous install RUNNING again')
      else
        LogInstall('[' + GetDateTimeString('yyyy-mm-dd hh:nn:ss', '-', ':') + '] rollback attempted but service still not RUNNING -- manual repair may be needed');
    end
    else
    begin
      LogInstall('[' + GetDateTimeString('yyyy-mm-dd hh:nn:ss', '-', ':') + '] post-install: service not RUNNING and no backup to roll back to (fresh install) -- service verb will keep retrying');
    end;
  end;
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
; Staged update installers + rollback backup are useless once StepWind is gone.
Type: filesandordirs; Name: "{commonappdata}\StepWind\updates"
Type: filesandordirs; Name: "{commonappdata}\StepWind\update-backup"
