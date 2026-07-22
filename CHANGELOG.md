# Changelog

All notable changes to StepWind are documented here.

## 1.0.0 — 2026-07-20

Initial release — an undo button for your whole PC (and a safety net for AI coding agents).

- **Delete-undo is real, end to end.** A newly created file is now baselined within a moment of
  creation (a fast create-capture path), so a file created and deleted inside the debounce quiet
  window still leaves a restorable version instead of nothing. The timeline shows a one-click
  **Restore** on a deleted file that has saved history, and an honest "Not saved" tag when it
  genuinely has none — never a dead button.
- **Per-user authorization on the service pipe.** The elevated service now identifies the
  connecting user (pipe impersonation) and authorizes every private or destructive action
  against them: one local account can't read, restore, browse, or purge another account's file
  history; the machine-wide timeline only shows a caller operations inside folders they can
  access; wiping all history (or all orphaned history) requires an administrator. Ownership is
  recorded when a folder is added and backfilled from the folder's on-disk owner for existing
  installs, with a live "can this user actually read the folder" fallback so no one is locked
  out of history for folders they can already open.
- **Undo handles can no longer be forged.** A timeline "undo" used to carry a self-contained,
  client-supplied operation that the SYSTEM service would act on — a crafted request could move
  any file anywhere. The timeline now hands out an opaque handle into the recorder's in-memory
  ring; the service re-derives the real paths from its own entry and rejects any handle it
  didn't issue. Restores ignore a caller-supplied destination for unprivileged callers, and a
  recovered file from a no-longer-protected folder lands in the requesting user's own profile
  rather than a world-readable public folder. Protecting two folders that share a name is
  refused, since they would merge different files under one history.
- **Never fills the disk; never fails silently.** A storage guard pauses capturing when the
  store's drive drops below a free-space floor (default 1 GiB) or an optional store-size cap is
  hit, runs an emergency retention prune to win space back, and resumes automatically once
  there's room — surfacing a loud "Capturing paused" status the whole time. Skipped changes are
  re-captured by the reconcile pass, so nothing is lost, only deferred.
- **Fail-closed, verified, rollback-safe updates** (see the update entry below).
- **Streaming capture.** Files are chunked and stored one chunk at a time, so peak memory is a
  single chunk regardless of file size — a multi-GB file is never buffered whole inside the
  SYSTEM service.
- **User-managed exclusions (Protected folders → Excluded).** You can now exclude a subfolder
  or path inside a protected folder from versioning — heavy build outputs, datasets, or caches
  you don't want kept. The engine already honored an exclusion list (and still auto-skips
  `node_modules`/`.git`/temp/cloud-online-only files); this exposes it: a folder picker to add
  one, a list to remove them (versioning resumes immediately), and — since an exclusion inside
  a protected folder may already have saved versions — adding one offers the same keep-or-
  delete-history choice as removing a folder. Verified end-to-end: a file written under an
  exclusion gets no history while its sibling does, and versioning resumes the instant the
  exclusion is removed.
- **Full light theme (System / Light / Dark).** A soft-white light theme built to the same
  standard as the dark one: every surface, overlay, shadow, and text accent is a theme token
  (no hardcoded color assumes a dark background), with light-specific diff colors, card
  shadows, and darker-hue accents that stay readable on white. Pick the mode in Settings →
  Appearance; **System** follows Windows and switches live the moment you change the OS
  theme. The choice is remembered locally, resolved before first paint (no flash), and the
  chromeless window frame + pre-load backdrop track the theme so there's no dark strip around
  a light UI. Entrance animations now play only on intentional (re)renders — navigating,
  filtering, opening history — never on the 3-second background refresh, so the list no
  longer re-fades under you; reduced-motion preferences are honored.
- **Crisp text on scaled/multi-monitor displays + two entrance-animation glitches fixed.**
  The app is now **Per-Monitor V2 DPI aware** (app.manifest): WPF defaults to "System" DPI
  awareness, which bitmap-stretches the window — and softens WebView2's text — at non-100%
  display scaling or across monitors with different scales; PMv2 renders both the host and
  the web content at each monitor's true pixel density (ClearType-crisp, re-sharpening live
  as the window moves). Also: switching to File versions briefly flashed a horizontal
  scrollbar, and the AI agents cards jumped for a moment on open — both were the entrance
  animations (rows/cards translate a few px on X, and the vertical scrollbar appearing mid-
  stagger reflowed the grid). Fixed by clipping the transient horizontal overflow
  (`overflow-x: hidden`) and reserving the scrollbar gutter (`scrollbar-gutter: stable`) on
  the scroll containers, so nothing shifts.
