# Changelog

All notable changes to StepWind are documented here.

## 1.0.0 — 2026-07-20

Initial release — an undo button for your whole PC.

- **Flight recorder (all drives):** tails the NTFS USN journal, reconstructs a plain-English
  operation timeline (rename vs move via parent-FRN delta), attributes each operation to its
  process via ETW, and reverses a move/rename in one click (no stored content needed).
  POSIX-unlink deletes (the modern Windows delete path) are detected **at the marker-rename
  instant** — measured on real hardware: the FileDelete record can lag minutes behind when
  another process (an AV scan, an indexer) still holds the file, and the timeline must not.
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
  Restores of files whose folder is no longer protected land in
  `Public Documents\StepWind Restored` — an earlier build dropped them inside the
  ACL-locked store where a standard user couldn't open their own recovered file.
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
- **Architecture:** an elevated background service (USN + ETW + engine + named-pipe API) with
  an unelevated WPF tray GUI over an ACL'd local pipe; explicit append-only wire-protocol ids
  so mixed-version GUI/service pairs mid-update can never execute the wrong command; a
  single-instance guard so a stray second service can never split pipe traffic.
- **Automated setup installer** (Inno Setup): registers the service (auto-start, LocalSystem),
  starts protecting immediately, adds the tray app to Windows startup for all users, and
  launches it. Clean uninstall stops + removes the service and keeps your version history.
- **Fully automatic, silent updates:** the SYSTEM service checks GitHub for new releases,
  verifies the setup's SHA-256, and installs them with zero UAC prompts. Opt-out available.

Verified: 70 unit tests; real-hardware elevated E2E through the production classes
(reconstruct + reverse + version round-trip, including the marker-time delete path measured
against the live NTFS journal); live service demo with encryption on (key sealed, zero
plaintext leak, restore byte-exact); UI-automation pass driving every view of the redesigned
GUI (timeline, recent-files → version history → Restore, folders, settings); and an
end-to-end install/uninstall test of the real setup (service RUNNING + auto-start + startup
entry; clean removal with the store preserved).
