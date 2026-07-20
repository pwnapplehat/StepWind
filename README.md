<div align="center">

<img src="assets/icon.png" width="96" alt="StepWind icon"/>

# StepWind

**An undo button for your whole PC.**
Real-time protection against accidental moves, renames, deletes, and bad saves — for
everyone, not just people who use git.

[![CI](https://github.com/pwnapplehat/StepWind/actions/workflows/ci.yml/badge.svg)](https://github.com/pwnapplehat/StepWind/actions)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-blueviolet)](https://dotnet.microsoft.com/)

Free · open source · 100% local · no cloud · no account · no telemetry

<img src="docs/screenshots/app.png" width="820" alt="StepWind timeline — a live, day-grouped feed of file operations with one-click undo"/>

</div>

---

## The problem

You move a folder, accidentally hit Ctrl+Z one too many times, and it's *gone* — not in
the Recycle Bin, no warning, no trace. Windows Explorer's undo is silent and can
permanently delete files, and this has been true for 20 years. Or you overwrite a good
draft with a bad one and only notice tomorrow. `git` protects committed code; it does
nothing for the uncommitted work — or for the 99% of people who don't use it.

Existing tools only version file **content** on a schedule. None of them record the
**operation** — the move/rename/delete itself — so none can simply *undo* it. StepWind does.

## Two layers of protection

**1. A flight recorder for your whole machine.** StepWind tails the NTFS change journal on
every drive and reconstructs a live, plain-English timeline of what happened — "Explorer
moved *Thesis* to Archive at 2:31 PM", attributed to the app that did it. A move or rename
is reversed with **one click**, instantly, needing no stored copy of your data.

**2. A time machine for the folders you care about.** Documents, Desktop, and your own
picks get continuous version history. Every save is captured as content-defined,
deduplicated, compressed chunks (the same technique restic/borg use), so scrolling a file
back to any earlier version — even after it was overwritten *and* deleted — is instant, and
a lightly-edited 2 GB file doesn't cost 2 GB per save.

## Built right from day one

- **Content-defined chunking + dedup** so history is cheap even for huge files, and identical
  re-saves don't add redundant versions.
- **Crash-safe, content-verified store** (atomic writes; every chunk re-hashed on read; GC is
  serialized against captures so it can never sweep an in-flight chunk).
- **Encryption at rest is a live toggle** (AES-256-GCM, key sealed by Windows DPAPI at machine
  scope — no passphrase needed, and a stolen/offline drive can't be read elsewhere). Flip it
  any time: the store re-encodes in the background while every version stays restorable, a
  crash mid-pass resumes on next start, and toggling off decrypts back the same way. File
  contents are encrypted; the index of names/dates is not. (Not designed to hide data from
  another admin on *this* machine.)
- **Retention + garbage collection** (tiered like Time Machine) so it never eats your disk.
- **Catch-up while off**: on startup (and when you add a folder) StepWind reconciles what
  changed or appeared while it wasn't running; USN wrap/overflow detection resyncs instead of
  silently missing changes; a watcher that overflows under a burst is rebuilt and reconciled.
- **Restores never overwrite** — a recovered version lands beside your current work, never on top.
- **Reversal is guarded** — it refuses to move something back onto a now-occupied path.
- **Panic hotkey** — Ctrl+Shift+Z opens StepWind from anywhere the instant something goes wrong.
- **Your data, your controls** — removing a folder asks whether to keep or delete its saved
  versions; Settings has delete-all, clean-up-unprotected, and run-cleanup-now (all confirmed
  first); retention windows are editable; the timeline can be scoped to protected folders only.
- **Smart exclusions** — build junk (`node_modules`, `target`…), caches, and — importantly —
  OneDrive online-only files are skipped (versioning a placeholder would force a full download).
  Files held under an *exclusive* lock (e.g. an open Outlook PST) are captured when the app
  releases them / on the next reconcile — StepWind doesn't force snapshots of locked files.
- **Honest architecture**: an elevated background **service** does the privileged work
  (journal + ETW); the tray **GUI** runs unelevated and talks to it over a local, ACL'd pipe.
- **Fully automatic, silent updates** — because the service already runs as SYSTEM, it
  checks GitHub for new releases, verifies the setup's SHA-256, and installs it with **zero
  UAC prompts**. Set-and-forget, like it should be.

## A UI designed for the job

StepWind has its own visual identity — an opaque deep-slate design with a navigation rail,
built around the product's core idea: a **time river**. Operations flow down the timeline
grouped by day, each with a color-coded rail (create / change / move / rename / delete),
monospaced clock time, the app that did it, and an Undo button right on the row. Filter
chips narrow the river to just deletes or just moves; the File versions page pairs a
searchable recently-changed list with full version history and one-click restore. Custom
visual layer, standard accessible control plumbing underneath — keyboard navigation and
screen readers keep working.

It's motion-polished, tastefully: views fade-and-rise on switch, rows cascade in, the active
nav indicator grows into place, buttons and cards answer with smooth colour fades, a subtle
press-scale and hover-lift, dialogs scale in, and the "protection active" dot has a gentle
heartbeat — all opacity/transform only (render-thread composited), so it's fluid without
taxing the machine.

| File versions | Protected folders | Settings |
|---|---|---|
| <img src="docs/screenshots/files.png" width="270"/> | <img src="docs/screenshots/folders.png" width="270"/> | <img src="docs/screenshots/settings.png" width="270"/> |

## Architecture

```
src/StepWind.Core/     engine: FastCDC chunker, content-addressed store (+AES-GCM),
                       retention/GC, USN journal reader, operation reconstruction &
                       reversal, ETW attribution, flight recorder, watch engine, IPC
src/StepWind.Service/  elevated Windows service: hosts the engine + named-pipe API
src/StepWind.App/      unelevated WPF tray app: timeline + version history + undo/restore
src/StepWind.Cli/      diagnostics + real-hardware E2E harness
tests/                 deterministic Core tests
```

## Verified

- **84 unit tests** — chunker determinism & shift-resistance, store dedup/crash-safety/
  integrity, encryption round-trip & tamper rejection, DPAPI key stability, live encryption
  toggling (mixed-store reads, background re-encode convergence in both directions,
  interrupted-migration recovery, storage byte tracking, the IPC toggle end-to-end), USN
  operation reconstruction (rename vs move via parent-FRN delta; POSIX-unlink deletes
  detected at the marker-rename instant, with the late FileDelete deduplicated and hex-named
  user files never misclassified), version-level dedup of identical re-saves, startup
  reconciliation (baseline + catch-up + idempotency), GC-vs-capture interleaving integrity,
  reversal safety, retention tiers + GC (and configurable retention with clamping), folder
  removal (captures stop, nothing reseeds, purge all/unprotected/folder/file), exclusions
  (incl. cloud placeholders), watch capture, and the full IPC capture→history→restore
  round-trip.
- **Real-hardware E2E** (elevated): a scripted create/rename/move/delete is reconstructed
  from the live journal, the move is reversed (folder back in one click), and a version is
  restored byte-exact after overwrite+delete — all through the production classes. The
  delete's journal shape (bare-hex marker rename; FileDelete lagging until the last handle
  closes) was measured on real hardware and is what the detector is built against.
- **Live demo**: the real service running with encryption on (key sealed, zero plaintext in
  blobs), the timeline populated with an actual incident, Undo working, and a UI-automation
  pass driving recent-files → version history → Restore (see the screenshot above).

## Install

Download **StepWind-x.y.z-setup.exe** from
[Releases](https://github.com/pwnapplehat/StepWind/releases) and run it. The installer sets
up the background service (auto-start), starts protecting immediately, and launches the tray
app (which then starts with Windows). From then on StepWind keeps itself up to date
automatically and silently — no reinstalling, no prompts. Uninstall from Settings → Apps like
any program; your version history is left intact.

Windows 10 (1809+) or Windows 11, an NTFS drive.

> **"Windows protected your PC"?** Expected for a new unsigned installer — not a malware
> detection. Click **More info → Run anyway**. It's open source; each release ships a
> `SHA256SUMS.txt`.

## Building

```powershell
dotnet build StepWind.slnx      # build everything
dotnet test                     # Core test suite
./build/publish.ps1             # self-contained service + GUI + CLI → dist/
iscc installer\stepwind.iss     # build the setup .exe → installer/Output/
```

Requires the .NET 10 SDK (and Inno Setup 6 for the installer).

## License

[MIT](LICENSE) — © 2026 StepWind Contributors. Made by iOS_hAT.
