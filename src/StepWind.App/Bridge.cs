using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using StepWind.Core.Engine;
using StepWind.Core.Integration;
using StepWind.Core.Ipc;

namespace StepWind.App;

/// <summary>
/// The single doorway between the web UI and the rest of the machine. Two rules:
///
///  1. ALLOW-LIST, not pass-through — the web layer can only invoke what is explicitly
///     listed here, and settings patches only carry explicitly allowed keys. The web
///     content is ours and local, but the boundary is designed as if it weren't.
///  2. Same capabilities as the old WPF GUI, no more — everything here is something the
///     unelevated GUI could already do: talk to the service pipe as the user, show
///     pickers, copy to clipboard, open Explorer/browser.
///
/// Wire shape: web posts {id, method, params}; host replies {id, ok, data|error}. The host
/// can also push unsolicited events ({type: ...}) — currently just window state.
/// </summary>
public sealed class Bridge(MainWindow window)
{
    private readonly PipeClient _pipe = new();

    /// <summary>
    /// Serializes pipe calls. The service handles one pipe connection at a time, and unlike
    /// the old (inherently sequential) WPF view-model, the web UI issues calls concurrently —
    /// racing them into the server's single accept loop made bursts eat into the 5s connect
    /// timeout and read as "service is not available". Queuing client-side keeps every call
    /// fast and ordered.
    /// </summary>
    private readonly SemaphoreSlim _pipeGate = new(1, 1);

    /// <summary>Settings keys the web layer may patch. Anything else is rejected.</summary>
    private static readonly HashSet<string> AllowedSettingsKeys = new(StringComparer.Ordinal)
    {
        "WatchedFolders", "ExcludedPrefixes", "AutoUpdateEnabled", "EncryptionEnabled",
        "FlightRecorderEnabled", "TimelineProtectedOnly", "RespectGitIgnore",
        "MinFreeDiskBytes", "MaxStoreBytes",
        "RetentionKeepAllHours",
        "RetentionHourlyDays", "RetentionDailyDays", "RetentionMaxAgeDays", "RetentionMaxVersionsPerFile",
    };

    public void NotifyWindowState(bool maximized) =>
        window.PostToWeb(JsonSerializer.Serialize(new { type = "winstate", maximized }));

    public async void HandleMessage(string messageJson)
    {
        long id = 0;
        try
        {
            JsonNode msg = JsonNode.Parse(messageJson)!;
            id = msg["id"]?.GetValue<long>() ?? 0;
            string method = msg["method"]?.GetValue<string>() ?? "";
            JsonNode? p = msg["params"];

            JsonNode? data = await DispatchAsync(method, p);
            window.PostToWeb(new JsonObject { ["id"] = id, ["ok"] = true, ["data"] = data }.ToJsonString());
        }
        catch (Exception ex)
        {
            window.PostToWeb(new JsonObject { ["id"] = id, ["ok"] = false, ["error"] = ex.Message }.ToJsonString());
        }
    }

