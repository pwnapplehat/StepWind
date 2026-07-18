# Changelog

All notable changes to StepWind are documented here.

## 1.0.1 — 2026-07-19

### Added
- **Automated setup installer** (Inno Setup). One `StepWind-x.y.z-setup.exe` registers the
  background service (auto-start, LocalSystem), starts protecting immediately, adds the tray
  app to Windows startup for all users, and launches it — no manual PowerShell, no portable
  extraction. Clean uninstall stops + removes the service and keeps your version history.
- **Fully automatic, silent updates.** The SYSTEM service checks GitHub for new releases,
  downloads the setup, verifies its SHA-256 against the release checksum, and installs it
  silently with **zero UAC prompts** (the service is already elevated). One check ~2 minutes
  after start, then daily; opt-out via `AutoUpdateEnabled`.
- Service self-management verbs (`install-service` / `uninstall-service` / `start-service` /
  `stop-service`) so all service plumbing lives in our code, not in installer script strings.

### Changed
- Distribution is now the setup .exe (the portable zip + manual `install.ps1` are gone).

### Verified
- Elevated end-to-end install test: silent install → service RUNNING + AUTO_START + GUI
  startup entry present + files in Program Files → silent uninstall → service and startup
  entry removed, version store preserved. 55 unit tests still green.

## 1.0.0 — 2026-07-18

Initial release — an undo button for your whole PC (flight recorder + folder time machine).
