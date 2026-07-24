# StepWind security model

StepWind installs a background **service that runs as LocalSystem** (it needs SYSTEM to read the
NTFS change journal and ETW). Anything running at that privilege is worth being careful about, so
this document is precise about what the service will and won't do, who can ask it to do what, and
how updates are trusted.

If you find a security issue, please open a
[GitHub security advisory](https://github.com/pwnapplehat/StepWind/security/advisories/new) or
email **contact@stepwind.app** rather than filing a public issue.

## Components and privilege

| Component | Runs as | Talks to |
|---|---|---|
| `StepWind.Service` | LocalSystem | hosts the engine + the named pipe `StepWind.Service` |
| `StepWind.exe` (tray GUI) | the logged-in user, **unelevated** | the service, over the pipe |
| `StepWind.Mcp.exe` (MCP server) | whichever user's AI tool launches it | the service, over the pipe |
| `StepWind.Cli.exe` | whoever runs it | the service, over the pipe |

The GUI never has privileges of its own — it asks the service. The service authorizes each
request against the **connecting user's Windows identity** (resolved by impersonating the pipe),
not against anything in the request payload.

## Who may do what over the pipe

The pipe accepts **local** authenticated users only — the NETWORK group is explicitly denied, so
a remote SMB client (`\\host\pipe\StepWind.Service`) is rejected before any command runs — and it
is bounded against denial of service (a per-connection read timeout and a maximum request size,
so a stalled or flooding client can't pin the service's handler slots). Every command is then
authorized against the connecting user's impersonated Windows identity:

| Action | Allowed for |
|---|---|
| Read a file's history / version content / browse / diff | the owner of that protected root, an administrator, or a user whose own token can read the folder |
| See a timeline operation (and get its undo handle) | a caller who can reach either endpoint of it; others never see it |
| Reverse a move/rename | a caller who can access the **restore destination** (where the file is moved back to) — stricter than merely seeing it, so no one can drive a SYSTEM move into a folder they have no rights to |
| Restore a version | the root's owner or an administrator; the restore destination is never caller-controlled for unprivileged callers |
| Add a protected folder | any user, but only a folder **their own token can read** (so the SYSTEM service can't be used to capture files they couldn't read); the adder becomes the owner |
| Stop protecting / purge one root's history | that root's owner or an administrator |
| Purge **all** history / "unprotected" history | administrator only |
| Change machine-wide settings (encryption, index encryption, auto-update, flight recorder, `.gitignore` policy, storage limits, retention) | the sole owner of history here, or an **administrator** once any *other* user also owns history |

The unelevated GUI/CLI/MCP also verify, after connecting, that the pipe is **owned by a
privileged identity** (SYSTEM/Administrators) before trusting it — so a standard-user process
can't squat the pipe name and feed you a forged "you're protected" view.

**Machine-wide settings on shared PCs:** as long as you are the only real user who owns protected
history — the common case — you change machine-wide settings from the unelevated app with no
friction, because it is your data alone. The moment *another* user owns history on the same PC,
those settings require an administrator (so no bystander account can, say, switch encryption off
or set destructive retention on someone else's data). An admin applies them from an elevated
terminal: `stepwind-cli set-settings <json>`. Per-user concerns — folders, exclusions, what the
timeline shows — always stay under the ownership rules above.

**Root ownership** is recorded when a folder is added, backfilled from the folder's on-disk NTFS
owner when no owner is on record (e.g. after a settings reset), and always has a live fallback:
if your own token can read the folder, you may see its history (that is never an escalation).

## Operation-undo handles are not forgeable

A timeline "undo" carries an **opaque handle** into the recorder's in-memory ring, not the
operation's paths. The service looks the handle up in its own ring and re-derives the real paths;
a handle it didn't issue matches nothing. A client therefore cannot craft "move *this* to *there*
as SYSTEM."

## The store

Version history lives under `%ProgramData%\StepWind\store`, ACL-locked to **SYSTEM +
Administrators** (standard users can't read it directly — they go through the authorized pipe).

Each protected folder stores its history under a **stable namespace**: normally the folder's own
name, or a deterministic `name~hash` id when another protected folder already uses that name — so
two folders that are both called "Documents" keep fully separate histories and owner sets.
Removing a folder keeps its namespace reserved (history stays restorable), an unrelated
same-named folder can never silently adopt it, and re-protecting the same path re-attaches its
old history.
Optional encryption at rest is AES-256-GCM with a key sealed by machine-scope DPAPI. **Encryption
protects blob *content* at rest** (a stolen/offline drive can't be read elsewhere). By default the
*index* (file names, paths, dates) is plaintext; enabling **"Also encrypt the index"** (Settings →
Protection) encrypts each index line with the same AES-256-GCM key, so an offline drive reveals no
metadata either. Encryption is **not** a cross-user privacy boundary on the same machine — that is
enforced by the authorization above — and machine-scope DPAPI is decryptable by any local admin.

**Recovery key.** Because the live key is machine-DPAPI-sealed, an OS reinstall or moving the disk
to another machine would otherwise orphan encrypted history forever. Export a passphrase-protected
recovery key (elevated: `stepwind-cli export-recovery-key <passphrase> <file>`), keep it off the
machine, and on a new machine `stepwind-cli recover-verify <file> <passphrase>` confirms it unlocks
the store. The recovery file wraps the key with PBKDF2-SHA256 (600k iterations) + AES-256-GCM, so
it's useless without the passphrase — but anyone with **both** the file and the passphrase can read
the store, so treat it like the master secret it is.

## Updates are fail-closed

The service downloads a candidate into an **ACL-locked staging folder (hardened before the
download begins)**, and installs it **silently only** if all of:

1. the release publishes `SHA256SUMS.txt` (absent ⇒ refused — no "assume good"), and
2. the downloaded setup's SHA-256 matches the checksum **for its filename**, and
3. the setup carries a **trusted Authenticode signature** (verified with `WinVerifyTrust`) that
   matches StepWind's **pinned certificate thumbprint**.

A checksum published in the same release an attacker would tamper with proves integrity, not
authenticity — so a pinned code signature is the real root of trust. Requirement 3 is fail-closed
on the pin: **while no thumbprint is pinned (as today, before code-signing is set up), NO release
is ever installed silently** — a trusted signature alone is not enough, because an attacker who
replaced the release assets could sign with any certificate that chains to a trusted CA.

- **Pinned-and-signed release** → installed silently (the service is already elevated). The exact
  staged file is re-verified (checksum + signature) immediately before launch, so nothing that
  changed after the first check is ever run.
- **Everything else** (unsigned, or signed but not the pinned cert) → the verified download stays
  in the ACL-locked staging folder and is offered in the app as a **one-click install**; you
  approve the normal UAC prompt. StepWind never runs such an installer silently as SYSTEM.

The installer itself is the rollback actor: it backs up the current install before swapping files
and restores it if the new service doesn't start, so an update can't leave you unprotected. If a
staged release is later withdrawn, the service clears the stale download instead of offering a
ghost update.

### Verifying a download yourself

Every release ships `SHA256SUMS.txt`. On the downloaded file:

```powershell
(Get-FileHash .\StepWind-x.y.z-setup.exe -Algorithm SHA256).Hash
```

Compare it to the matching line in `SHA256SUMS.txt`.

## Code signing

StepWind is not code-signed yet. Signing is set up to go via the **SignPath Foundation** free
open-source program (OV certificate, key on their HSM, signature applied in CI only after they
verify the binary was built from this public repo) — the step-by-step application and the
already-wired CI switch are in
[docs/signing/SignPath-application.md](docs/signing/SignPath-application.md). Until it's enabled,
Windows SmartScreen will show "Windows protected your PC" for the installer — expected for a new,
unsigned app, not a malware detection. Choose **More info → Run anyway**, and verify the SHA-256
as above.

## Enterprise / managed environments

On managed fleets an administrator can centrally control StepWind via **Group Policy / MDM**
(`HKLM\SOFTWARE\Policies\StepWind`, writable only by the org's policy engine). Any value set there
is **enforced**: the service applies it and refuses to let *any* caller — standard user or local
administrator — change it through the app, because only the org's policy may. This makes
machine-wide settings (encryption, updates, retention, storage limits, the flight recorder) and
the protected-folder set centrally governed, and removes the shared-PC question entirely in a
managed environment. An ADMX/ADML template ships in `enterprise/policy/`.

Every security-relevant action (settings changes, purges, restores, reversals, folder changes,
encryption toggles, updates, and policy-denied attempts) is written — naming the acting user — to
a dedicated **`StepWind` Windows Event Log**, the standard local, SIEM-forwardable audit sink
(Windows Event Forwarding or any EDR agent can collect it; StepWind itself never sends anything).
Full deployment, policy, and audit reference: [`enterprise/README.md`](enterprise/README.md).

## Known limitations (honest)

- **Metadata encryption is opt-in.** File paths, names, and timestamps in the version index are
  plaintext by default even when blob content is encrypted; enable "Also encrypt the index" to
  cover them too (see "The store").
- **The whole-machine timeline needs a change journal.** Drives without one (exFAT, network
  shares, some removable media) aren't on the flight-recorder timeline — the app's coverage panel
  shows exactly which drives are recorded. Folder version history works on any filesystem
  regardless.
- **Local machine only.** StepWind protects the Windows machine it runs on; files inside WSL
  distributions or on remote machines aren't covered.