    private async Task<JsonNode?> DispatchAsync(string method, JsonNode? p) => method switch
    {
        // ── window chrome ──
        "win" => DoWindowAction(p?["action"]?.GetValue<string>()),
        "chromeTheme" => SetChromeTheme(p?["theme"]?.GetValue<string>()),

        // ── service pipe: reads ──
        "status" => await PipeAsync(IpcCommand.GetStatus),
        "settings" => await PipeAsync(IpcCommand.GetSettings),
        "timeline" => await PipeAsync(IpcCommand.GetTimeline, limit: p?["limit"]?.GetValue<int>() ?? 250),
        "recent" => await PipeAsync(IpcCommand.GetRecentFiles, limit: p?["limit"]?.GetValue<int>() ?? 60),
        "browse" => await PipeAsync(IpcCommand.BrowseVersions,
            p?["path"]?.GetValue<string>() ?? "", p?["query"]?.GetValue<string>(), 500),
        "history" => await PipeAsync(IpcCommand.GetHistory, Require(p, "relativePath")),
        "read" => await PipeAsync(IpcCommand.GetVersionContent, Require(p, "selector")),
        "diff" => await PipeAsync(IpcCommand.DiffVersions, Require(p, "oldSel"), Require(p, "newSel")),

        // ── service pipe: actions ──
        "undo" => await PipeAsync(IpcCommand.ReverseOperation, Require(p, "operationId")),
        "undoBatch" => await PipeAsync(IpcCommand.ReverseBatch, Require(p, "operationIds")),
        "restore" => await PipeAsync(IpcCommand.RestoreVersion, Require(p, "versionId")),
        "runRetention" => await PipeAsync(IpcCommand.RunRetention),
        "purge" => await PipeAsync(IpcCommand.PurgeHistory, Require(p, "selector")),
        "verifyStore" => await PipeAsync(IpcCommand.VerifyStore, p?["deep"]?.GetValue<bool>() == true ? "deep" : null),
        "repairStore" => await PipeAsync(IpcCommand.RepairStore, p?["deep"]?.GetValue<bool>() == true ? "deep" : null),
        "relocateStore" => await RelocateStoreAsync(),
        "patch" => await PatchSettingsAsync(p),

        // ── host: AI agents ──
        "agents" => DetectAgents(),
        "agentConnect" => await AgentActionAsync(Require(p, "id"), connect: true),
        "agentDisconnect" => await AgentActionAsync(Require(p, "id"), connect: false),
        "mcpInfo" => McpInfo(),

        // ── host: updates ──
        "installUpdate" => InstallUpdate(Require(p, "path")),

        // ── host: diagnostics ──
        "exportDiagnostics" => await ExportDiagnosticsAsync(),

        // ── host: shell helpers ──
        "pickFolder" => PickFolder(p?["title"]?.GetValue<string>()),
        "pickFile" => PickFile(),
        "openPath" => OpenInExplorer(p?["path"]?.GetValue<string>()),
        "openBackups" => OpenBackups(),
        "openUrl" => OpenUrl(Require(p, "url")),
        "copyText" => CopyText(Require(p, "text")),
        "appInfo" => AppInfo(),

        _ => throw new InvalidOperationException($"unknown bridge method '{method}'"),
    };

    private static string Require(JsonNode? p, string key) =>
        p?[key]?.GetValue<string>() is { Length: > 0 } v
            ? v
            : throw new ArgumentException($"missing required parameter '{key}'");

    // ─────────────────────────────── pipe plumbing ───────────────────────────────

    private async Task<JsonNode?> PipeAsync(IpcCommand cmd, string? a1 = null, string? a2 = null, int limit = 200)
    {
        IpcResponse resp;
        await _pipeGate.WaitAsync();
        try
        {
            resp = await _pipe.SendAsync(new IpcRequest { Command = cmd, Arg1 = a1, Arg2 = a2, Limit = limit });
        }
        finally
        {
            _pipeGate.Release();
        }

        if (!resp.Ok)
        {
            throw new InvalidOperationException(resp.Error ?? "The StepWind service did not respond.");
        }

        return resp.Json is null ? null : JsonNode.Parse(resp.Json);
    }

    private async Task<JsonNode?> PatchSettingsAsync(JsonNode? p)
    {
        if (p?["patch"] is not JsonObject patch)
        {
            throw new ArgumentException("missing settings patch");
        }

        foreach ((string key, JsonNode? _) in patch)
        {
            if (!AllowedSettingsKeys.Contains(key))
            {
                throw new InvalidOperationException($"settings key '{key}' is not writable from the UI");
            }
        }

        return await PipeAsync(IpcCommand.SetSettings, patch.ToJsonString());
    }

    /// <summary>
    /// First-run seeding (host-side so it happens regardless of which view opens first).
    /// The SYSTEM service can't see the user's real folders; the GUI supplies them exactly
    /// once. FirstRunCompleted flips permanently on any human folder decision, so removed
    /// folders never come back on their own.
    /// </summary>
    public async Task SeedDefaultFoldersOnFirstRunAsync()
    {
        try
        {
            JsonNode? status = await PipeAsync(IpcCommand.GetStatus);
            JsonNode? settings = await PipeAsync(IpcCommand.GetSettings);
            int roots = status?["WatchedRoots"]?.GetValue<int>() ?? -1;
            bool firstRunDone = settings?["FirstRunCompleted"]?.GetValue<bool>() ?? true;
            if (roots != 0 || firstRunDone)
            {
                return;
            }

            List<string> defaults = StepWindSettings.DefaultUserFolders();
            if (defaults.Count > 0)
            {
                await PipeAsync(IpcCommand.SetSettings,
                    JsonSerializer.Serialize(new { WatchedFolders = defaults }));
            }
        }
        catch
        {
            // Service down — the UI already shows "not protecting"; seeding retries next launch.
        }
    }

