# Changelog

All notable changes to StepWind are documented here.

## 1.0.0 — 2026-07-24

Initial release — an undo button for your whole PC, and a safety net for AI coding agents.

### Protect & undo (the flight recorder)

- **A whole-machine timeline.** StepWind tails the change journal on every drive that provides
  one (NTFS, and ReFS/Dev Drive where Windows exposes a journal), reconstructs a plain-English
  operation feed — creates, modifies, moves, renames, deletes — and reverses a move or rename in
  one click, no stored content needed. Bulk operations undo as a batch with a per-item report;
  a partial failure never silently stops the rest.
- **Deletes are caught at the honest instant.** POSIX-unlink deletes (the modern Windows delete
  path) are detected at the marker-rename moment — measured on real hardware, the journal's
  FileDelete record can lag minutes behind when an AV scan or indexer still holds the file, and
  the timeline must not.
- **Process attribution held to a hard rule: a wrong name is worse than no name.** Only authored
  actions count (writes, renames, deletes, set-disposition, delete-on-close) — never handle opens
  or stats, which is how watchers, antivirus, and indexers get blamed for other apps' changes in
  lesser designs. Each operation matches its author by kind and time, the kernel's lazy-writer is
  never treated as an author, recycled PIDs resolve to the process that owned the PID at event
  time, and when nothing fits, the label is honestly blank.
- **Honest coverage.** The drive-coverage panel reports which volumes the recorder is *actually*
  reading — never guessed from the filesystem name. Drives without a journal aren't on the
  timeline, and the app says so; folder version history works on them regardless.
- **Journal wrap/truncation is detected and resynced loudly** instead of silently missing
  operations; an overflowed folder watcher is rebuilt and reconciled the same way.
- **`Ctrl`+`Shift`+`Z` from anywhere** opens StepWind — a global panic hotkey that works even
  while the app sits minimized in the tray.

### Version history (the folder time machine)

- **Continuous history for the folders you choose:** content-defined chunking, deduplication,
  compression, and crash-safe content-addressed storage, with tiered retention (keep-all window →
  hourly → daily → age cap, all editable, clamped to sane floors) and mark-sweep GC serialized
  against live captures. Identical re-saves deduplicate at the version level.
- **Any filesystem.** Folder history uses the file system watcher, not the journal — removable
  drives, exFAT, and network shares all version fine.
- **Same-name folders are first-class.** Two protected folders may share a name ("Documents" on
  two drives): each root stores under a stable namespace — the folder's name, or a deterministic
  suffixed id on collision — so histories and owner sets never merge, an unrelated same-named
  folder can never adopt a removed folder's history, and re-protecting the same path re-attaches
  its old history.
- **A file created and deleted seconds later is still restorable:** new files are baselined by a
  fast create-capture path within moments of appearing, so the create-then-delete window leaves a
  version, not nothing. The timeline offers one-click **Restore** on deleted files with saved
  history, and an honest "Not saved" tag when there genuinely is none — never a dead button.
- **Streaming capture:** files are chunked and stored one chunk at a time, so peak memory is a
  single chunk regardless of file size — a multi-GB file is never buffered whole inside the
  SYSTEM service.
- **Deep trees welcome:** paths beyond 260 characters are versioned (extended-length path
  support), and File versions is a real folder browser — breadcrumbs, per-folder file/version
  counts, recursive search — built for `Code/src/…`, not just a flat feed.
- **User-managed exclusions** for build outputs, datasets, and caches (on top of automatic skips
  for `node_modules`, `.git`, temp files, and cloud online-only placeholders), plus an opt-in
  setting to honor a repo's `.gitignore`. Versions captured inside a git repo are annotated with
  the branch/commit they were saved on.
- **Files held open by another program are surfaced** in Settings instead of silently skipped;
  startup reconciliation captures whatever changed while StepWind wasn't running, and a removed
  folder stops capturing instantly — the baseline scan aborts the moment the folder set changes,
  and removal asks what to do with already-saved versions (keep or delete), a choice that's
  honored forever.

### Safety rules (enforced in the engine, not the UI)

- **Restores never overwrite.** A recovered file lands beside the current one; reversal refuses
  an occupied destination rather than clobbering it.
- **The disk never fills silently.** A storage guard pauses capturing below a free-space floor
  (default 1 GiB) or past an optional store-size cap, runs an emergency retention prune, resumes
  automatically, and shows a loud "Capturing paused" status the whole time. Skipped changes are
  re-captured by the reconcile pass — deferred, not lost.
- **The store heals and proves itself:** an integrity check runs at startup, Settings offers
  verify + repair (quarantining only unrestorable records), the index is backed up automatically,
  and Data management can relocate the whole store to another drive the safe way — copy, verify
  every version restores, then switch, leaving the old copy untouched.

