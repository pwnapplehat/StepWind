# StepWind for enterprise

StepWind is a per-machine, 100%-local file-protection agent (an elevated LocalSystem service plus
an unelevated tray app). This guide covers fleet deployment, central policy, and the security
audit trail. Everything here is local and offline — StepWind never phones home.

## 1. Deployment (Intune / SCCM / GPO)

StepWind ships a signed[\*] Inno Setup installer, `StepWind-<version>-setup.exe` (x64) and
`StepWind-<version>-arm64-setup.exe` (ARM64). It installs the service (auto-start, LocalSystem),
adds the tray app to per-user startup, and supports fully silent, non-interactive install.

Silent switches:

```
StepWind-<version>-setup.exe /VERYSILENT /NORESTART /SUPPRESSMSGBOXES
```

- **Microsoft Intune:** wrap the EXE with the Microsoft Win32 Content Prep Tool (`IntuneWinAppUtil`)
  into a `.intunewin`, then:
  - Install command: `StepWind-<version>-setup.exe /VERYSILENT /NORESTART /SUPPRESSMSGBOXES`
  - Uninstall command: `"%ProgramFiles%\StepWind\unins000.exe" /VERYSILENT`
  - Detection rule: file `%ProgramFiles%\StepWind\StepWind.Service.exe` exists (or version ≥ target).
- **ConfigMgr (SCCM):** deploy the EXE as an application/package with the same install/uninstall
  command lines; detection on the service binary or the `StepWind` service.