- **Hardened three edge cases in the web-UI host** (all found by driving the real app on a
  real machine, not in theory): (1) opening the window from the tray/hotkey could crash with
  "WebView2 was already initialized with a different CoreWebView2Environment" — the window's
  load handler and the tray-open path both initialized the web view, and the guard only
  checked the *completed* state, so two initializations raced; init is now a single cached
  task every caller awaits. (2) The Ctrl+Shift+Z panic hotkey didn't work while the app sat
  minimized in the tray (its most common state) because it was registered against the visible
  window's handle, which doesn't exist until first shown — it now lives on a dedicated
  message-only window created at startup. (3) The bridge serializes its pipe calls: the web
  UI issues several at once and the service accepts one connection at a time, so a burst could
  eat into the connect timeout and read as "service not reachable."
- **Fixed the installer's service-stop race** (the actual cause of a post-upgrade "service not
  reachable"): setup blind-slept 5s after `sc stop` instead of waiting for the service to be
  genuinely STOPPED, so a slow stop let the file copy race the live service, and its
  crash-recovery restart could bring a new instance up on half-copied DLLs (observed: the
  service then failed to load `System.IO.Pipes.AccessControl` on every pipe accept until the
  next restart). Setup now disarms the crash-recovery action for the copy, then polls
  `sc query` until STOPPED before copying. Verified by upgrading in place over a running
  service: the pipe answers immediately afterward with zero load errors.
- **Web-rendered UI (WebView2):** the interface is drawn by a dependency-free web layer
  (plain HTML/CSS/JS shipped beside the exe) inside Windows' built-in WebView2 runtime — the
  architecture of VS Code/Discord/Linear/1Password without shipping Chromium. A thin .NET
  host owns the chromeless window (native drag/snap/double-click-maximize via `app-region`),
  tray icon, global hotkey, and an **allow-listed JSON bridge** to the service: the web
  layer can only invoke what's explicitly listed, settings patches only carry explicitly
  allowed keys, and the browser can only open StepWind's own two URLs. New capabilities the
  web stack made practical: an inline **unified diff viewer** in File versions (click any
  version to see exactly what changed vs. the file on disk now, falling back to the
  version's own content when the live file is gone), a **command palette (Ctrl+K)** that
  searches commands and every file in the version store, live 3-second refresh that never
  steals scroll position (fingerprinted renders), and a full in-app dialog system (notice /
  danger-confirm / keep-or-delete choices). The installer bundles Microsoft's signed
  WebView2 Evergreen bootstrapper and runs it only on machines missing the runtime (older
  Win10/LTSC); everything else already has it. Verified by a DEBUG-only in-app E2E runner
  that drives the real DOM against the real service — folder add/capture/diff/remove-with-
  history-delete, settings round-trips, timeline Undo moving a real folder back — plus the
  full unit suite.

- **A safety net for AI coding agents — MCP server + AI agents tab:** StepWind ships an MCP
  server (`StepWind.Mcp.exe`) that gives agents like Cursor and Claude a time machine, not a
  shredder. Ten tools: status, timeline, protected folders, browse, file history, read
  version, unified diff, checkpoint, restore, undo operation. Read-only + additive by
  design: an agent can checkpoint a file before a risky edit, diff exactly what it changed
  (`latest:` vs `current:`), and restore — it can never delete history or change settings.
  Restores never overwrite the live file. Diffs come from a linear-space Hirschberg LCS
  engine (an always-on service must not allocate a 1.6 GB DP table because two 20,000-line
  files showed up). Runs on demand over stdio; nothing listens in the background.
  The dedicated **AI agents** tab detects the AI tools installed on your PC — Cursor, Claude
  Desktop, Claude Code, Antigravity, Windsurf, VS Code (Copilot), Cline, Gemini CLI, Codex
  CLI, Copilot CLI, LM Studio, Kiro — and connects StepWind to any of them with one click.
  No hand-editing JSON: StepWind merges its entry into the tool's own MCP config (each
  tool's real path and schema researched individually, including Claude Desktop's
  MSIX-virtualized config location and Antigravity's post-2.0 migration marker; Codex CLI's
  TOML gets line-conservative surgery that leaves every other line untouched). Safety rules
  for touching someone else's config: strict-parse-or-refuse (a JSONC file with comments is
  never rewritten — rewriting would strip the comments), timestamped backup before every
  change (Open backups folder in the app), atomic temp+rename writes, and a post-write
  re-parse that restores the backup automatically if verification fails. Disconnect (also
  one click) removes exactly our entry and nothing else. Repair shows up automatically when
  a connected tool points at a stale StepWind location. Manual copy-paste config stays
  available for MCP clients we don't auto-detect. The path written into configs is always
  SPACELESS (a copy of the server ships at `%ProgramData%\StepWind\bin`): several MCP
  clients — Cursor among them, verified from its own logs — spawn the stdio command through
  cmd.exe without quoting, so a `C:\Program Files\...` command executes `C:\Program` and
  dies with "not recognized as an internal or external command".