    // ─────────────────────────────── AI agents ───────────────────────────────

    private static string McpServerPath => McpInstaller.ResolveServerExe(Path.Combine(
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory, "StepWind.Mcp.exe"));

    private static JsonNode DetectAgents()
    {
        string exe = McpServerPath;
        var arr = new JsonArray();
        foreach (McpClientTarget t in McpInstaller.DetectAll())
        {
            bool stale = t.Configured && t.ConfiguredCommand is { Length: > 0 } cmd &&
                         !string.Equals(cmd, exe, StringComparison.OrdinalIgnoreCase);
            arr.Add(new JsonObject
            {
                ["id"] = t.Id,
                ["name"] = t.DisplayName,
                ["configPath"] = t.ConfigPath,
                ["detected"] = t.Installed,
                ["connected"] = t.Configured,
                ["needsRepair"] = stale,
                ["note"] = t.Note,
            });
        }
        return arr;
    }

    private static async Task<JsonNode> AgentActionAsync(string targetId, bool connect)
    {
        McpClientTarget target = McpInstaller.DetectAll().FirstOrDefault(t => t.Id == targetId)
            ?? throw new InvalidOperationException($"unknown AI tool '{targetId}'");

        McpInstallResult result;
        if (connect)
        {
            string exe = McpServerPath;
            result = File.Exists(exe)
                ? await Task.Run(() => McpInstaller.Install(target, exe))
                : new McpInstallResult(false,
                    $"StepWind.Mcp.exe was not found next to the app ({exe}). Repair the StepWind installation first — nothing was changed.");
        }
        else
        {
            result = await Task.Run(() => McpInstaller.Remove(target));
        }

        return new JsonObject { ["ok"] = result.Ok, ["message"] = result.Message };
    }

    private static JsonNode McpInfo() => new JsonObject
    {
        ["serverPath"] = McpServerPath,
        ["snippet"] = "{\n  \"mcpServers\": {\n    \"stepwind\": {\n      \"command\": "
                      + JsonSerializer.Serialize(McpServerPath) + "\n    }\n  }\n}",
    };

    // ─────────────────────────────── shell helpers ───────────────────────────────

    /// <summary>
    /// Matches the native window frame to the web theme: the 6px resize border (Window
    /// background) and the WebView's pre-paint color. Without this a light UI shows a thin
    /// dark strip when not maximized, and a dark flash on load after switching to light.
    /// </summary>
    private JsonNode? SetChromeTheme(string? theme)
    {
        window.RunOnUi(() => window.SetChromeTheme(theme == "light"));
        return null;
    }

    private JsonNode? DoWindowAction(string? action)
    {
        window.RunOnUi(() =>
        {
            switch (action)
            {
                case "minimize": window.WebMinimize(); break;
                case "maximize": window.WebMaximizeRestore(); break;
                case "close": window.WebClose(); break;
            }
        });
        return null;
    }

