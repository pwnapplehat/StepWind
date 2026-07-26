; StepWind Inno Setup script.
; Build with:  iscc installer\stepwind.iss
; Expects published binaries in dist\win-x64 (run build\publish.ps1 first).
;
; UI harness:  iscc /DPreviewUI installer\stepwind.iss
; builds this exact wizard with no payload, no elevation, no service and no registry writes, so
; the look can be reviewed (or screenshotted in CI) on any machine without installing anything.
; Every difference it makes is behind an #ifdef PreviewUI below.
;
; The wizard wears StepWind's own dark identity (see the "Look and feel" block below) rather
; than the stock grey Inno wizard. That is done entirely with supported 6.7 directives and the
; artwork in installer\brand — NOT by repainting controls from [Code], which a custom style
; overrides anyway. The install ENGINE (service stop, backup, health gate, rollback) is
; unchanged and lives at the bottom of this file.

; Dark mode + WizardBackImageFile + WizardBackColor are 6.6/6.7 features. Building with an
; older compiler would silently produce the stock grey wizard, so fail loudly instead.
#if VER < EncodeVer(6,7,0)
  #error StepWind's installer requires Inno Setup 6.7 or newer (dark mode and custom wizard backgrounds).
#endif

#define AppName "StepWind"
#define AppVersion "1.0.2"
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
UsePreviousAppDir=yes
DefaultGroupName={#AppName}
OutputDir=Output
SetupIconFile=..\assets\icon.ico
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed={#ArchAllowed}
ArchitecturesInstallIn64BitMode={#ArchAllowed}
#ifdef PreviewUI
DefaultDirName={localappdata}\StepWind-UI-Preview
OutputBaseFilename=StepWind-ui-preview
PrivilegesRequired=lowest
#else
DefaultDirName={autopf}\{#AppName}
OutputBaseFilename=StepWind-{#AppVersion}{#SetupSuffix}
PrivilegesRequired=admin
#endif
UninstallDisplayIcon={app}\StepWind.exe
; Let Restart Manager close + reopen the tray GUI around the file swap so its locked
; StepWind.exe can be replaced. Deliberately NO AppMutex: with /SUPPRESSMSGBOXES the AppMutex
; "app is running" prompt auto-answers Cancel and ABORTS the whole (silent auto-)update before
; any file is copied — the exact bug that left the service binary stale. The service itself is
; stopped in code (CurStepChanged, ssInstall) before the copy.
CloseApplications=yes
RestartApplications=yes

; ── Look and feel: StepWind's identity, not the stock wizard ──────────────────────────────────
; dark             force the dark appearance regardless of the user's Windows theme, because
;                  the app itself is dark-only — a light installer then a dark app is jarring.
; hidebevels       the divider lines look like seams on top of the background art.
; includetitlebar  style the title bar and border too, so the whole window is one surface
;                  (the app's own window has custom chrome for the same reason).
; Colors are the app's design tokens (#0A0C10 canvas, indigo -> cyan accents).
WizardStyle=modern dark hidebevels includetitlebar
WizardSizePercent=130
WizardBackColor=#0a0c10
WizardBackImageFile=brand\backdrop.png
WizardImageFile=brand\panel.png
WizardSmallImageFile=brand\logo.png

; ── Flow: one screen, then it installs ───────────────────────────────────────────────────────
; The stock flow asked four questions (license, folder, tasks, confirm) to install a service
; that has exactly one sensible configuration. What's left is: a branded screen with the one
; real choice on it (desktop shortcut) -> progress -> done.
;
; No LicenseFile: MIT grants its rights without acceptance, so a click-through gate would be
; theatre. LICENSE still ships in {app} (see [Files]), which is what MIT actually requires.
;
; DisableDirPage also closes a privilege hole: this is a SYSTEM service, and a service binary
; in a folder standard users can write to is a local privilege-escalation path. {autopf} is
; ACL'd correctly by Windows. Admins can still redirect with /DIR=, and CurStepChanged locks
; the ACLs down when they do.
DisableWelcomePage=no
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
; Kept as a real task (not just a checkbox) so scripted installs can still pass /TASKS=desktopicon.
; Its wizard page is skipped — the checkbox lives on the welcome screen instead.
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
#ifdef PreviewUI
; UI harness: one small file so there is something to copy and a progress bar to look at.
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
#else
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
#endif
; Progress-bar strips: compiled in but never installed (dontcopy), extracted to {tmp} at runtime
; and stretched over the native progress bar. See the progress-bar block in [Code]. Outside the
; #ifdef deliberately — the harness has to render the same bar the real installer does.
Source: "brand\bar-track.png"; Flags: dontcopy
Source: "brand\bar-fill.png"; Flags: dontcopy

#ifdef PreviewUI
[Dirs]
; Stand-in for a real history store, so the harness's uninstaller exercises the keep/delete
; prompt (IsStepWindStore looks for a blobs\ folder) against throwaway data.
Name: "{app}\sandbox-programdata\store\blobs"
#endif

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\StepWind.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\StepWind.exe"; Tasks: desktopicon

#ifndef PreviewUI
; Start the tray GUI for all users at logon (unelevated; the service does the privileged work).
[Registry]
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
  ValueName: "StepWind"; ValueData: """{app}\StepWind.exe"" --minimized"; Flags: uninsdeletevalue
#endif

[Messages]
; StepWind's own voice. The stock copy ("Welcome to the StepWind Setup Wizard... It is
; recommended that you close all other applications") tells the user nothing about what they
; are about to get.
SetupWindowTitle=%1 Setup
WelcomeLabel1=Install StepWind
WelcomeLabel2=StepWind keeps a version history of the folders you choose, and records moves, renames and deletes across your whole PC — so an accidental change can be undone instead of lost.
; Inno appends this to the welcome text. The default ("Click Next to continue") contradicts the
; button, which is now Install because the Ready page is gone.
ClickNext=Setup installs a background protection service and the tray app, then starts protecting straight away.
WizardInstalling=Setting up protection
InstallingLabel=Installing the protection service and starting it. This takes a few seconds.
FinishedHeadingLabel=StepWind is protecting your PC
FinishedLabel=Protection is running now. StepWind sits in your system tray — open it to choose which folders keep version history.
FinishedLabelNoIcons=Protection is running now. StepWind sits in your system tray — open it to choose which folders keep version history.

#ifdef PreviewUI
[Run]
; UI harness only: the harness payload copies in a few milliseconds, so the progress page would
; flash past before it could be reviewed. This holds it on screen long enough to look at, with a
; status message of roughly the length the real service-install step produces.
Filename: "{cmd}"; Parameters: "/c timeout /t 4 /nobreak"; Flags: runhidden waituntilterminated; StatusMsg: "Installing StepWind protection service..."
#else
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
#endif

[UninstallDelete]
; The user's version history under %ProgramData%\StepWind\store is deliberately NOT listed
; here: whether it survives is the user's choice, asked in InitializeUninstall and carried out
; in CurUninstallStepChanged. Everything below is StepWind's own scaffolding, which is useless
; once StepWind is gone.
Type: filesandordirs; Name: "{app}\logs"
Type: filesandordirs; Name: "{commonappdata}\StepWind\bin"
Type: filesandordirs; Name: "{commonappdata}\StepWind\updates"
Type: filesandordirs; Name: "{commonappdata}\StepWind\update-backup"

[Code]
// ── Welcome screen extras ───────────────────────────────────────────────────────────────────
// The desktop-shortcut choice and the fine print live on the welcome page so the whole wizard
// is one screen. Controls are created here and positioned when the page is first shown, by
// which point the form has its final DPI-scaled layout.
var
  DesktopIconCheck: TNewCheckBox;
  Benefits: TNewStaticText;
  FinePrint: TNewStaticText;
  BarTrack: TBitmapImage;
  BarFill: TBitmapImage;

  PrevInstallExisted: Boolean;
  RemoveHistoryOnUninstall: Boolean;

// ── Brand-coloured progress bar ─────────────────────────────────────────────────────────────
// TNewProgressBar has no colour property, and the active custom style paints it the stock lime
// green — which looks like a different product against this dark canvas. So the native bar is
// hidden and two stretched images stand in for it: a dark track and an indigo -> cyan fill whose
// width follows the real progress. Images are used rather than coloured panels because a VCL
// style overrides control colours set from Pascal, but it cannot restyle a bitmap.
procedure CreateProgressBar;
begin
  ExtractTemporaryFile('bar-track.png');
  ExtractTemporaryFile('bar-fill.png');

  BarTrack := TBitmapImage.Create(WizardForm);
  BarTrack.Parent := WizardForm.InstallingPage;
  BarTrack.Stretch := True;
  BarTrack.PngImage.LoadFromFile(ExpandConstant('{tmp}\bar-track.png'));
  BarTrack.Visible := False;

  BarFill := TBitmapImage.Create(WizardForm);
  BarFill.Parent := WizardForm.InstallingPage;
  BarFill.Stretch := True;
  BarFill.PngImage.LoadFromFile(ExpandConstant('{tmp}\bar-fill.png'));
  BarFill.Visible := False;
end;

// Lay the stand-in bar exactly over the native one and hide the original.
procedure ShowProgressBar;
begin
  WizardForm.ProgressGauge.Visible := False;

  BarTrack.Left := WizardForm.ProgressGauge.Left;
  BarTrack.Top := WizardForm.ProgressGauge.Top;
  BarTrack.Width := WizardForm.ProgressGauge.Width;
  BarTrack.Height := WizardForm.ProgressGauge.Height;
  BarTrack.Visible := True;

  BarFill.Left := BarTrack.Left;
  BarFill.Top := BarTrack.Top;
  BarFill.Height := BarTrack.Height;
  BarFill.Width := 0;
  BarFill.Visible := True;
end;

procedure CurInstallProgressChanged(CurProgress, MaxProgress: Integer);
begin
  if (BarFill <> nil) and BarFill.Visible and (MaxProgress > 0) then
    BarFill.Width := (BarTrack.Width * CurProgress) div MaxProgress;
end;

procedure InitializeWizard;
begin
  CreateProgressBar;
  Benefits := TNewStaticText.Create(WizardForm);
  Benefits.Parent := WizardForm.WelcomePage;
  Benefits.WordWrap := False;
  Benefits.Caption :=
    '•   Undo an accidental move, rename or delete' + #13#10 +
    '•   Roll a file back to how it was at an earlier save' + #13#10 +
    '•   Works offline, on this PC only';

  DesktopIconCheck := TNewCheckBox.Create(WizardForm);
  DesktopIconCheck.Parent := WizardForm.WelcomePage;
  DesktopIconCheck.Caption := 'Create a desktop shortcut';

  FinePrint := TNewStaticText.Create(WizardForm);
  FinePrint.Parent := WizardForm.WelcomePage;
  FinePrint.WordWrap := True;
end;

procedure CurPageChanged(CurPageID: Integer);
var
  BenefitsTop, BenefitsMaxTop: Integer;
begin
  if CurPageID = wpWelcome then
  begin
    // Everything below the hero copy is anchored to the BOTTOM of the page. The copy above
    // varies in height with DPI, font scaling and translation, so measuring downward from it
    // eventually collides with it — and anchoring the fine print upward from the bottom is what
    // stops the last line being clipped off the page edge.
    Benefits.Left := WizardForm.WelcomeLabel2.Left;
    Benefits.Width := WizardForm.WelcomeLabel2.Width;
    // Sit directly under the intro copy, which means measuring it: AutoSize collapses the label
    // to its wrapped text height (Inno leaves it stretched down the page otherwise). Clamped so
    // that if the copy ever grows — a longer translation, a larger system font — the list stops
    // above the checkbox instead of overlapping it.
    WizardForm.WelcomeLabel2.AutoSize := True;
    BenefitsTop := WizardForm.WelcomeLabel2.Top + WizardForm.WelcomeLabel2.Height + ScaleY(20);
    BenefitsMaxTop := WizardForm.WelcomePage.ClientHeight - ScaleY(152);
    if BenefitsTop > BenefitsMaxTop then
      BenefitsTop := BenefitsMaxTop;
    Benefits.Top := BenefitsTop;

    DesktopIconCheck.Left := WizardForm.WelcomeLabel2.Left;
    DesktopIconCheck.Width := WizardForm.WelcomeLabel2.Width;
    DesktopIconCheck.Height := ScaleY(20);
    DesktopIconCheck.Top := WizardForm.WelcomePage.ClientHeight - ScaleY(76);
    // Honor /TASKS= from the command line instead of overwriting it.
    DesktopIconCheck.Checked := WizardIsTaskSelected('desktopicon');

    FinePrint.Left := WizardForm.WelcomeLabel2.Left;
    FinePrint.Width := WizardForm.WelcomeLabel2.Width;
    FinePrint.Height := ScaleY(46);
    FinePrint.Top := WizardForm.WelcomePage.ClientHeight - ScaleY(48);
    // WizardDirValue, NOT ExpandConstant('{app}'): {app} is not initialized while the welcome
    // page is showing, and expanding it there is a hard runtime error.
    FinePrint.Caption :=
      'Free and open source (MIT). No account, no cloud, no telemetry.' + #13#10 +
      'Installs to ' + WizardDirValue;

    // There is no Ready page to press Install on any more, so this button is the Install button.
    WizardForm.NextButton.Caption := SetupMessage(msgButtonInstall);
  end;

  if CurPageID = wpInstalling then
    ShowProgressBar;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  // The only task has a checkbox on the welcome page; its own page would be a second click
  // asking the same question.
  Result := PageID = wpSelectTasks;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = wpWelcome then
  begin
    if DesktopIconCheck.Checked then
      WizardSelectTasks('desktopicon')
    else
      WizardSelectTasks('!desktopicon');
  end;
end;

// ── Where the user's history lives ──────────────────────────────────────────────────────────
// Under the UI harness both of these point INSIDE the throwaway install dir. That is not
// cosmetic: without it, running the harness's uninstaller would offer to delete the real
// machine's real version history.
function StepWindDataDir: string;
begin
#ifdef PreviewUI
  Result := ExpandConstant('{app}\sandbox-programdata');
#else
  Result := ExpandConstant('{commonappdata}\StepWind');
#endif
end;

function InstallLogPath: string;
begin
  Result := StepWindDataDir + '\logs\update-install.log';
end;

procedure LogInstall(Msg: string);
var
  Existing: AnsiString;
begin
  ForceDirectories(StepWindDataDir + '\logs');
  if LoadStringFromFile(InstallLogPath, Existing) then
    SaveStringToFile(InstallLogPath, Existing + Msg + #13#10, False)
  else
    SaveStringToFile(InstallLogPath, Msg + #13#10, False);
end;

function Stamp: string;
begin
  Result := '[' + GetDateTimeString('yyyy-mm-dd hh:nn:ss', '-', ':') + '] ';
end;

// The store can have been moved off %ProgramData% (Settings -> move history store), so read
// its real location out of settings.json rather than assuming the default. A hand-rolled scan
// is enough for one string value, and every failure path falls back to the default location.
function ReadStoreRoot: string;
var
  Text: AnsiString;
  Value: string;
  I, Start: Integer;
begin
  Result := StepWindDataDir + '\store';
#ifdef PreviewUI
  Exit;  // sandboxed store (see StepWindDataDir); never read the real settings.json
#endif
  if not LoadStringFromFile(StepWindDataDir + '\settings.json', Text) then
    Exit;
  I := Pos('"StoreRoot"', Text);
  if I = 0 then
    Exit;
  I := I + Length('"StoreRoot"');
  while (I <= Length(Text)) and (Text[I] <> '"') do   // step over ':' and whitespace
    I := I + 1;
  if I > Length(Text) then
    Exit;
  Start := I + 1;
  I := Start;
  while (I <= Length(Text)) and (Text[I] <> '"') do
  begin
    if Text[I] = '\' then
      I := I + 1;                                     // \" must not be read as the end quote
    I := I + 1;
  end;
  Value := Copy(Text, Start, I - Start);
  StringChangeEx(Value, '\\', '\', True);             // JSON-escaped separators
  if Value <> '' then
    Result := Value;
end;

// Only ever treat a folder as deletable history if it actually looks like a StepWind store.
// This is a delete of the user's documents-in-miniature driven by a parsed path, so it gets a
// positive identification, not just a "the folder exists" check.
function IsStepWindStore(Path: string): Boolean;
begin
  Result := (Length(Path) > 3) and DirExists(Path)
        and (DirExists(AddBackslash(Path) + 'blobs') or FileExists(AddBackslash(Path) + 'versions.jsonl'));
end;

// ── Install-dir hardening for non-default locations ─────────────────────────────────────────
// {autopf} already denies write access to standard users. A /DIR= target might not, and a
// SYSTEM service whose binary a standard user can overwrite is a privilege-escalation path,
// so give a custom location the same shape of ACL: SYSTEM + Administrators full, everyone
// else read and execute.
procedure HardenAppDirIfOutsideProgramFiles;
var
  App, Pf, Pf32: string;
  ResultCode: Integer;
begin
  App := Lowercase(ExpandConstant('{app}'));
  Pf := Lowercase(ExpandConstant('{autopf}'));
  Pf32 := Lowercase(ExpandConstant('{commonpf32}'));
  if (Pos(Pf, App) = 1) or (Pos(Pf32, App) = 1) then
    Exit;

  if Exec(ExpandConstant('{sys}\icacls.exe'),
          '"' + ExpandConstant('{app}') + '" /inheritance:r' +
          ' /grant:r "*S-1-5-18:(OI)(CI)F"' +      // NT AUTHORITY\SYSTEM
          ' /grant:r "*S-1-5-32-544:(OI)(CI)F"' +  // BUILTIN\Administrators
          ' /grant:r "*S-1-5-11:(OI)(CI)RX"' +     // NT AUTHORITY\Authenticated Users
          ' /T /C /Q',
          '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0) then
    LogInstall(Stamp + 'install dir is outside Program Files; ACLs hardened (SYSTEM/Admins full, users read+execute)')
  else
    LogInstall(Stamp + 'install dir is outside Program Files and ACL hardening FAILED (icacls exit ' + IntToStr(ResultCode) + ') -- a standard user may be able to replace the service binary');
end;

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

function BackupDir: string;
begin
  Result := StepWindDataDir + '\update-backup';
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
#ifdef PreviewUI
  Exit;  // UI harness: no service to stop, nothing to back up, nothing to health-gate
#endif
  if CurStep = ssInstall then
  begin
    // Is a previous install present? (Decides whether there's anything to back up / roll back to.)
    PrevInstallExisted := FileExists(ExpandConstant('{app}\StepWind.Service.exe'));
    StopServiceAndWait;
    if PrevInstallExisted then
    begin
      LogInstall(Stamp + 'upgrade: backing up current install before swap');
      MirrorTree(ExpandConstant('{app}'), BackupDir);
    end;
  end;

  if CurStep = ssPostInstall then
  begin
    HardenAppDirIfOutsideProgramFiles;

    // Health-gate the freshly installed service. [Run]'s install-service verb already tried to
    // start it; give it a moment to reach RUNNING.
    if WaitForServiceState('RUNNING', 30) then
    begin
      LogInstall(Stamp + 'post-install: service RUNNING; update healthy');
      if DirExists(BackupDir) then
        DelTree(BackupDir, True, True, True); // discard the backup — the new build is healthy
    end
    else if PrevInstallExisted and DirExists(BackupDir) then
    begin
      // The new build won't start. ROLL BACK to the backed-up install so protection survives.
      LogInstall(Stamp + 'post-install: service did NOT reach RUNNING; ROLLING BACK to previous install');
      Exec(ExpandConstant('{sys}\sc.exe'), 'stop StepWind', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      WaitForServiceState('STOPPED', 40);
      Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM StepWind.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      Sleep(400);
      MirrorTree(BackupDir, ExpandConstant('{app}'));
      // Re-register + start the restored service (its verb does stop/delete/create/start).
      Exec(ExpandConstant('{app}\StepWind.Service.exe'), 'install-service', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      if WaitForServiceState('RUNNING', 30) then
        LogInstall(Stamp + 'rollback complete: previous install RUNNING again')
      else
        LogInstall(Stamp + 'rollback attempted but service still not RUNNING -- manual repair may be needed');
    end
    else
    begin
      LogInstall(Stamp + 'post-install: service not RUNNING and no backup to roll back to (fresh install) -- service verb will keep retrying');
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

// ── Uninstall: the history is the user's, so ask ─────────────────────────────────────────────
// Saved versions outlive the app by design (uninstall/reinstall must never destroy history),
// but silently leaving gigabytes of a user's files behind without saying so is not honest
// either. So: ask once, up front, before anything is removed.
//
// A SUPPRESSIBLE dialog on purpose — a scripted or silent uninstall (/VERYSILENT, enterprise
// removal) gets the default, and the default is KEEP. Losing history must always take an
// explicit human "yes".
function InitializeUninstall: Boolean;
var
  Store: string;
  Choices: TArrayOfString;
begin
  Result := True;
  RemoveHistoryOnUninstall := False;

  Store := ReadStoreRoot;
  if not IsStepWindStore(Store) then
    Exit;  // no history on this machine: nothing to decide

  // Built as a variable rather than an inline array literal: Inno's parser reads any line whose
  // first character is '[' as a section tag, even inside [Code].
  SetArrayLength(Choices, 2);
  Choices[0] := '&Keep my file history' + #13#10 +
    'Leave it on this PC. Reinstalling StepWind picks it back up, and the files stay restorable.';
  Choices[1] := '&Delete my file history' + #13#10 +
    'Permanently erase every saved version. This cannot be undone.';

  RemoveHistoryOnUninstall :=
    SuppressibleTaskDialogMsgBox(
      'What should happen to your file history?',
      'StepWind''s saved versions of your files are stored in' + #13#10 + Store,
      mbConfirmation, MB_YESNO, Choices, 0, IDYES) = IDNO;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Store: string;
begin
  // usPostUninstall: the service is stopped and deregistered by now, so nothing is holding the
  // store open or writing new versions into it.
  if (CurUninstallStep = usPostUninstall) and RemoveHistoryOnUninstall then
  begin
    Store := ReadStoreRoot;
    if IsStepWindStore(Store) then
      DelTree(Store, True, True, True);
    // Settings, the DPAPI-sealed store key and the logs are meaningless without the store.
    DelTree(StepWindDataDir, True, True, True);
  end;
end;