### Security & privacy

- **100% local.** No cloud, no account, no telemetry. The only network call is the release check
  on GitHub, and it can be turned off.
- **A locked-down service pipe.** The elevated service's named pipe accepts **local** users only
  (the NETWORK group is denied, so remote SMB clients are rejected) and is bounded against denial
  of service (per-connection read timeout + maximum request size, so a stalled or flooding client
  can't take the service offline). The unelevated GUI/CLI/MCP also verify the pipe is owned by a
  privileged identity before trusting it, so a standard-user process can't squat the name and fake
  a "you're protected" view.
- **Per-user authorization.** The service resolves the connecting user's Windows identity by
  impersonating the pipe and authorizes every private or destructive action against it: one
  account can't read, browse, restore, or purge another's history; the timeline shows a caller
  only operations on files they can access; machine-wide purges require an administrator. Adding a
  folder requires that the caller's own token can read it — the SYSTEM service can't be used to
  capture files the caller couldn't open themselves.
- **Machine-wide settings are elevation-gated on shared PCs.** While you are the only user who
  owns history, you manage everything from the unelevated app; the moment *another* user also owns
  history, encryption/update/retention/storage settings require an administrator
  (`stepwind-cli set-settings` from an elevated terminal) — so no bystander account can weaken
  protection of data that isn't theirs.
- **Undo handles can't be forged, and reversal is destination-gated.** The timeline hands out
  opaque handles into the recorder's own ring; the service re-derives real paths from its own
  entry and rejects any handle it didn't issue — a client can't craft "move *this* to *there* as
  SYSTEM." Reversing an operation additionally requires access to the restore *destination*, so no
  one can drive a SYSTEM move into a folder they have no rights to. Restore destinations are never
  caller-controlled for unprivileged users, and recovered files from no-longer-protected folders
  land in the requesting user's own profile, never a world-readable folder.
- **Encryption at rest as a live toggle:** AES-256-GCM with a machine-DPAPI-sealed key; flipping
  the switch re-encodes the store in the background with both formats readable throughout, every
  decode verified against the chunk's content hash, and a crash mid-re-encode resuming on next
  start. Optional **index encryption** covers the metadata (names, paths, dates) too, and an
  exportable passphrase recovery key (PBKDF2-SHA256, 600k iterations + AES-256-GCM) means
  encrypted history survives an OS reinstall. The store itself is ACL-locked to
  SYSTEM + Administrators.

### Enterprise & managed fleets

- **Central policy (Group Policy / MDM).** Administrators enforce settings via
  `HKLM\SOFTWARE\Policies\StepWind` (ADMX/ADML template in `enterprise/policy/`): encryption,
  index encryption, auto-update, the flight recorder, `.gitignore` policy, retention, storage
  limits, mandatory always-protected folders, and whether users may change folders at all. An
  enforced setting binds every caller — standard user *or* local administrator — because only the
  org's policy may change it; in a managed fleet this makes machine-wide configuration central and
  removes the shared-PC question. The tray app shows managed settings dimmed with a clear notice.
- **Security audit trail.** Every security-relevant action — settings changes, purges, restores,
  reversals, folder changes, encryption toggles, updates, and policy-denied attempts — is written
  (naming the acting user, with stable Event IDs) to the Windows Application event log under the
  dedicated `StepWind.Audit` source, ready for Windows Event Forwarding or any SIEM/EDR. Nothing
  leaves the machine on its own.
- **Fleet deployment.** Silent EXE install for Intune/ConfigMgr (`/VERYSILENT`), a documented
  registry/ADMX policy reference, and a managed uninstall that preserves history by default. See
  [`enterprise/README.md`](enterprise/README.md).

### Updates

- **Fail-closed, verified, rollback-safe.** A release is downloaded into an ACL-locked staging
  folder (hardened before the download) and installs silently only if it ships `SHA256SUMS.txt`,
  the download's SHA-256 matches for its exact filename, *and* the setup is Authenticode-signed by
  StepWind's **pinned** certificate — re-verified on the exact staged file immediately before
  launch. Until a certificate is pinned (as today, before free code-signing via SignPath
  Foundation is enabled), **nothing installs silently**: every release — even a validly signed one
  — is staged in the ACL-locked folder and offered as a one-click install behind the normal UAC
  prompt. The installer is the rollback actor: it
  backs up the current install, health-checks the new service, and restores the backup if it
  won't start. A pulled release retracts its staged offer instead of advertising a ghost update.
- **Releases ship for x64 and ARM64**, and the updater picks the installer matching the CPU.

### AI agents & developers

- **An MCP server with guardrails** (`StepWind.Mcp.exe`, stdio, runs on demand): eleven tools —
  status, timeline, protected folders, browse/search, file history, read version, unified diff,
  checkpoint, restore, single and batch undo. Read + additive only, enforced by an in-process
  command allow-list: an agent can checkpoint before a risky edit, diff `latest:` vs `current:`,
  and restore — it can never delete history or change settings. Diffs come from a linear-space
  Hirschberg LCS engine, so two 20,000-line files can't allocate a gigabyte inside the service.
- **An Agent Skill teaches the habits.** Connecting a tool that supports Agent Skills (Cursor,
  Claude Code) also installs StepWind's `SKILL.md` — checkpoint before risky edits, diff after,
  restore on regret — so models reach for the safety net at the right moments. Other tools can
  copy the skill from the manual-setup card. Disconnect removes exactly our file and nothing else.
- **One-click connect** for Cursor, Claude Desktop, Claude Code, Antigravity, Windsurf, VS Code
  (Copilot), Cline, Gemini CLI, Codex CLI, Copilot CLI, LM Studio, and Kiro — each tool's real
  config path and schema handled individually (including Claude Desktop's MSIX-virtualized
  location and Codex CLI's TOML, edited with line-conservative surgery). Config safety rules:
  strict-parse-or-refuse (a JSONC file with comments is never rewritten and stripped), a
  timestamped backup before every change, atomic writes, and post-write verification that
  restores the backup if anything looks wrong. The server path written into configs is always
  spaceless, because several MCP clients spawn stdio commands through cmd.exe without quoting.
- **A scriptable CLI** (`stepwind-cli`): status, timeline, history, read, diff, checkpoint, undo,
  restore, protect/unprotect, verify/repair, purge, relocate-store, set-settings, encryption and
  recovery-key management — JSON output throughout.
- **Diagnostics export:** a support bundle with configuration and health only — no file names,
  no contents.

### The app

- **First-run onboarding that asks.** A welcome screen explains the always-on flight recorder and
  lets you choose which folders get version history, with pre-checked suggestions. Nothing is
  captured without an explicit choice.
- **A custom-designed, web-rendered UI** (WebView2 — the VS Code/Discord/Linear architecture
  without shipping Chromium): a chromeless window with native drag/snap, a day-grouped timeline
  with color-coded operation rails and filter chips, an inline unified diff viewer, a command
  palette (`Ctrl`+`K`) that searches commands and every versioned file, and an allow-listed JSON
  bridge — the web layer can only invoke what's explicitly listed. A WPF fallback screen handles
  a missing WebView2 runtime with retry/install actions; the installer bundles Microsoft's signed
  Evergreen bootstrapper for machines that need it.
- **Full light/dark/system theming** resolved before first paint (no flash), with the window
  frame tracking the theme; live 3-second refresh that never steals scroll position; entrance
  animations only on intentional renders, transform/opacity only, honoring reduced-motion.
- **Tray-first behavior:** notifications when protection stops, capturing pauses, or an update is
  ready; a live tooltip; relaunching the app brings the existing window forward.
- **Accessible:** screen readers announce protection-state changes and notifications; dialogs
  trap and restore focus; timeline actions carry descriptive labels; everything operates from the
  keyboard.
- **International:** dates and numbers follow the Windows locale; UI text flows through a
  translatable catalog (a new language is one data file). English ships complete.
- **Per-Monitor V2 DPI aware** — ClearType-crisp on scaled and mixed-DPI displays, re-sharpening
  live as the window moves between monitors.

### Architecture & install

- An elevated background service (journal + ETW + engine + named-pipe API) with an unelevated
  tray GUI over an ACL'd local pipe; append-only wire-protocol ids so mixed-version GUI/service
  pairs mid-update can never execute the wrong command; a single-instance guard against split
  pipe traffic; concurrent pipe serving so one slow client can't stall the GUI and every MCP
  client behind it.
- An automated installer (Inno Setup) that registers the auto-start service, starts protection,
  adds the tray app to startup, and waits for a genuine service STOP before swapping files on
  upgrade. Uninstall stops and removes the service and keeps your version history.

---

Verified for release: **294 unit tests** (chunking, store, retention, undo, authorization,
updates, IPC wire contract, root namespaces — timing-sensitive tests written deterministically);
real-hardware elevated end-to-end runs through the production classes (reconstruct + reverse +
version round-trip, including the marker-time delete path measured against the live NTFS
journal); a live service run with encryption on (key sealed, zero plaintext in blobs, byte-exact
restore); a DEBUG-only in-app E2E runner driving the real DOM against the real service; an MCP
stdio smoke test in CI; and an install/upgrade/uninstall pass of the real setup on real hardware
(service RUNNING, pipe answering immediately after an in-place upgrade, store preserved on
uninstall).
