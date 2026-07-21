using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Data;
using System.Windows.Threading;
using StepWind.Core.Engine;
using StepWind.Core.Integration;
using StepWind.Core.Ipc;

namespace StepWind.App.ViewModels;

/// <summary>A row on the operation timeline (flight recorder).</summary>
public sealed class TimelineRow
{
    public required string Kind { get; init; }

    /// <summary>Group header key: "Today", "Yesterday", or "Jul 18".</summary>
    public required string Day { get; init; }

    /// <summary>Clock time within the day ("14:31:07").</summary>
    public required string Time { get; init; }

    public required string Description { get; init; }
    public string? ByProcess { get; init; }
    public bool Reversible { get; init; }
    public required string OperationId { get; init; }

    /// <summary>Raw paths (not displayed) so the "protected folders only" scope can filter.</summary>
    public string? OldPath { get; init; }
    public string? NewPath { get; init; }
}

/// <summary>A row in a file's version history.</summary>
public sealed class VersionRow
{
    public required string When { get; init; }
    public required string SizeText { get; init; }
    public required string Reason { get; init; }
    public required string VersionId { get; init; }
}

/// <summary>A row in the folder-navigable version browser (a subfolder or a file).</summary>
public sealed class BrowseRow
{
    public required string Name { get; init; }
    public required string RelativePath { get; init; }
    public required bool IsFolder { get; init; }
    public required string Detail { get; init; }
    public string Glyph => IsFolder ? "\uE8B7" : "\uE8A5"; // Segoe MDL2: Folder / Document
}

/// <summary>A clickable breadcrumb segment in the version browser.</summary>
public sealed class Crumb
{
    public required string Label { get; init; }
    public required string Path { get; init; }
    public required bool IsLast { get; init; }
}

/// <summary>
/// One AI tool card on the AI agents tab. Immutable — the collection is rebuilt after every
/// install/remove/refresh, matching how the other list views work.
/// </summary>
public sealed class AgentRow
{
    public required McpClientTarget Target { get; init; }

    public string Name => Target.DisplayName;
    public string ConfigPath => Target.ConfigPath;

    /// <summary>The tool appears to be installed on this machine.</summary>
    public bool Detected => Target.Installed;

    /// <summary>Our stepwind entry is present in its config.</summary>
    public bool Connected => Target.Configured;

    /// <summary>Connected, but pointing at a stale exe path (app moved/reinstalled elsewhere).</summary>
    public required bool NeedsRepair { get; init; }

    public required string StatusText { get; init; }

    // The XAML template picks button visibility off these three:
    public bool ShowConnect => Detected && !Connected;
    public bool ShowRepair => Detected && Connected && NeedsRepair;
    public bool ShowDisconnect => Connected;

    /// <summary>Sort weight: actionable first, then connected, then undetected (dimmed).</summary>
    public int SortOrder => Detected ? (Connected ? (NeedsRepair ? 0 : 1) : 0) : 2;
}

