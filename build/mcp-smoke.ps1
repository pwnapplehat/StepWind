# End-to-end smoke test of the StepWind MCP server as a real stdio process: launch it, speak
# JSON-RPC (initialize -> initialized -> tools/list), and assert it advertises the expected tools.
# This exercises the actual shipped process + transport, not just the tool methods in-proc.
#
#   ./build/mcp-smoke.ps1 -Exe dist/win-x64/StepWind.Mcp.exe
#
# Exits 0 on success, non-zero on failure. Does NOT require the service to be running: tools/list
# is answered by the server itself; only tool *calls* would round-trip to the pipe.
param(
    [string]$Exe
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

if (-not $Exe) {
    $candidates = @(
        (Join-Path $root "dist\win-x64\StepWind.Mcp.exe"),
        (Join-Path $root "src\StepWind.Mcp\bin\Release\net10.0-windows\StepWind.Mcp.exe"),
        (Join-Path $root "src\StepWind.Mcp\bin\Debug\net10.0-windows\StepWind.Mcp.exe")
    )
    $Exe = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $Exe -or -not (Test-Path $Exe)) { throw "StepWind.Mcp.exe not found. Build/publish first, or pass -Exe." }

Write-Host "MCP smoke: $Exe" -ForegroundColor Cyan

# Minimal JSON-RPC handshake + tools/list, one message per line (the stdio transport is line-based).
$nl = "`n"
$requests = (@(
    '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"smoke","version":"1.0"}}}'
    '{"jsonrpc":"2.0","method":"notifications/initialized"}'
    '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}'
) -join $nl) + $nl

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $Exe
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.UseShellExecute = $false

$proc = [System.Diagnostics.Process]::Start($psi)
$proc.StandardInput.Write($requests)
$proc.StandardInput.Flush()
# Keep stdin OPEN — closing it makes the stdio transport hit EOF and shut down before it has
# answered. Read responses as they stream in, stop once tools/list has come back, then kill.
$sb = New-Object System.Text.StringBuilder
$deadline = (Get-Date).AddSeconds(12)
while ((Get-Date) -lt $deadline) {
    $lineTask = $proc.StandardOutput.ReadLineAsync()
    if ($lineTask.Wait(1000)) {
        $line = $lineTask.Result
        if ($null -eq $line) { break }   # server exited / stdout closed
        [void]$sb.AppendLine($line)
        if ($line -match 'stepwind_undo_operations') { break }   # the last-registered tool arrived
    }
}
$stdout = $sb.ToString()
try { $proc.StandardInput.Close() } catch {}
try { if (-not $proc.HasExited) { $proc.Kill() } } catch {}

$expected = @(
    "stepwind_get_status", "stepwind_list_timeline", "stepwind_browse", "stepwind_get_file_history",
    "stepwind_read_version", "stepwind_diff_versions", "stepwind_checkpoint_file",
    "stepwind_restore_version", "stepwind_undo_operation", "stepwind_recent_files",
    "stepwind_undo_operations"
)
$missing = $expected | Where-Object { $stdout -notmatch [regex]::Escape($_) }

if ($missing.Count -gt 0) {
    Write-Host "MCP smoke FAILED - missing tools: $($missing -join ', ')" -ForegroundColor Red
    Write-Host "----- server output -----"
    Write-Host $stdout
    exit 1
}

Write-Host "MCP smoke PASS - server advertised all $($expected.Count) tools over stdio." -ForegroundColor Green
exit 0
