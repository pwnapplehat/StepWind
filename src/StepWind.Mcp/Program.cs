using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using StepWind.Mcp;

// StepWind's MCP server: an unelevated, on-demand stdio process — the AI client (Cursor, Claude
// Desktop, etc.) launches a fresh instance of THIS process per session and talks to it over its
// stdin/stdout. It never touches the filesystem directly; every tool call is a named-pipe round
// trip to the already-running, elevated StepWind service (StepWind.Mcp.Tools.StepWindTools →
// StepWindGateway → StepWind.Core.Ipc.PipeClient), the exact same pattern the tray GUI uses.
// No new privileges, no new service — just another unelevated client of the existing pipe.
//
// EMPTY builder, deliberately: Host.CreateApplicationBuilder wires appsettings.json providers
// with reload-on-change FILE WATCHERS rooted at the process's working directory — and MCP
// clients spawn this exe with THEIR cwd (Cursor: the user's folder). Measured live via ETW:
// that watcher stat-ed every file that changed near the cwd, which both wasted IO and made
// StepWind.Mcp look like it was touching files it never touched (it poisoned the timeline's
// process attribution before the attribution rules also learned to ignore observers). A stdio
// server needs no config files, so it gets a builder that watches nothing.
var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { Args = args });

// CRITICAL for stdio MCP servers: stdout carries ONLY the JSON-RPC protocol stream. Any stray
// text there (a stray Console.WriteLine, or default console logging) corrupts every message
// after it. All logging must go to stderr instead.
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton<StepWindGateway>();

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new Implementation
        {
            Name = "StepWind",
            Version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "1.0.0",
        };
        options.ServerInstructions =
            "StepWind protects folders with continuous version history and records file " +
            "operations (move/rename/delete) across the whole machine. Typical workflow before " +
            "a risky change: call stepwind_checkpoint_file, make the edit, call " +
            "stepwind_diff_versions with 'latest:path' and 'current:path' to see exactly what " +
            "changed, and stepwind_restore_version to undo if needed (restores never overwrite " +
            "— they land beside the current file). Use stepwind_browse to find a file's exact " +
            "relative path first if you don't already know it. Only paths inside a protected " +
            "folder (see stepwind_list_protected_folders) have content history; everywhere else " +
            "only move/rename/delete show up, in stepwind_list_timeline.";
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