/// <summary>
/// Talks to the elevated service over the pipe and drives the window: connection status, the
/// live operation timeline (with one-click reverse), per-file version history (with restore),
/// protected-folder management, and settings. All work is async and failure-tolerant — if the
/// service is down, the UI says so instead of throwing.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly PipeClient _pipe = new();
    private readonly DispatcherTimer _refreshTimer;
    private string _status = "Connecting to StepWind service…";
    private bool _serviceUp;
    private string _historyPath = "";
    private string _historyTitle = DefaultHistoryTitle;
    private string _currentView = "timeline";
    private string _timelineFilter = "All";
    private bool _autoUpdateEnabled = true;
    private bool _encryptionOn;
    private bool _flightRecorderOn;
    private int _protectingCount;
    private long _totalVersions;
    private bool _loadingSettings;

    private const string DefaultHistoryTitle =
        "Pick a file to see every saved version — restore any of them, even after an overwrite or delete.";

    public MainViewModel()
    {
        TimelineView = CollectionViewSource.GetDefaultView(Timeline);
        TimelineView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(TimelineRow.Day)));
        TimelineView.Filter = o =>
        {
            if (o is not TimelineRow r)
            {
                return false;
            }

            if (_timelineFilter != "All" && r.Kind != _timelineFilter)
            {
                return false;
            }

            return !_timelineProtectedOnly || RowIsInProtectedFolder(r);
        };

        BuildBreadcrumbs(); // "Home" is present before the first browse loads

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(3) };
        _refreshTimer.Tick += async (_, _) => await RefreshAsync();
        _refreshTimer.Start();
        _ = RefreshAsync();
    }

    public ObservableCollection<TimelineRow> Timeline { get; } = [];

    public ObservableCollection<VersionRow> History { get; } = [];

    public ObservableCollection<BrowseRow> BrowseEntries { get; } = [];

    public ObservableCollection<Crumb> Breadcrumbs { get; } = [];

    public ObservableCollection<string> WatchedFolders { get; } = [];

    /// <summary>Grouped (by day) + filtered view the timeline list binds to.</summary>
    public ICollectionView TimelineView { get; }

    public string CurrentView
    {
        get => _currentView;
        set { _currentView = value; OnChanged(); }
    }

    /// <summary>"All" or an operation kind name; drives the timeline filter chips.</summary>
    public string TimelineFilter
    {
        get => _timelineFilter;
        set
        {
            _timelineFilter = value;
            OnChanged();
            TimelineView.Refresh();
        }
    }

    private bool _timelineProtectedOnly;

    /// <summary>Timeline scope: false = all drives (full flight recorder), true = protected folders only.</summary>
    public bool TimelineProtectedOnly
    {
        get => _timelineProtectedOnly;
        set
        {
            if (_timelineProtectedOnly == value)
            {
                return;
            }

            _timelineProtectedOnly = value;
            OnChanged();
            TimelineView.Refresh();
            if (!_loadingSettings)
            {
                _ = PushPatchAsync(new { TimelineProtectedOnly = value });
            }
        }
    }

    private bool RowIsInProtectedFolder(TimelineRow row)
    {
        foreach (string root in WatchedFolders)
        {
            string prefix = root.TrimEnd('\\') + "\\";
            if (row.OldPath?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true
                || row.NewPath?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true)
            {
                return true;
            }
        }

        return false;
    }

    // ── Folder-navigable version browser ────────────────────────────────────────────
    private string _browsePath = "";
    private string _browseSearch = "";
    private string _browseFingerprint = "";

    /// <summary>Search box in File versions: non-empty ⇒ recursive file search under the current folder.</summary>
    public string BrowseSearch
    {
        get => _browseSearch;
        set
        {
            if (_browseSearch == value)
            {
                return;
            }

            _browseSearch = value;
            OnChanged();
            _browseFingerprint = ""; // content shape changes → allow rebuild
            _ = RefreshBrowseAsync();
        }
    }

    public string BrowsePathDisplay => _browsePath.Length == 0 ? "All protected folders" : _browsePath;

    public bool CanGoUp => _browsePath.Length > 0;

    /// <summary>Loads the immediate children of a folder (or root) and rebuilds the breadcrumb.</summary>
    public async Task BrowseToAsync(string prefix)
    {
        _browsePath = prefix.Replace('\\', '/').Trim('/');
        _browseSearch = "";
        OnChanged(nameof(BrowseSearch));
        OnChanged(nameof(BrowsePathDisplay));
        OnChanged(nameof(CanGoUp));
        BuildBreadcrumbs();
        _browseFingerprint = "";
        await RefreshBrowseAsync();
    }

    public async Task GoUpAsync()
    {
        if (_browsePath.Length == 0)
        {
            return;
        }

        int slash = _browsePath.LastIndexOf('/');
        await BrowseToAsync(slash < 0 ? "" : _browsePath[..slash]);
    }

    /// <summary>Opens a browser row: drill into a folder, or load a file's history.</summary>
    public async Task OpenBrowseRowAsync(BrowseRow row)
    {
        if (row.IsFolder)
        {
            await BrowseToAsync(row.RelativePath);
        }
        else
        {
            await LoadHistoryAsync(row.RelativePath);
        }
    }

    /// <summary>Re-queries the current browse folder/search; skips the UI rebuild if unchanged.</summary>
    public async Task RefreshBrowseAsync()
    {
        var req = new IpcRequest
        {
            Command = IpcCommand.BrowseVersions,
            Arg1 = _browsePath,
            Arg2 = string.IsNullOrWhiteSpace(_browseSearch) ? null : _browseSearch,
            Limit = 500,
        };
        IpcResponse resp = await _pipe.SendAsync(req);
        if (!resp.Ok || resp.Json is null)
        {
            return;
        }

        if (resp.Json == _browseFingerprint)
        {
            return; // nothing changed — don't disturb the list
        }

        _browseFingerprint = resp.Json;
        BrowseEntry[] entries = JsonSerializer.Deserialize<BrowseEntry[]>(resp.Json) ?? [];
        BrowseEntries.Clear();
        foreach (BrowseEntry e in entries)
        {
            BrowseEntries.Add(new BrowseRow
            {
                Name = e.Name,
                RelativePath = e.RelativePath,
                IsFolder = e.IsFolder,
                Detail = e.IsFolder
                    ? $"{e.FileCount} file{(e.FileCount == 1 ? "" : "s")} · {e.VersionCount} version{(e.VersionCount == 1 ? "" : "s")}"
                    : $"{e.VersionCount} version{(e.VersionCount == 1 ? "" : "s")} · {e.LastCapturedUtc.ToLocalTime():MMM d, HH:mm}",
            });
        }

        OnChanged(nameof(BrowseIsEmpty));
        OnChanged(nameof(BrowseIsSearch));
    }

    public bool BrowseIsEmpty => BrowseEntries.Count == 0;

    public bool BrowseIsSearch => !string.IsNullOrWhiteSpace(_browseSearch);

    private void BuildBreadcrumbs()
    {
        Breadcrumbs.Clear();
        Breadcrumbs.Add(new Crumb { Label = "Home", Path = "", IsLast = _browsePath.Length == 0 });
        if (_browsePath.Length == 0)
        {
            return;
        }

        string[] segs = _browsePath.Split('/');
        var acc = new List<string>();
        for (int i = 0; i < segs.Length; i++)
        {
            acc.Add(segs[i]);
            Breadcrumbs.Add(new Crumb
            {
                Label = segs[i],
                Path = string.Join('/', acc),
                IsLast = i == segs.Length - 1,
            });
        }
    }

    public string Status
    {
        get => _status;
        private set { _status = value; OnChanged(); }
    }

    public bool ServiceUp
    {
        get => _serviceUp;
        private set { _serviceUp = value; OnChanged(); }
    }

    public int ProtectingCount
    {
        get => _protectingCount;
        private set { _protectingCount = value; OnChanged(); }
    }

    public long TotalVersions
    {
        get => _totalVersions;
        private set { _totalVersions = value; OnChanged(); }
    }

    /// <summary>
    /// Two-way: flipping the switch starts/stops the whole-machine flight recorder live.
    /// If the service can't start it (e.g. unprivileged dev run), the push fails and the
    /// next settings load snaps the switch back to the truth.
    /// </summary>
    public bool FlightRecorderOn
    {
        get => _flightRecorderOn;
        set
        {
            if (_flightRecorderOn == value)
            {
                return;
            }

            _flightRecorderOn = value;
            OnChanged();
            if (!_loadingSettings)
            {
                _ = PushFlightRecorderAsync(value);
            }
        }
    }

    private async Task PushFlightRecorderAsync(bool enabled)
    {
        await PushPatchAsync(new { FlightRecorderEnabled = enabled });
        await LoadSettingsAsync();
        await RefreshStatusOnlyAsync();
    }

    /// <summary>
    /// Two-way: flipping the switch pushes the change to the service, which re-encodes the
    /// existing store in the background (readable throughout). If the push fails, the next
    /// settings load snaps the switch back to the service's truth.
    /// </summary>
    public bool EncryptionOn
    {
        get => _encryptionOn;
        set
        {
            if (_encryptionOn == value)
            {
                return;
            }

            _encryptionOn = value;
            OnChanged();
            if (!_loadingSettings)
            {
                _ = PushEncryptionAsync(value);
            }
        }
    }

    private long _storeBytes;
    private bool _reEncoding;

    /// <summary>"12 versions · 3.4 MB" for the storage row.</summary>
    public string StorageText
    {
        get
        {
            string versions = $"{TotalVersions:N0} version{(TotalVersions == 1 ? "" : "s")}";
            return _storeBytes > 0 ? $"{versions} · {FormatSize(_storeBytes)}" : versions;
        }
    }

    public bool ReEncoding
    {
        get => _reEncoding;
        private set { _reEncoding = value; OnChanged(); }
    }

    /// <summary>Two-way: flipping the switch pushes the change to the service immediately.</summary>
    public bool AutoUpdateEnabled
    {
        get => _autoUpdateEnabled;
        set
        {
            if (_autoUpdateEnabled == value)
            {
                return;
            }

            _autoUpdateEnabled = value;
            OnChanged();
            if (!_loadingSettings)
            {
                _ = PushAutoUpdateAsync(value);
            }
        }
    }

    public string HistoryPath
    {
        get => _historyPath;
        set { _historyPath = value; OnChanged(); }
    }

    public string HistoryTitle
    {
        get => _historyTitle;
        private set { _historyTitle = value; OnChanged(); }
    }

    public static string AppVersion =>
        typeof(MainViewModel).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.0.0";

    /// <summary>
    /// The MCP server exe path written into AI tools' configs. Resolved through
    /// <see cref="McpInstaller.ResolveServerExe(string)"/>, which prefers the installer's
    /// SPACELESS copy under %ProgramData%\StepWind\bin — several MCP clients (Cursor included)
    /// spawn the command via cmd.exe without quoting, so a "Program Files" path breaks at the
    /// space. Falls back to the exe beside the app (dev tree / portable).
    /// </summary>
    public static string McpServerPath => McpInstaller.ResolveServerExe(Path.Combine(
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory, "StepWind.Mcp.exe"));

    /// <summary>
    /// The exact JSON block to paste into an AI tool's MCP config (Cursor's mcp.json, Claude
    /// Desktop's config, etc.) — the standard "mcpServers" shape nearly every MCP client uses.
    /// </summary>
    public static string McpConfigSnippet
    {
        get
        {
            string jsonPath = JsonSerializer.Serialize(McpServerPath); // correct \-escaping, pre-quoted
            return "{\n  \"mcpServers\": {\n    \"stepwind\": {\n      \"command\": " + jsonPath + "\n    }\n  }\n}";
        }
    }

    // ------------------------------- AI agents tab -------------------------------

    public ObservableCollection<AgentRow> Agents { get; } = [];

    public int AgentsDetectedCount
    {
        get => _agentsDetectedCount;
        private set { _agentsDetectedCount = value; OnChanged(); OnChanged(nameof(AgentsSummary)); }
    }

    public int AgentsConnectedCount
    {
        get => _agentsConnectedCount;
        private set { _agentsConnectedCount = value; OnChanged(); OnChanged(nameof(AgentsSummary)); }
    }

    public string AgentsSummary => AgentsDetectedCount == 0
        ? "No supported AI tools were found on this PC. Install one (Cursor, Claude, VS Code…) or use the manual setup below."
        : $"{AgentsDetectedCount} AI tool{(AgentsDetectedCount == 1 ? "" : "s")} found on this PC · {AgentsConnectedCount} connected";

    private int _agentsDetectedCount;
    private int _agentsConnectedCount;

    /// <summary>
    /// Re-probes every supported AI tool (installed? already configured? pointing at the right
    /// exe?). Pure disk probing — runs off the UI thread, then swaps the collection in one pass.
    /// </summary>
    public async Task RefreshAgentsAsync()
    {
        List<AgentRow> rows = await Task.Run(() =>
        {
            string exe = McpServerPath;
            var built = new List<AgentRow>();
            foreach (McpClientTarget t in McpInstaller.DetectAll())
            {
                bool stale = t.Configured && t.ConfiguredCommand is { Length: > 0 } cmd &&
                             !string.Equals(cmd, exe, StringComparison.OrdinalIgnoreCase);
                string status = !t.Installed
                    ? "Not found on this PC"
                    : !t.Configured
                        ? "Ready to connect"
                        : stale
                            ? "Connected, but pointing at an old StepWind location"
                            : "Connected";
                built.Add(new AgentRow { Target = t, NeedsRepair = stale, StatusText = status });
            }
            return built.OrderBy(r => r.SortOrder).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        });

        Agents.Clear();
        foreach (AgentRow r in rows)
        {
            Agents.Add(r);
        }
        AgentsDetectedCount = rows.Count(r => r.Detected);
        AgentsConnectedCount = rows.Count(r => r.Connected);
    }

    /// <summary>One-click connect (also used for repair — install always writes the current path).</summary>
    public async Task<McpInstallResult> ConnectAgentAsync(AgentRow row)
    {
        string exe = McpServerPath;
        if (!File.Exists(exe))
        {
            return new McpInstallResult(false,
                $"StepWind.Mcp.exe was not found next to the app ({exe}). Repair the StepWind installation first — nothing was changed.");
        }

        McpInstallResult result = await Task.Run(() => McpInstaller.Install(row.Target, exe));
        await RefreshAgentsAsync();
        return result;
    }

    public async Task<McpInstallResult> DisconnectAgentAsync(AgentRow row)
    {
        McpInstallResult result = await Task.Run(() => McpInstaller.Remove(row.Target));
        await RefreshAgentsAsync();
        return result;
    }

    private bool _seededDefaults;

    public async Task RefreshAsync()
    {
        IpcResponse status = await _pipe.SendAsync(new IpcRequest { Command = IpcCommand.GetStatus });
        if (!status.Ok)
        {
            ServiceUp = false;
            Status = status.Error ?? "StepWind service is not running.";
            return;
        }

        ServiceUp = true;
        int roots = ParseStatus(status.Json!);

        await LoadSettingsAsync();

        // First-run seeding: the SYSTEM service can't see the user's real folders, so the GUI
        // (running as the user) supplies sensible defaults — but ONLY on a genuine first run.
        // FirstRunCompleted flips permanently once any human folder decision is made, so a
        // user who removes folders (even all of them) never gets them silently re-added.
        if (roots == 0 && !_firstRunCompleted && !_seededDefaults)
        {
            _seededDefaults = true;
            List<string> defaults = StepWindSettings.DefaultUserFolders();
            if (defaults.Count > 0)
            {
                await SetWatchedFoldersAsync(defaults);
            }
        }
        await LoadTimelineAsync();
        await RefreshBrowseAsync();
        if (!string.IsNullOrWhiteSpace(_historyPath))
        {
            await LoadHistoryAsync(_historyPath);
        }
    }

    /// <summary>Parses GetStatus json into the status properties; returns the root count.</summary>
    private int ParseStatus(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement r = doc.RootElement;
        int roots = r.GetProperty("WatchedRoots").GetInt32();
        long versions = r.TryGetProperty("TotalVersions", out JsonElement tv) ? tv.GetInt64() : 0;
        bool fr = r.TryGetProperty("FlightRecorder", out JsonElement f) && f.GetBoolean();

        ProtectingCount = roots;
        TotalVersions = versions;
        if (_flightRecorderOn != fr)
        {
            // Reflect the ACTUAL running state without going through the pushing setter —
            // status updates must never themselves send a settings patch.
            _flightRecorderOn = fr;
            OnChanged(nameof(FlightRecorderOn));
            OnChanged(nameof(TimelineIsEmpty));
            OnChanged(nameof(TimelineEmptyText));
        }

        _storeBytes = r.TryGetProperty("StoreBytes", out JsonElement sb) ? sb.GetInt64() : 0;
        OnChanged(nameof(StorageText));
        ReEncoding = r.TryGetProperty("ReEncoding", out JsonElement re) && re.GetBoolean();
        Status = roots == 0
            ? "No folders protected yet"
            : $"Protecting {roots} folder{(roots == 1 ? "" : "s")} · {versions:N0} versions · {FormatSize(Math.Max(0, _storeBytes))}";
        return roots;
    }

    public async Task LoadSettingsAsync()
    {
        IpcResponse resp = await _pipe.SendAsync(new IpcRequest { Command = IpcCommand.GetSettings });
        if (!resp.Ok || resp.Json is null)
        {
            return;
        }

        _loadingSettings = true;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(resp.Json);
            JsonElement root = doc.RootElement;

            WatchedFolders.Clear();
            if (root.TryGetProperty("WatchedFolders", out JsonElement wf) && wf.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement e in wf.EnumerateArray())
                {
                    if (e.GetString() is { Length: > 0 } path)
                    {
                        WatchedFolders.Add(path);
                    }
                }
            }

            if (root.TryGetProperty("AutoUpdateEnabled", out JsonElement au))
            {
                AutoUpdateEnabled = au.GetBoolean();
            }

            if (root.TryGetProperty("EncryptionEnabled", out JsonElement enc))
            {
                EncryptionOn = enc.GetBoolean();
            }

            if (root.TryGetProperty("FirstRunCompleted", out JsonElement frc))
            {
                _firstRunCompleted = frc.GetBoolean();
            }

            if (root.TryGetProperty("TimelineProtectedOnly", out JsonElement tpo))
            {
                TimelineProtectedOnly = tpo.GetBoolean();
            }

            if (root.TryGetProperty("RetentionKeepAllHours", out JsonElement kah))
            {
                RetentionKeepAllHours = kah.GetInt32();
            }

            if (root.TryGetProperty("RetentionHourlyDays", out JsonElement hd))
            {
                RetentionHourlyDays = hd.GetInt32();
            }

            if (root.TryGetProperty("RetentionDailyDays", out JsonElement dd))
            {
                RetentionDailyDays = dd.GetInt32();
            }

            if (root.TryGetProperty("RetentionMaxAgeDays", out JsonElement mad))
            {
                RetentionMaxAgeDays = mad.GetInt32();
            }

            if (root.TryGetProperty("RetentionMaxVersionsPerFile", out JsonElement mv))
            {
                RetentionMaxVersionsPerFile = mv.GetInt32();
            }
        }
        finally
        {
            _loadingSettings = false;
        }
    }

    public async Task AddWatchedFolderAsync(string folder)
    {
        var set = WatchedFolders.ToList();
        if (!set.Contains(folder, StringComparer.OrdinalIgnoreCase))
        {
            set.Add(folder);
            await SetWatchedFoldersAsync(set);
        }
    }

    public async Task RemoveWatchedFolderAsync(string folder)
    {
        var set = WatchedFolders.Where(f => !f.Equals(folder, StringComparison.OrdinalIgnoreCase)).ToList();
        await SetWatchedFoldersAsync(set);
    }

    private async Task SetWatchedFoldersAsync(List<string> folders)
    {
        string json = JsonSerializer.Serialize(new { WatchedFolders = folders });
        await _pipe.SendAsync(new IpcRequest { Command = IpcCommand.SetSettings, Arg1 = json });
        await LoadSettingsAsync();
        await RefreshStatusOnlyAsync();
    }

    private async Task PushAutoUpdateAsync(bool enabled)
    {
        string json = JsonSerializer.Serialize(new { AutoUpdateEnabled = enabled });
        await _pipe.SendAsync(new IpcRequest { Command = IpcCommand.SetSettings, Arg1 = json });
    }

    private async Task PushEncryptionAsync(bool enabled)
    {
        string json = JsonSerializer.Serialize(new { EncryptionEnabled = enabled });
        await _pipe.SendAsync(new IpcRequest { Command = IpcCommand.SetSettings, Arg1 = json });
        // Re-load: on success this confirms the value; on failure (old service, key error)
        // it snaps the switch back to what the service actually has.
        await LoadSettingsAsync();
        await RefreshStatusOnlyAsync();
    }

    private async Task PushPatchAsync(object patch)
        => await _pipe.SendAsync(new IpcRequest { Command = IpcCommand.SetSettings, Arg1 = JsonSerializer.Serialize(patch) });

    // ── Retention (user-configurable) ──────────────────────────────────────────────
    private bool _firstRunCompleted;
    private int _retKeepAllHours = 24, _retHourlyDays = 7, _retDailyDays = 90, _retMaxAgeDays = 365, _retMaxVersions = 200;

    public int RetentionKeepAllHours { get => _retKeepAllHours; set { _retKeepAllHours = value; OnChanged(); } }
    public int RetentionHourlyDays { get => _retHourlyDays; set { _retHourlyDays = value; OnChanged(); } }
    public int RetentionDailyDays { get => _retDailyDays; set { _retDailyDays = value; OnChanged(); } }
    public int RetentionMaxAgeDays { get => _retMaxAgeDays; set { _retMaxAgeDays = value; OnChanged(); } }
    public int RetentionMaxVersionsPerFile { get => _retMaxVersions; set { _retMaxVersions = value; OnChanged(); } }

    /// <summary>Pushes the retention numbers to the service (values clamped service-side).</summary>
    public async Task ApplyRetentionAsync()
    {
        await PushPatchAsync(new
        {
            RetentionKeepAllHours = _retKeepAllHours,
            RetentionHourlyDays = _retHourlyDays,
            RetentionDailyDays = _retDailyDays,
            RetentionMaxAgeDays = _retMaxAgeDays,
            RetentionMaxVersionsPerFile = _retMaxVersions,
        });
        await LoadSettingsAsync(); // reflect service-side clamping immediately
    }

    // ── Data management ────────────────────────────────────────────────────────────

    /// <summary>Deletes stored history now. Selector: "*", "unprotected", or a store path.</summary>
    public async Task<string> PurgeHistoryAsync(string selector)
    {
        IpcResponse resp = await _pipe.SendAsync(new IpcRequest { Command = IpcCommand.PurgeHistory, Arg1 = selector });
        if (!resp.Ok || resp.Json is null)
        {
            return resp.Error ?? "Could not delete history.";
        }

        using JsonDocument doc = JsonDocument.Parse(resp.Json);
        int versions = doc.RootElement.GetProperty("RemovedVersions").GetInt32();
        int blobs = doc.RootElement.GetProperty("SweptBlobs").GetInt32();
        _browseFingerprint = ""; // force the browser to rebuild after a purge
        await RefreshAsync();
        return $"Deleted {versions:N0} version{(versions == 1 ? "" : "s")} and freed {blobs:N0} stored chunk{(blobs == 1 ? "" : "s")}.";
    }

    /// <summary>Runs the retention + GC pass right now instead of waiting for the daily timer.</summary>
    public async Task<string> RunRetentionNowAsync()
    {
        IpcResponse resp = await _pipe.SendAsync(new IpcRequest { Command = IpcCommand.RunRetention });
        if (!resp.Ok || resp.Json is null)
        {
            return resp.Error ?? "Could not run cleanup.";
        }

        using JsonDocument doc = JsonDocument.Parse(resp.Json);
        int before = doc.RootElement.GetProperty("VersionsBefore").GetInt32();
        int kept = doc.RootElement.GetProperty("VersionsKept").GetInt32();
        int swept = doc.RootElement.GetProperty("BlobsSwept").GetInt32();
        await RefreshAsync();
        return $"Cleanup done — kept {kept:N0} of {before:N0} versions, freed {swept:N0} chunk{(swept == 1 ? "" : "s")}.";
    }

    /// <summary>The store-relative name of a folder ("Desk" for …\Desk), for folder purges.</summary>
    public static string FolderSelector(string folderPath)
        => System.IO.Path.GetFileName(folderPath.TrimEnd('\\', '/'));

    private async Task RefreshStatusOnlyAsync()
    {
        IpcResponse status = await _pipe.SendAsync(new IpcRequest { Command = IpcCommand.GetStatus });
        if (status.Ok && status.Json is not null)
        {
            ParseStatus(status.Json);
        }
    }

    private string _timelineFingerprint = "";

    private async Task LoadTimelineAsync()
    {
        IpcResponse resp = await _pipe.SendAsync(new IpcRequest { Command = IpcCommand.GetTimeline, Limit = 200 });
        if (!resp.Ok || resp.Json is null)
        {
            return;
        }

        // Same reason as the recent-files guard: the 3s auto-refresh must not reset the
        // user's scroll position unless something actually happened.
        if (resp.Json == _timelineFingerprint)
        {
            return;
        }

        _timelineFingerprint = resp.Json;
        TimelineEntry[] entries = JsonSerializer.Deserialize<TimelineEntry[]>(resp.Json) ?? [];

        // Plain Clear+Add: the collection view tracks ObservableCollection changes
        // incrementally. (DeferRefresh must NOT wrap source mutations — WPF throws
        // "cannot change contents while Refresh is being deferred".)
        Timeline.Clear();
        foreach (TimelineEntry e in entries)
        {
            DateTime local = e.TimestampUtc.ToLocalTime();
            Timeline.Add(new TimelineRow
            {
                Kind = e.Kind,
                Day = DayLabel(local),
                Time = local.ToString("HH:mm:ss"),
                Description = Describe(e),
                ByProcess = e.ByProcess,
                Reversible = e.Reversible,
                OperationId = e.OperationId,
                OldPath = e.OldPath,
                NewPath = e.NewPath,
            });
        }

        OnChanged(nameof(TimelineIsEmpty));
        OnChanged(nameof(TimelineEmptyText));
    }

    /// <summary>True when the timeline has nothing to show — drives the empty-state overlay.</summary>
    public bool TimelineIsEmpty => Timeline.Count == 0;

    /// <summary>Explains WHY the timeline is empty (recorder off vs simply no activity yet).</summary>
    public string TimelineEmptyText => !_flightRecorderOn
        ? "The flight recorder is off. Turn it on in Settings to record file operations across your drives."
        : "No file activity recorded yet. Move, rename, or delete a file and it'll show up here — with one-click undo.";

    private static string DayLabel(DateTime local)
    {
        DateTime today = DateTime.Now.Date;
        return local.Date == today ? "Today"
            : local.Date == today.AddDays(-1) ? "Yesterday"
            : local.Year == today.Year ? local.ToString("MMMM d")
            : local.ToString("MMMM d, yyyy");
    }

    private bool _hasHistorySelection;

    /// <summary>True once a file is selected in the browser — gates the "delete this file's history" button.</summary>
    public bool HasHistorySelection
    {
        get => _hasHistorySelection;
        private set { _hasHistorySelection = value; OnChanged(); }
    }

    private string _historyFingerprint = "";

    public async Task LoadHistoryAsync(string relativePath)
    {
        bool switchingFile = !string.Equals(relativePath, HistoryPath, StringComparison.OrdinalIgnoreCase);
        HistoryPath = relativePath;
        HistoryTitle = relativePath;
        HasHistorySelection = true;
        IpcResponse resp = await _pipe.SendAsync(new IpcRequest { Command = IpcCommand.GetHistory, Arg1 = relativePath });
        if (!resp.Ok || resp.Json is null)
        {
            return;
        }

        // Don't rebuild the list on the 3s auto-refresh when nothing changed — it would reset
        // the user's scroll/selection. A file switch always rebuilds.
        string fp = relativePath + "\u0001" + resp.Json;
        if (!switchingFile && fp == _historyFingerprint)
        {
            return;
        }

        _historyFingerprint = fp;
        History.Clear();
        foreach (VersionEntry v in JsonSerializer.Deserialize<VersionEntry[]>(resp.Json) ?? [])
        {
            History.Add(new VersionRow
            {
                When = v.CapturedUtc.ToLocalTime().ToString("MMM d, HH:mm:ss"),
                SizeText = FormatSize(v.Size),
                Reason = v.Reason,
                VersionId = v.VersionId,
            });
        }
    }

    /// <summary>Clears the version-history pane (after the selected file's history is deleted).</summary>
    public void ClearHistorySelection()
    {
        History.Clear();
        HistoryPath = "";
        HistoryTitle = DefaultHistoryTitle;
        HasHistorySelection = false;
        _historyFingerprint = "";
    }

    public async Task<string> ReverseAsync(TimelineRow row)
    {
        IpcResponse resp = await _pipe.SendAsync(new IpcRequest { Command = IpcCommand.ReverseOperation, Arg1 = row.OperationId });
        await RefreshAsync();
        return resp.Ok ? "Reversed." : resp.Error ?? "Could not reverse.";
    }

    public async Task<string> RestoreAsync(VersionRow row)
    {
        IpcResponse resp = await _pipe.SendAsync(new IpcRequest { Command = IpcCommand.RestoreVersion, Arg1 = row.VersionId });
        if (resp.Ok && resp.Json is not null)
        {
            string? path = JsonSerializer.Deserialize<JsonElement>(resp.Json).GetProperty("RestoredPath").GetString();
            return "Restored to: " + path;
        }

        return resp.Error ?? "Could not restore.";
    }

    private static string Describe(TimelineEntry e) => e.Kind switch
    {
        "Move" => $"Moved {e.Name}  ({Short(e.OldPath)} → {Short(e.NewPath)})",
        "Rename" => $"Renamed to {e.Name}",
        "Delete" => $"Deleted {e.Name}  ({Short(e.OldPath)})",
        "Create" => $"Created {e.Name}",
        _ => $"Changed {e.Name}",
    };

    private static string Short(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "?";
        }

        string[] parts = path.Split('\\');
        return parts.Length <= 3 ? path : ".../" + string.Join('\\', parts[^2..]);
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (1024.0 * 1024 * 1024):0.#} GB",
        >= 1L << 20 => $"{bytes / (1024.0 * 1024):0.#} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes} B",
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
