# Getting StepWind code-signed for free (SignPath Foundation)

StepWind ships unsigned today, so Windows SmartScreen shows "Windows protected your PC" on the
installer and Windows 11 Smart App Control may block it until the file builds cloud reputation.
The **free** fix for open-source projects is the **SignPath Foundation** program: they issue an
OV code-signing certificate to the Foundation and sign your CI-built binaries after verifying they
came from this public repo. No cost, no hardware token, key stays on their HSM.

This is the only signing route we pursue — no paid certificates.

> Everything the updater and installer need is already wired for signing (see
> [Wiring CI](#3-wiring-ci-signing) below); this is the one step that needs a human: submitting
> the application as the repo owner.

## 1. Eligibility (StepWind qualifies)

SignPath Foundation's conditions and StepWind's status:

| Condition | StepWind |
|---|---|
| OSI-approved open-source license | ✅ MIT (`LICENSE`) |
| Public source repository | ✅ https://github.com/pwnapplehat/StepWind |
| Free downloads | ✅ GitHub Releases |
| No malware / no bundled proprietary components | ✅ 100% open source, no telemetry, no bundleware |
| Documented functionality | ✅ `README.md` + `SECURITY.md` |
| Already released in signable form | ✅ v1.0.0 installers on GitHub Releases |
| Actively maintained | ✅ (keep commits/releases current) |

Their terms also require, once approved (both are one-line doc edits, prepared in
[§4](#4-after-the-first-signed-release)):
- the exact attribution *"Free code signing provided by SignPath.io, certificate by SignPath
  Foundation"* on the project's home/download page, and
- a **code signing policy** stated on the project page (what gets signed, by whom, from where —
  ours: only CI-built artifacts from tagged releases of this public repo are submitted for
  signing; no locally-built binaries are ever signed).

## 2. Application — copy/paste answers

Apply at **https://signpath.org/apply.html** (also reachable via signpath.io → Open Source →
Apply). Review typically takes **1–2 weeks**. Use these answers:

- **Project name:** StepWind
- **Project description:** An open-source Windows app that gives you an undo button for your whole
  PC — real-time protection against accidental moves, renames, deletes, and bad saves, with
  continuous version history for chosen folders. 100% local, no cloud, no telemetry.
- **Repository URL:** https://github.com/pwnapplehat/StepWind
- **License:** MIT (OSI-approved) — see `LICENSE` in the repo root.
- **Programming language / build:** C# / .NET 10, built with `dotnet publish` + Inno Setup in
  GitHub Actions (`.github/workflows/ci.yml`).
- **Download / release URL:** https://github.com/pwnapplehat/StepWind/releases
- **Artifacts to sign:** the Windows installers `StepWind-<version>-setup.exe` (x64) and
  `StepWind-<version>-arm64-setup.exe` (ARM64), plus the app executables they package
  (`StepWind.exe`, `StepWind.Service.exe`, `StepWind.Mcp.exe`, `StepWind.Cli.exe`).
- **CI system:** GitHub Actions (public repo; SignPath can verify the build provenance).
- **Maintainer:** iOS_hAT (repo owner `pwnapplehat`).

After applying:

1. Install the **SignPath GitHub App** on the `pwnapplehat/StepWind` repo.
2. In the SignPath dashboard, note your **Organization ID**, create a **Project** (slug e.g.
   `stepwind`), an **Artifact configuration**, and a **Signing policy** (slug e.g. `release`).
3. Create an **API token** for CI.

## 3. Wiring CI (signing)

CI is already prepared. To turn signing on, set these on the GitHub repo:

- **Actions secret** `SIGNPATH_API_TOKEN` = your SignPath API token.
- **Actions variables** `SIGNPATH_ORGANIZATION_ID`, `SIGNPATH_PROJECT_SLUG` (e.g. `stepwind`),
  `SIGNPATH_POLICY_SLUG` (e.g. `release`).
- **Actions variable** `ENABLE_SIGNING` = `true` (the switch the workflow checks).

With `ENABLE_SIGNING=true`, the `Sign installer (SignPath)` step in `ci.yml` submits the built
installer to SignPath, waits for the signed artifact, and replaces it in place — so the published
`SHA256SUMS.txt` and GitHub Release cover the **signed** binary. With it unset (the default), the
step is skipped and unsigned artifacts ship exactly as today.

## 4. After the first signed release

1. **Add the required attribution** to `README.md` (SignPath's terms require it):

   > Free code signing provided by [SignPath.io](https://signpath.io), certificate by
   > [SignPath Foundation](https://signpath.org).

2. **Pin the certificate in the updater.** Once you know StepWind's signing certificate
   thumbprint (visible on the signed exe → Properties → Digital Signatures, or in the SignPath
   dashboard), set `ExpectedSignerThumbprint` in
   `src/StepWind.Service/UpdateService.cs` to it. That makes the auto-updater require **our**
   certificate specifically (not merely any trusted publisher), and — because the setup is now
   signed — silent auto-updates start working automatically (the fail-closed check passes).

3. **Submit to winget** (also free, also removes friction): run
   `./build/winget-manifest.ps1 -Version <v> -InstallerPath <signed-setup.exe>` and open the
   generated `winget/<v>/` folder as a PR to `microsoft/winget-pkgs`.
