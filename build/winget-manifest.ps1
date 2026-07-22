# Generates a winget manifest set for a built StepWind installer, ready to submit to
# microsoft/winget-pkgs (the free, official Windows package repo). Run after building the
# installer for a tagged release:
#
#   ./build/winget-manifest.ps1 -Version 1.0.0 -InstallerPath installer/Output/StepWind-1.0.0-setup.exe
#
# It computes the real SHA-256 from the actual file (no placeholder hashes) and writes the three
# required YAML files under winget/<Version>/. Submit that folder as a PR to microsoft/winget-pkgs.
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$InstallerPath,
    [string]$OutDir
)
$ErrorActionPreference = "Stop"

if (-not (Test-Path $InstallerPath)) { throw "installer not found: $InstallerPath" }

$root = Split-Path -Parent $PSScriptRoot
if (-not $OutDir) { $OutDir = Join-Path $root "winget\$Version" }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$sha = (Get-FileHash $InstallerPath -Algorithm SHA256).Hash.ToUpper()
$id = "StepWind.StepWind"
$installerUrl = "https://github.com/pwnapplehat/StepWind/releases/download/v$Version/$([System.IO.Path]::GetFileName($InstallerPath))"

Set-Content (Join-Path $OutDir "$id.installer.yaml") @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.installer.1.6.0.schema.json
PackageIdentifier: $id
PackageVersion: $Version
InstallerLocale: en-US
InstallerType: inno
Scope: machine
InstallModes:
  - interactive
  - silent
  - silentWithProgress
UpgradeBehavior: install
Installers:
  - Architecture: x64
    InstallerUrl: $installerUrl
    InstallerSha256: $sha
ManifestType: installer
ManifestVersion: 1.6.0
"@

Set-Content (Join-Path $OutDir "$id.locale.en-US.yaml") @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.defaultLocale.1.6.0.schema.json
PackageIdentifier: $id
PackageVersion: $Version
PackageLocale: en-US
Publisher: StepWind Contributors
PublisherUrl: https://github.com/pwnapplehat/StepWind
PackageName: StepWind
PackageUrl: https://stepwind.app
License: MIT
LicenseUrl: https://github.com/pwnapplehat/StepWind/blob/main/LICENSE
ShortDescription: An undo button for your whole PC — real-time protection against accidental moves, renames, deletes, and bad saves.
Tags:
  - backup
  - versioning
  - undo
  - file-history
  - recovery
ManifestType: defaultLocale
ManifestVersion: 1.6.0
"@

Set-Content (Join-Path $OutDir "$id.yaml") @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.version.1.6.0.schema.json
PackageIdentifier: $id
PackageVersion: $Version
DefaultLocale: en-US
ManifestType: version
ManifestVersion: 1.6.0
"@

Write-Host "winget manifest for $Version written to $OutDir (installer SHA-256 $sha)" -ForegroundColor Green
Write-Host "Submit that folder as a PR to https://github.com/microsoft/winget-pkgs" -ForegroundColor Cyan
