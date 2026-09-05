# Developing StepWind from Linux

StepWind is a Windows app (`net10.0-windows` + a WebView2 host). It **cannot compile or
run on Ubuntu**. GitHub Actions `windows-latest` is the build machine: unit tests, publish,
pinned Inno Setup, SHA256SUMS, and (on a `v*` tag) the GitHub Release including ARM64.
That is enough for small changes. A real Windows desktop is reserved for major UI/service work.

The UI itself is `src/StepWind.App/web/` — dependency-free HTML/CSS/JS. You can edit and
preview that folder in a browser on Ubuntu; the .NET host and named-pipe bridge still
need Windows CI to compile.

## Everyday loop

```bash
# in StepWind/
git push origin main          # CI: restore, build, test, publish, Inno installer, SHA256SUMS
```

Watch [Actions](https://github.com/pwnapplehat/StepWind/actions). Preview the web UI:

```bash
python3 -m http.server 8124 --directory src/StepWind.App/web
# open http://localhost:8124/  (bridge calls fail — expected off Windows)
# File versions still paints: Hide folders, drag both splitters (empty diff until Windows).
# Sample novel-length EN+JA wrap fixture (not used by the packaged app):
#   http://localhost:8124/?preview=diff  — folders | version list | wrapped diff
```

## Shipping a release (no local Windows)

1. Bump `<Version>` in `Directory.Build.props` **and** `#define AppVersion` in
   `installer/stepwind.iss`, add a `CHANGELOG.md` entry, commit, push, wait for green CI.
2. Tag that version:

```bash
git tag -a v1.0.5 -m "StepWind v1.0.5"
git push origin v1.0.5
```

3. CI already publishes `StepWind-<ver>-setup.exe`, `StepWind-<ver>-arm64-setup.exe`, and
   `SHA256SUMS.txt` (binary-mode `*` lines — required by the SYSTEM updater). Silent
   auto-install stays disabled until Authenticode/SignPath is configured.

Do **not** recut an existing tag. Do **not** treat SmartScreen as a product bug.

## What Ubuntu cannot do

- `dotnet build` / `dotnet test` / `dotnet run` of this repo.
- The elevated service, USN journal, tray app, MCP one-click install into Cursor, E2E.

A Windows 11 KVM guest on this host can open the same preview from Edge at
`http://192.168.122.1:8124/?preview=diff` while the host server binds `0.0.0.0`.
That checks wrap / hide folders / both splitters only. Real File versions still
needs the installer from GitHub Actions.
