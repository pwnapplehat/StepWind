# Installs the exact Inno Setup build that installer\stepwind.iss is developed and tested against.
#
#   ./build/install-innosetup.ps1
#
# Why pinned instead of "choco install innosetup":
#   * The wizard's dark theme, WizardBackImageFile and WizardBackColor are 6.6/6.7 features. An
#     older compiler is a silent downgrade to the stock grey wizard, so the version is a build
#     input, not an incidental. (stepwind.iss also #errors below 6.7 as a second line of defence.)
#   * An unpinned package manager makes the toolchain float, so two builds of the same commit can
#     differ. StepWind verifies checksums and signatures of what it ships; the tool that produces
#     the installer deserves the same treatment.
#
# The download is verified twice: SHA-256 against the pin below, and Authenticode against the
# publisher jrsoftware.org documents for this release. Either failing aborts the build.
param(
    [string]$Version = "6.7.3",
    [string]$Sha256 = "9c73c3bae7ed48d44112a0f48e66742c00090bdb5bef71d9d3c056c66e97b732",
    [string]$ExpectedSigner = "Pyrsys B.V."
)
$ErrorActionPreference = "Stop"

$tag = "is-" + ($Version -replace '\.', '_')
$url = "https://github.com/jrsoftware/issrc/releases/download/$tag/innosetup-$Version.exe"
$installer = Join-Path $env:TEMP "innosetup-$Version.exe"
$target = Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6"

Write-Host "Inno Setup $Version <- $url" -ForegroundColor Cyan
Invoke-WebRequest $url -OutFile $installer -UseBasicParsing -TimeoutSec 300

$actual = (Get-FileHash $installer -Algorithm SHA256).Hash.ToLower()
if ($actual -ne $Sha256.ToLower()) {
    throw "Inno Setup SHA-256 mismatch.`n  expected $Sha256`n  actual   $actual`nRefusing to install an unexpected build. If the pin is genuinely out of date, verify the new release by hand and update -Sha256."
}
Write-Host "  sha256 OK" -ForegroundColor DarkGray

$sig = Get-AuthenticodeSignature $installer
if ($sig.Status -ne "Valid") {
    throw "Inno Setup installer signature is $($sig.Status), expected Valid."
}
if ($sig.SignerCertificate.Subject -notlike "*$ExpectedSigner*") {
    throw "Inno Setup installer signed by an unexpected publisher: $($sig.SignerCertificate.Subject)"
}
Write-Host "  signature OK ($($sig.SignerCertificate.Subject))" -ForegroundColor DarkGray

# /PORTABLE=0 gives a normal per-machine install; ISCC does not land on PATH by itself, which is
# why the directory is exported below rather than assumed.
$p = Start-Process $installer -ArgumentList "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/ALLUSERS" -Wait -PassThru
if ($p.ExitCode -ne 0) { throw "Inno Setup installer exited with $($p.ExitCode)." }

$iscc = Join-Path $target "ISCC.exe"
if (-not (Test-Path $iscc)) {
    # Fall back to a search in case a future release changes its default directory.
    $iscc = Get-ChildItem "${env:ProgramFiles(x86)}", $env:ProgramFiles -Filter ISCC.exe -Recurse -ErrorAction SilentlyContinue |
        Select-Object -First 1 -ExpandProperty FullName
}
if (-not $iscc) { throw "ISCC.exe not found after installing Inno Setup $Version." }

$isccDir = Split-Path -Parent $iscc
# Make `iscc` resolvable in later CI steps (and in this session for a local run).
if ($env:GITHUB_PATH) { Add-Content -Path $env:GITHUB_PATH -Value $isccDir }
$env:PATH = "$isccDir;$env:PATH"

$reported = (& $iscc | Select-String -Pattern 'Inno Setup \d' | Select-Object -First 1).Line
Write-Host "Inno Setup ready: $iscc" -ForegroundColor Green
if ($reported) { Write-Host "  $($reported.Trim())" -ForegroundColor DarkGray }