- **GPO software installation** assigns `.msi` packages only. StepWind ships an EXE (see
  [§5](#5-why-an-exe-installer-not-an-msi)); assign it via a startup script or, preferably, Intune/SCCM.

The uninstaller stops and removes the service and **keeps the version-history store** by default
(`%ProgramData%\StepWind\store`) so an uninstall/reinstall never destroys history. Delete that
folder explicitly if you need a clean wipe.

[\*] Code-signing via the SignPath Foundation OSS program is being set up; until then the installer
is unsigned and SmartScreen will prompt. See `docs/signing/SignPath-application.md`.

## 2. Central policy (Group Policy / MDM)

The StepWind service reads machine policy from **`HKLM\SOFTWARE\Policies\StepWind`** at startup.
Any value present there is **enforced**: the service applies it and refuses to let any user — a
standard user *or* a local administrator — change it through the app. Only your Group Policy / MDM
(which owns that key) can. In a managed fleet this makes machine-wide settings centrally
controlled, and removes the shared-PC question entirely.

### Group Policy (ADMX)

Copy the template into your Central Store (or a single machine's `C:\Windows\PolicyDefinitions\`):

```
policy\StepWind.admx        ->  %SystemRoot%\PolicyDefinitions\StepWind.admx
policy\en-US\StepWind.adml  ->  %SystemRoot%\PolicyDefinitions\en-US\StepWind.adml
```

Settings then appear under **Computer Configuration → Administrative Templates → StepWind**.
"Not Configured" = the user controls it; Enabled/Disabled = enforced.

### Registry reference (for Intune Settings Catalog / OMA-URI or scripts)

All values live under `HKLM\SOFTWARE\Policies\StepWind`. Presence = enforced.

| Value | Type | Meaning |
|---|---|---|
| `EncryptionEnabled` | REG_DWORD 0/1 | Force store encryption on/off |
| `EncryptIndex` | REG_DWORD 0/1 | Force index (metadata) encryption on/off |
| `AutoUpdateEnabled` | REG_DWORD 0/1 | Force auto-update on/off (set 0 if you manage updates centrally) |
| `FlightRecorderEnabled` | REG_DWORD 0/1 | Force the whole-machine timeline on/off |
| `RespectGitIgnore` | REG_DWORD 0/1 | Force `.gitignore` honoring on/off |
| `AllowUserFolderChanges` | REG_DWORD 0/1 | 0 = only admins may change protected folders |
| `AuditEnabled` | REG_DWORD 0/1 | Write the security audit log (default on) |
| `RetentionKeepAllHours` | REG_DWORD | Keep every version within N hours |
| `RetentionHourlyDays` | REG_DWORD | Thin to hourly for N days |
| `RetentionDailyDays` | REG_DWORD | Thin to daily for N days |
| `RetentionMaxAgeDays` | REG_DWORD | Hard age cap (days) |
| `RetentionMaxVersionsPerFile` | REG_DWORD | Hard per-file version cap |
| `MinFreeDiskBytes` | REG_SZ or REG_QWORD | Pause capturing below this free space |
| `MaxStoreBytes` | REG_SZ or REG_QWORD | Cap the store's own size (0 = unlimited) |
| `MandatoryFolders` | REG_MULTI_SZ | Folders always protected; users cannot un-protect them |

Example (PowerShell, run elevated) — force encryption on, lock updates off, and mandate a folder:

```powershell
$k = 'HKLM:\SOFTWARE\Policies\StepWind'
New-Item -Path $k -Force | Out-Null
New-ItemProperty -Path $k -Name EncryptionEnabled  -Type DWord    -Value 1 -Force
New-ItemProperty -Path $k -Name AutoUpdateEnabled  -Type DWord    -Value 0 -Force
New-ItemProperty -Path $k -Name AllowUserFolderChanges -Type DWord -Value 0 -Force
New-ItemProperty -Path $k -Name MandatoryFolders   -Type MultiString -Value @('C:\Work','C:\Users\Public\Shared') -Force
# Apply: restart the StepWind service (or the machine).
Restart-Service StepWind
```

Policy is read when the service starts, so apply changes with `Restart-Service StepWind` (or on
the next reboot / GPO-driven service restart). Setting locks are also enforced live between
restarts, so a user can never change a managed setting even before the next policy read.

## 3. Security audit trail

When auditing is enabled (default), StepWind writes security-relevant actions to the Windows
**Application** event log under the dedicated source **`StepWind.Audit`** — a standard local,
append-only sink your SIEM or Windows Event Forwarding (WEF) subscription can collect by filtering
that provider. Nothing is ever sent anywhere by StepWind itself.

Each record names the acting user, the outcome (OK / DENIED-FAILED), and details. Stable Event IDs:

| Event ID | Action |
|---|---|
| 1000 / 1001 | Service started / stopped |
| 2000 | Settings changed (lists which) |
| 2001 | Settings change **denied by policy** |
| 2002 | Settings change **denied by authorization** (another user's data) |
| 3000 | History purged |
| 3001 | Version restored |
| 3002 | Operation reversed (undo) |
| 3003 / 3004 | Folder protected / un-protected |
| 3005 | Encryption toggled |
| 3006 | Store relocated |
| 4000 / 4001 | Update staged / launched |
| 5000 | Machine policy enforced at startup |

Collect via a WEF subscription filtered on provider `StepWind.Audit`, or point your EDR/agent at
it. Example queries:

```powershell
# All StepWind audit events (PowerShell):
Get-WinEvent -FilterHashtable @{ LogName='Application'; ProviderName='StepWind.Audit' } -MaxEvents 50

# Just purges and policy-denied attempts (wevtutil):
wevtutil qe Application /q:"*[System[Provider[@Name='StepWind.Audit'] and (EventID=3000 or EventID=2001)]]" /f:text /c:20
```

## 4. What the service can and cannot do

The trust model is documented in full in [`../SECURITY.md`](../SECURITY.md). In brief: the pipe is
local-only and DoS-bounded, every action is authorized against the connecting user, one user can't
touch another's history, updates are fail-closed (checksum + pinned signature), and the store is
ACL-locked to SYSTEM + Administrators.

## 5. Why an EXE installer, not an MSI

StepWind's Inno Setup installer does non-trivial, safety-critical work at upgrade time: it stops
the service, backs up the current install, health-checks the new service, and **rolls back
automatically** if the new build won't start. Re-implementing that orchestration correctly in an
MSI custom-action sequence would add risk we can't fully validate across every managed-deployment
path. Since Intune and ConfigMgr deploy EXE installers first-class (with silent switches and
detection rules, above), we ship the hardened EXE rather than an under-verified MSI. If your
environment strictly requires an MSI for GPO software-installation assignment, contact us
(contact@stepwind.app) — a thin MSI wrapper is on the roadmap once it can be verified to the same
standard.
