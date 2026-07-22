# StepWind security model

StepWind installs a background **service that runs as LocalSystem** (it needs SYSTEM to read the
NTFS change journal and ETW). Anything running at that privilege is worth being careful about, so
this document is precise about what the service will and won't do, who can ask it to do what, and
how updates are trusted.

If you find a security issue, please open a
[GitHub security advisory](https://github.com/pwnapplehat/StepWind/security/advisories/new) rather
than a public issue.

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

The pipe is reachable by authenticated local users, but the service authorizes every command:

| Action | Allowed for |
|---|---|
| Read a file's history / version content / browse / diff | the owner of that protected root, an administrator, or a user whose own token can read the folder |
| See a timeline operation (and get its undo handle) | a caller who can reach that file; others never see it |
| Reverse a move/rename | same as seeing it — you can't undo an operation you can't see |
| Restore a version | the root's owner or an administrator; the restore destination is never caller-controlled for unprivileged callers |
| Add a protected folder | any user, but only a folder **their own token can read** (so the SYSTEM service can't be used to capture files they couldn't read); the adder becomes the owner |
| Stop protecting / purge one root's history | that root's owner or an administrator |
| Purge **all** history / "unprotected" history | administrator only |
| Change encryption / auto-update / retention settings | the connecting user (these are machine config; see "Known limitations") |

**Root ownership** is recorded when a folder is added, backfilled from the folder's on-disk owner
for pre-existing installs, and always has a live fallback: if your own token can read the folder,
you may see its history (that is never an escalation).

## Operation-undo handles are not forgeable

A timeline "undo" carries an **opaque handle** into the recorder's in-memory ring, not the
operation's paths. The service looks the handle up in its own ring and re-derives the real paths;
a handle it didn't issue matches nothing. A client therefore cannot craft "move *this* to *there*
as SYSTEM."

## The store

Version history lives under `%ProgramData%\StepWind\store`, ACL-locked to **SYSTEM +
Administrators** (standard users can't read it directly — they go through the authorized pipe).
Optional encryption at rest is AES-256-GCM with a key sealed by machine-scope DPAPI. **Encryption
protects blob *content* at rest** (a stolen/offline drive can't be read elsewhere); it does **not**
hide the index of names/dates from an administrator on the same machine, and it is not a
cross-user privacy boundary — that is enforced by the authorization above.

**Recovery key.** Because the live key is machine-DPAPI-sealed, an OS reinstall or moving the disk
to another machine would otherwise orphan encrypted history forever. Export a passphrase-protected
recovery key (elevated: `stepwind-cli export-recovery-key <passphrase> <file>`), keep it off the
machine, and on a new machine `stepwind-cli recover-verify <file> <passphrase>` confirms it unlocks
the store. The recovery file wraps the key with PBKDF2-SHA256 (600k iterations) + AES-256-GCM, so
it's useless without the passphrase — but anyone with **both** the file and the passphrase can read
the store, so treat it like the master secret it is.

## Updates are fail-closed

The service checks GitHub for new releases and applies one **only** if:

1. the release publishes `SHA256SUMS.txt` (absent ⇒ refused — no "assume good"), and
2. the downloaded setup's SHA-256 matches the checksum **for its filename**, and
3. the setup carries a **trusted Authenticode signature** (verified with `WinVerifyTrust`).

A checksum published in the same release an attacker would tamper with proves integrity, not
authenticity — so the **code signature is the real root of trust**.

- **Signed release** → installed silently (the service is already elevated).
- **Unsigned release** (today, until code-signing is set up) → the verified download is **staged**
  in an ACL-locked folder and offered in the app as a **one-click install**; you approve the normal
  UAC prompt. StepWind never runs an unsigned installer silently as SYSTEM.

The installer itself is the rollback actor: it backs up the current install before swapping files
and restores it if the new service doesn't start, so an update can't leave you unprotected.

### Verifying a download yourself

Every release ships `SHA256SUMS.txt`. On the downloaded file:

```powershell
(Get-FileHash .\StepWind-x.y.z-setup.exe -Algorithm SHA256).Hash
```

Compare it to the matching line in `SHA256SUMS.txt`.

## Code signing

StepWind is not code-signed yet. Signing is planned via the **SignPath Foundation** free
open-source program (OV certificate, key on their HSM, signature applied in CI only after they
verify the binary was built from this public repo). Until then Windows SmartScreen will show
"Windows protected your PC" for the installer — expected for a new, unsigned app, not a malware
detection. Choose **More info → Run anyway**, and verify the SHA-256 as above.

## Known limitations (honest)

- **Machine-wide config isn't elevation-gated.** Toggling encryption, auto-update, or retention is
  allowed for any connecting user, because the primary control surface (the tray GUI) is
  unelevated. These change behavior but don't leak or destroy another user's data. Tightening this
  behind a UAC-elevation flow is future work.
- **Same-name folders can't both be protected.** Two protected folders that share a leaf name
  ("Documents") would share one history namespace; adding the second is refused rather than
  silently merging them. A future stable per-root id will lift this.
- **Metadata isn't encrypted.** File paths, names, and timestamps in the version index are stored
  in plaintext even when blob content is encrypted (see "The store").
