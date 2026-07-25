# End-to-end smoke test of the StepWind MCP server as a real stdio process: launch it, speak
# JSON-RPC (initialize -> initialized -> tools/list), assert it advertises the expected tools, then
# CALL every tool it advertised.
#
#   ./build/mcp-smoke.ps1 -Exe dist/win-x64/StepWind.Mcp.exe
#
# Why it calls them: this script used to stop at tools/list, and a tool that was advertised but
# refused on every invocation therefore passed CI and shipped. stepwind_list_protected_folders
# needed IpcCommand.GetSettings, the gateway's allow-list did not include it, and every call died
# with "not permitted to call 'GetSettings'". Advertising a tool proves nothing about calling it.
#
# The service does NOT need to be running. The allow-list is enforced inside the MCP process
# BEFORE the named pipe is touched, so a refusal shows up with no service present, while a genuine
# "service is not running" answer means the call got all the way through and is treated as a pass.
#
# Arguments are generated from each tool's own inputSchema, so a newly added tool is covered
# without touching this file. Required strings get an obvious non-existent placeholder, which is
# what makes calling the mutating tools safe: there is no version, operation or file by that name,
# so restore/undo/checkpoint have nothing they could alter.
#
# Exits 0 on success, non-zero on failure.
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
# Absolute: Process.Start resolves a relative path against the PROCESS working directory, not the
# shell's current location, so a relative -Exe fails with a confusing "cannot find the file".
$Exe = (Resolve-Path $Exe).Path

Write-Host "MCP smoke: $Exe" -ForegroundColor Cyan

$PLACEHOLDER = "__stepwind_smoke_does_not_exist__"

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $Exe
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.UseShellExecute = $false
$proc = [System.Diagnostics.Process]::Start($psi)

# Keep stdin OPEN for the whole run: closing it makes the stdio transport hit EOF and shut down
# before it has answered.
function Send-Rpc {
    param([string]$Json)
    $proc.StandardInput.Write($Json + "`n")
    $proc.StandardInput.Flush()
}

# Responses are one JSON object per line, but notifications and log lines can be interleaved, so
# read until the id being waited on shows up.
function Read-Rpc {
    param([int]$Id, [int]$TimeoutSec = 20)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        $task = $proc.StandardOutput.ReadLineAsync()
        if (-not $task.Wait(1000)) { continue }
        $line = $task.Result
        if ($null -eq $line) { return $null }        # stdout closed: server gone
        if (-not $line.Trim()) { continue }
        try { $obj = $line | ConvertFrom-Json } catch { continue }
        if ($null -ne $obj.id -and [int]$obj.id -eq $Id) { return $obj }
    }
    return $null
}

function Stop-Server {
    try { $proc.StandardInput.Close() } catch {}
    try { if (-not $proc.HasExited) { $proc.Kill() } } catch {}
}

# ── handshake + tools/list ────────────────────────────────────────────────────────────────────
Send-Rpc '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"smoke","version":"1.0"}}}'
if (-not (Read-Rpc -Id 1)) { Stop-Server; Write-Host "MCP smoke FAILED - no initialize response" -ForegroundColor Red; exit 1 }
Send-Rpc '{"jsonrpc":"2.0","method":"notifications/initialized"}'
Send-Rpc '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}'
$list = Read-Rpc -Id 2
if (-not $list) { Stop-Server; Write-Host "MCP smoke FAILED - no tools/list response" -ForegroundColor Red; exit 1 }

$tools = $list.result.tools
$advertised = @($tools | ForEach-Object { $_.name })

$expected = @(
    "stepwind_get_status", "stepwind_list_timeline", "stepwind_list_protected_folders",
    "stepwind_browse", "stepwind_recent_files", "stepwind_get_file_history",
    "stepwind_read_version", "stepwind_diff_versions", "stepwind_checkpoint_file",
    "stepwind_restore_version", "stepwind_undo_operation", "stepwind_undo_operations"
)
$missing = $expected | Where-Object { $advertised -notcontains $_ }
if ($missing.Count -gt 0) {
    Stop-Server
    Write-Host "MCP smoke FAILED - missing tools: $($missing -join ', ')" -ForegroundColor Red
    Write-Host "advertised: $($advertised -join ', ')"
    exit 1
}
Write-Host "  tools/list: all $($expected.Count) tools advertised" -ForegroundColor DarkGray

# ── call every advertised tool ────────────────────────────────────────────────────────────────
$id = 100
$refused = @()
$noAnswer = @()

foreach ($tool in $tools) {
    $args = @{}
    $schema = $tool.inputSchema
    if ($schema -and $schema.required) {
        foreach ($name in $schema.required) {
            $type = "string"
            if ($schema.properties -and $schema.properties.$name -and $schema.properties.$name.type) {
                $type = $schema.properties.$name.type
            }
            switch ($type) {
                "integer" { $args[$name] = 1 }
                "number"  { $args[$name] = 1 }
                "boolean" { $args[$name] = $false }
                default   { $args[$name] = $PLACEHOLDER }
            }
        }
    }

    $id++
    $payload = @{
        jsonrpc = "2.0"; id = $id; method = "tools/call"
        params  = @{ name = $tool.name; arguments = $args }
    } | ConvertTo-Json -Depth 8 -Compress
    Send-Rpc $payload

    $resp = Read-Rpc -Id $id
    if (-not $resp) { $noAnswer += $tool.name; continue }

    # Any mention of the gateway's refusal means the tool is wired to a command the allow-list
    # does not include: the tool can never work, for anyone.
    $text = ($resp | ConvertTo-Json -Depth 12 -Compress)
    if ($text -match "not permitted to call") {
        $refused += $tool.name
        Write-Host "  $($tool.name): REFUSED by allow-list" -ForegroundColor Red
    }
    else {
        Write-Host "  $($tool.name): reached the gateway" -ForegroundColor DarkGray
    }
}

Stop-Server

if ($refused.Count -gt 0 -or $noAnswer.Count -gt 0) {
    if ($refused.Count -gt 0) {
        Write-Host "MCP smoke FAILED - allow-list refuses: $($refused -join ', ')" -ForegroundColor Red
        Write-Host "Add the IpcCommand these tools use to StepWindGateway.Allowed." -ForegroundColor Yellow
    }
    if ($noAnswer.Count -gt 0) {
        Write-Host "MCP smoke FAILED - no response from: $($noAnswer -join ', ')" -ForegroundColor Red
    }
    exit 1
}

Write-Host "MCP smoke PASS - all $($expected.Count) tools advertised AND callable end to end." -ForegroundColor Green
exit 0
