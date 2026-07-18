# Changelog

All notable changes to StepWind are documented here.

## 1.0.0 — 2026-07-19

Initial release — an undo button for your whole PC.

- **Flight recorder (all drives):** tails the NTFS USN journal, reconstructs a plain-English
  operation timeline (rename vs move via parent-FRN delta; POSIX-unlink delete detection),
  attributes each operation to its process via ETW, and reverses a move/rename in one click
  (no stored content needed).
- **Folder time machine:** watched folders get continuous version history — content-defined
  chunked, deduplicated, compressed, crash-safe content-addressed storage with optional
  AES-256-GCM passphrase encryption, tiered retention + mark-sweep GC, and byte-exact restore
  that never overwrites current work.
- **Reliability from day one:** USN catch-up-while-off with journal-wrap detection; restores
  land beside current work; reversal refuses to clobber an occupied path; smart exclusions
  (build junk, caches, and OneDrive online-only placeholders).
- **Architecture:** an elevated background service (USN + ETW + engine + named-pipe API) with
  an unelevated WPF tray GUI over an ACL'd local pipe.
- **Automated setup installer** (Inno Setup): registers the service (auto-start, LocalSystem),
  starts protecting immediately, adds the tray app to Windows startup for all users, and
  launches it. Clean uninstall stops + removes the service and keeps your version history.
- **Fully automatic, silent updates:** the SYSTEM service checks GitHub for new releases,
  verifies the setup's SHA-256, and installs them with zero UAC prompts. Opt-out available.

Verified: 55 unit tests; real-hardware elevated E2E through the production classes
(reconstruct + reverse + version round-trip); live service demo; and an end-to-end
install/uninstall test of the real setup (service RUNNING + auto-start + startup entry;
clean removal with the store preserved).