    /// <summary>
    /// Launches a verified, service-staged update installer. The GUI is unelevated, so starting
    /// the (admin-manifested) setup triggers a normal UAC prompt the user consents to — this is
    /// the FREE path that gives auto-update without silently running an unsigned binary as SYSTEM.
    /// The path is validated to live inside StepWind's ACL-locked staging dir, so the web layer
    /// can only ever launch what the service put there.
    /// </summary>
    private JsonNode? InstallUpdate(string path)
    {
        string stagingDir = System.IO.Path.GetFullPath(StepWindSettings.DefaultUpdatesDir);
        string full = System.IO.Path.GetFullPath(path);
        bool inStaging = full.StartsWith(stagingDir + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (!inStaging || !full.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
        {
            throw new InvalidOperationException("That update installer isn't a staged StepWind setup.");
        }

        // UseShellExecute lets Windows apply the installer's admin manifest → UAC prompt.
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(full) { UseShellExecute = true });
        return null;
    }

    /// <summary>
    /// Gathers a support bundle — settings, status, store-integrity summary, detected AI tools,
    /// versions, OS/runtime, and the installer/update log tail — and saves it to a file the user
    /// picks. Deliberately contains NO file contents and no version data: only configuration and
    /// health, so it's safe to attach to a bug report. Returns the written path (or null if cancelled).
    /// </summary>
    private async Task<JsonNode?> ExportDiagnosticsAsync()
    {
        JsonNode? status = await SafePipe(IpcCommand.GetStatus);
        JsonNode? settings = await SafePipe(IpcCommand.GetSettings);
        JsonNode? storeCheck = await SafePipe(IpcCommand.VerifyStore);

        var agents = new JsonArray();
        foreach (McpClientTarget t in McpInstaller.DetectAll())
        {
            agents.Add(new JsonObject { ["name"] = t.DisplayName, ["detected"] = t.Installed, ["connected"] = t.Configured });
        }

        var bundle = new JsonObject
        {
            ["generatedUtc"] = DateTime.UtcNow.ToString("u"),
            ["appVersion"] = typeof(Bridge).Assembly.GetName().Version?.ToString() ?? "unknown",
            ["os"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            ["runtime"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            ["architecture"] = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
            ["status"] = status,
            ["settings"] = settings,
            ["storeCheck"] = storeCheck,
            ["aiAgents"] = agents,
            ["updateLog"] = TailFile(Path.Combine(StepWindSettings.DefaultRoot, "logs", "update-install.log"), 100),
            ["note"] = "Contains configuration and health only — no file names, paths inside protected folders, or file contents.",
        };

        string json = bundle.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

        string? savePath = null;
        window.RunOnUi(() =>
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save StepWind diagnostics",
                FileName = $"stepwind-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.json",
                Filter = "JSON diagnostics (*.json)|*.json",
            };
            if (dialog.ShowDialog() == true)
            {
                savePath = dialog.FileName;
            }
        });

        if (savePath is null)
        {
            return null; // cancelled
        }

        await File.WriteAllTextAsync(savePath, json);
        return JsonValue.Create(savePath);
    }

    /// <summary>Pipe call that returns null instead of throwing, so one down subsystem can't abort the whole bundle.</summary>
    private async Task<JsonNode?> SafePipe(IpcCommand cmd)
    {
        try { return await PipeAsync(cmd); }
        catch (Exception ex) { return new JsonObject { ["error"] = ex.Message }; }
    }

    private static JsonNode? TailFile(string path, int maxLines)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            string[] lines = File.ReadAllLines(path);
            return string.Join('\n', lines.TakeLast(maxLines));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Picks a destination folder and asks the service to move the history store there (admin).</summary>
    private async Task<JsonNode?> RelocateStoreAsync()
    {
        string? picked = null;
        window.RunOnUi(() =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a new (empty) folder for your version history" };
            if (dialog.ShowDialog() == true)
            {
                picked = dialog.FolderName;
            }
        });

        return picked is null ? null : await PipeAsync(IpcCommand.RelocateStore, picked);
    }

    private JsonNode? PickFolder(string? title)
    {
        string? picked = null;
        window.RunOnUi(() =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = title ?? "Choose a folder to protect" };
            if (dialog.ShowDialog() == true)
            {
                picked = dialog.FolderName;
            }
        });
        return picked is null ? null : JsonValue.Create(picked);
    }

    private JsonNode? PickFile()
    {
        string? picked = null;
        window.RunOnUi(() =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Open a file to see its version history",
                CheckFileExists = false, // deleted files still have history
            };
            if (dialog.ShowDialog() == true)
            {
                picked = dialog.FileName;
            }
        });
        return picked is null ? null : JsonValue.Create(picked);
    }

    private static JsonNode? OpenInExplorer(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && (File.Exists(path) || Directory.Exists(path)))
        {
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
        return null;
    }

    private static JsonNode? OpenBackups()
    {
        Directory.CreateDirectory(McpInstaller.BackupRoot);
        System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo(McpInstaller.BackupRoot) { UseShellExecute = true });
        return null;
    }

    /// <summary>Only StepWind's own destinations — the web layer can't open arbitrary URLs.</summary>
    private static JsonNode? OpenUrl(string url)
    {
        bool allowed = url is "https://stepwind.app" or "https://github.com/pwnapplehat/StepWind";
        if (!allowed)
        {
            throw new InvalidOperationException("URL not in the allow-list");
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        return null;
    }

    private JsonNode? CopyText(string text)
    {
        window.RunOnUi(() => Clipboard.SetText(text));
        return null;
    }

    private static JsonNode AppInfo() => new JsonObject
    {
        ["version"] = typeof(Bridge).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.0.0",
    };
}
