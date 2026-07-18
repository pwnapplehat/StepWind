# StepWind publish — self-contained service + GUI + CLI into dist\<runtime>.
param([string]$Runtime = "win-x64", [string]$Configuration = "Release")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root "dist\$Runtime"

Write-Host "Publishing StepWind ($Configuration, $Runtime)..." -ForegroundColor Cyan
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Force -Path $dist | Out-Null

$projects = @(
    @{ Path = "src\StepWind.App\StepWind.App.csproj";     Single = $true  },
    @{ Path = "src\StepWind.Service\StepWind.Service.csproj"; Single = $false },
    @{ Path = "src\StepWind.Cli\StepWind.Cli.csproj";     Single = $true  }
)
foreach ($p in $projects) {
    dotnet publish (Join-Path $root $p.Path) -c $Configuration -r $Runtime --self-contained true `
        -p:PublishSingleFile=$($p.Single) -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=$($p.Single) -p:DebugType=none -o $dist
    if ($LASTEXITCODE -ne 0) { throw "publish failed: $($p.Path)" }
}

Copy-Item (Join-Path $root "LICENSE") $dist -Force
Copy-Item (Join-Path $root "install.ps1") $dist -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path $root "README.md") $dist -Force -ErrorAction SilentlyContinue

Get-ChildItem $dist -File | Where-Object { $_.Extension -in '.exe' } |
    ForEach-Object { Write-Host ("  {0,-26} {1,8:N1} MB" -f $_.Name, ($_.Length/1MB)) }
Write-Host "`nDone -> $dist" -ForegroundColor Green