- **Flight recorder (all drives):** tails the NTFS USN journal, reconstructs a plain-English
  operation timeline (rename vs move via parent-FRN delta), attributes each operation to its
  process via ETW, and reverses a move/rename in one click (no stored content needed).
  POSIX-unlink deletes (the modern Windows delete path) are detected **at the marker-rename
  instant** — measured on real hardware: the FileDelete record can lag minutes behind when
  another process (an AV scan, an indexer) still holds the file, and the timeline must not.
  Process attribution is held to a hard rule: **a wrong name is worse than no name.** Only
  authored actions count (namespace creates, writes, renames, deletes, set-disposition, and
  delete-on-close opens — how `del` and DeleteFile actually delete, measured on real
  hardware) — never handle opens or stats, which is how watchers/antivirus/indexers used to
  get blamed for other apps' changes. Each operation is matched against a per-path history
  by its OWN kind and timestamp (a delete only matches delete-shaped events from the moments
  at or before it, so late reactions can't steal attribution), the kernel's own lazy-writer
  (System, pid 4) is never treated as an author, the ETW session tracks process start/stop
  so recycled PIDs resolve to the process that owned the PID at event time, and when nothing
  fits the label is honestly blank. Because real-time ETW delivery lags the USN journal by a
  buffer flush, each poll re-attributes still-blank operations from the last few seconds —
  verified live: an author's create/modify/delete all name the author even while another
  process aggressively stats the same file throughout.
- **Folder time machine:** watched folders get continuous version history — content-defined
  chunked, deduplicated, compressed, crash-safe content-addressed storage, tiered retention +
  mark-sweep GC, and byte-exact restore that never overwrites current work. Identical
  re-saves are deduplicated at the version level, so history stays meaningful.
- **Encryption at rest — a live toggle, not a create-time decision:** flip the switch in
  Settings and the store re-encodes itself in the background (AES-256-GCM, key sealed by
  Windows DPAPI at machine scope — no passphrase needed by the unattended service; a
  stolen/offline drive can't be read elsewhere). Both formats stay readable throughout —
  every decode is verified against the chunk's content hash, a crash mid-re-encode resumes
  on next start, and toggling off decrypts back the same way. Verified live over the pipe:
  key sealed on first enable, zero plaintext in blobs, both eras of history restorable,
  clean convergence in both directions. File contents are encrypted; the index of names
  and dates is not.
- **Storage visibility:** Settings and the status footer show exactly what the history
  costs on disk (live byte tracking — no directory rescans), alongside the version count.
- **Removing a protected folder is a real decision, honored forever:** removal stops new
  captures instantly (verified live on the running service), and the app asks what to do
  with the already-saved versions — **Keep history** (restorable until retention ages it
  out) or **Delete history** (gone now, disk space freed). Two bugs that made removal look
  broken are fixed: (1) when the folder list became empty the GUI silently re-seeded the
  default folders on the next launch — a "first run completed" flag now records that a
  human made a choice, so removed folders never come back on their own; (2) the background
  baseline scan that captures a newly added folder's existing files **kept running after
  the folder was removed** — on a large folder it ground on for minutes, so versions kept
  appearing with zero folders protected. The scan now aborts the instant the folder set
  changes (regression test pins it).
  Restores of files whose folder is no longer protected land in the requesting user's own
  `Documents\StepWind Restored` (an administrator/CLI restore uses `Public Documents`) — never
  inside the ACL-locked store where a standard user couldn't open their own recovered file, and
  never a world-readable public folder for a normal user's private data.
- **Your data, your controls (Settings → Data management):** delete ALL history, clean up
  history belonging to no-longer-protected folders, run the retention cleanup on demand,
  and delete a single file's history from its version pane — every destructive action
  confirmed first in the app's own dialogs, every one reports exactly what it removed.
- **Retention is configurable:** keep-all window, hourly/daily thinning, hard age cap, and
  max versions per file are editable in Settings (values clamped to sane floors so a typo
  can't wipe history).
- **Timeline scope control:** switch the timeline between "All drives" (the full flight
  recorder) and "Protected only" — so after you stop protecting a folder, you can choose
  not to see its churn either. Persisted like every other setting.
- **Reliability from day one:** startup reconciliation captures whatever changed or appeared
  while StepWind wasn't running (and baselines newly added folders immediately); USN
  journal-wrap detection resyncs instead of silently missing changes; an overflowed watcher
  is rebuilt and reconciled; retention GC is serialized against live captures so a sweep can
  never delete an in-flight chunk; restores land beside current work; reversal refuses to
  clobber an occupied path; smart exclusions (build junk, caches, OneDrive online-only
  placeholders).
- **A custom-designed app, not a themed dialog:** StepWind has its own visual identity —
  opaque deep-slate surfaces, a navigation rail (Timeline / File versions / Protected
  folders / Settings), and a day-grouped **time river** timeline with color-coded operation
  rails, filter chips, per-row process attribution and Undo. File versions pairs a
  searchable recently-changed list with full history and one-click restore. The custom
  visual layer sits on standard accessible control plumbing, so keyboard navigation and
  screen readers keep working. A global panic hotkey — **Ctrl+Shift+Z** — opens StepWind
  from anywhere.
- **The flight recorder is a real toggle now** (it was a read-only badge). Flip it in
  Settings to start/stop whole-machine operation recording live, no restart. If ETW/USN
  can't start (e.g. an unprivileged run) the switch fails honestly and stays off rather than
  lying.
- **Auto-update toggle now takes effect at runtime.** The update loop only read the setting
  once at service start, so flipping it in Settings did nothing until a restart (turn on →
  no checks; turn off → checks kept running). The loop now re-reads the live setting each
  cycle. Found in a full UI→service audit alongside a timeline empty-state that explains why
  the timeline is blank (recorder off vs. no activity yet) instead of showing an empty card.
- **Fixed a serious installer/auto-update bug:** the setup declared `AppMutex`, so with
  `/SUPPRESSMSGBOXES` the "app is running" prompt auto-cancelled and the whole silent update
  **aborted before copying a single file** — upgrades (and every silent auto-update) kept the
  OLD service binary while reporting success. Setup now closes the tray app via Restart
  Manager and cleanly stops the service before copying, so binaries actually get replaced.
  (Also: the E2E harnesses now back up and restore your real settings.json, so testing can
  never again leave the flight recorder or auto-update switched off on a real machine.)
- **File versions is a folder browser now, not one long list:** open a protected folder,
  drill into any subfolder via breadcrumbs, and see version history scoped to what you picked
  — built for deep dev trees (`Reports/Q1/…`, `Code/src/…`), not just a flat feed. Each
  folder shows how many files and versions live beneath it; single-click drills in, and a
  search box does a fast recursive find of files anywhere under the current folder. The old
  flat "recently changed" list is gone in favour of this.
- **Smooth, animated scrolling** everywhere (timeline, browser, history, settings) — real
  pixel-based inertia-style easing instead of the default jump-by-a-few-rows, via an
  attached behavior that animates the scroll offset with a cubic ease-out.
- **Motion that feels premium, not busy:** views fade-and-rise on switch, list rows cascade
  in as they load, the active nav indicator grows into place, buttons/cards respond with
  smooth colour fades + a subtle press-scale and hover-lift, dialogs scale in, and the
  "protection active" dot has a gentle heartbeat. Everything animates only opacity and
  transforms (composited on the render thread) with short cubic ease-outs — so it stays
  fluid and never spikes CPU/GPU the way animated blur would.
- **Architecture:** an elevated background service (USN + ETW + engine + named-pipe API) with
  an unelevated WPF tray GUI over an ACL'd local pipe; explicit append-only wire-protocol ids
  so mixed-version GUI/service pairs mid-update can never execute the wrong command; a
  single-instance guard so a stray second service can never split pipe traffic.
- **Automated setup installer** (Inno Setup): registers the service (auto-start, LocalSystem),
  starts protecting immediately, adds the tray app to Windows startup for all users, and
  launches it. Clean uninstall stops + removes the service and keeps your version history.
- **Fail-closed, verified updates:** the SYSTEM service checks GitHub for new releases and
  installs one only if it passes a SHA-256 checksum matched to its filename **and** a trusted
  Authenticode signature — a release missing either is refused, never run. The installer backs
  up the current install and rolls back automatically if the new build won't start, so an
  update can't leave the machine unprotected. Silent auto-install stays disabled until releases
  are code-signed (the safe default). Opt-out available.

Verified: 217 unit tests; real-hardware elevated E2E through the production classes
(reconstruct + reverse + version round-trip, including the marker-time delete path measured
against the live NTFS journal); live service demo with encryption on (key sealed, zero
plaintext leak, restore byte-exact); UI-automation pass driving every view of the redesigned
GUI (timeline, recent-files → version history → Restore, folders, settings); and an
end-to-end install/uninstall test of the real setup (service RUNNING + auto-start + startup
entry; clean removal with the store preserved).
