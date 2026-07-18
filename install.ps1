# StepWind installer (run elevated). Registers + starts the background service (LocalSystem,
# for USN journal + ETW), and sets the GUI to start with Windows (unelevated). Portable:
# run from wherever the published files live.
#   .\install.ps1            install + start service, enable GUI autostart
#   .\install.ps1 -Uninstall stop + remove service, remove autostart
param([switch]$Uninstall)

$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$serviceExe = Join-Path $here "StepWind.Service.exe"
$guiExe = Join-Path $here "StepWind.exe"
$serviceName = "StepWind"
$runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"

function Assert-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)) {
        throw "Please run this installer from an elevated (Administrator) PowerShell."
    }
}
Assert-Admin

if ($Uninstall) {
    Write-Host "Removing StepWind service..." -ForegroundColor Cyan
    & sc.exe stop $serviceName | Out-Null
    Start-Sleep 2
    & sc.exe delete $serviceName | Out-Null
    Remove-ItemProperty -Path $runKey -Name "StepWind" -ErrorAction SilentlyContinue
    Write-Host "Uninstalled. (Your version store under %ProgramData%\StepWind was left intact.)" -ForegroundColor Green
    return
}

if (-not (Test-Path $serviceExe)) { throw "StepWind.Service.exe not found next to this script. Run from the published folder." }

Write-Host "Installing StepWind service..." -ForegroundColor Cyan
& sc.exe query $serviceName *> $null
if ($LASTEXITCODE -eq 0) { & sc.exe stop $serviceName | Out-Null; Start-Sleep 2; & sc.exe delete $serviceName | Out-Null; Start-Sleep 1 }

& sc.exe create $serviceName binPath= "`"$serviceExe`"" start= auto DisplayName= "StepWind Protection" obj= "LocalSystem" | Out-Null
& sc.exe description $serviceName "Real-time file protection and version history (USN + ETW flight recorder)." | Out-Null
& sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/5000/restart/5000 | Out-Null
& sc.exe start $serviceName | Out-Null

if (Test-Path $guiExe) {
    Set-ItemProperty -Path $runKey -Name "StepWind" -Value "`"$guiExe`" --minimized"
}

Write-Host "StepWind is installed and running. The tray app starts with Windows." -ForegroundColor Green
