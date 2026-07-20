using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Data;
using System.Windows.Threading;
using StepWind.Core.Engine;
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
}

/// <summary>A row in a file's version history.</summary>
public sealed class VersionRow
{
    public required string When { get; init; }
    public required string SizeText { get; init; }
    public required string Reason { get; init; }
    public required string VersionId { get; init; }
}

/// <summary>A recently-changed protected file for the quick-pick list.</summary>
public sealed class RecentFileRow
{
    public required string RelativePath { get; init; }
    public required string DisplayName { get; init; }
    public required string Detail { get; init; }
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
    private string _recentSearch = "";
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
            _timelineFilter == "All" || (o is TimelineRow r && r.Kind == _timelineFilter);

        RecentView = CollectionViewSource.GetDefaultView(RecentFiles);
        RecentView.Filter = o =>
            string.IsNullOrWhiteSpace(_recentSearch)
            || (o is RecentFileRow f && f.RelativePath.Contains(_recentSearch, StringComparison.OrdinalIgnoreCase));

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(3) };
        _refreshTimer.Tick += async (_, _) => await RefreshAsync();
        _refreshTimer.Start();
        _ = RefreshAsync();
    }

    public ObservableCollection<TimelineRow> Timeline { get; } = [];

    public ObservableCollection<VersionRow> History { get; } = [];

    public ObservableCollection<RecentFileRow> RecentFiles { get; } = [];

    public ObservableCollection<string> WatchedFolders { get; } = [];

    /// <summary>Grouped (by day) + filtered view the timeline list binds to.</summary>
    public ICollectionView TimelineView { get; }

    /// <summary>Search-filtered view of the recent files list.</summary>
    public ICollectionView RecentView { get; }

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

    public string RecentSearch
    {
        get => _recentSearch;
        set
        {
            _recentSearch = value;
            OnChanged();
            RecentView.Refresh();
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

    public bool FlightRecorderOn
    {
        get => _flightRecorderOn;
        private set { _flightRecorderOn = value; OnChanged(); }
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

        // First-run seeding: the SYSTEM service can't see the user's real folders, so the GUI
        // (running as the user) supplies sensible defaults the first time it finds none.
        if (roots == 0 && !_seededDefaults)
        {
            _seededDefaults = true;
            List<string> defaults = StepWindSettings.DefaultUserFolders();
            if (defaults.Count > 0)
            {
                await SetWatchedFoldersAsync(defaults);
            }
        }

        await LoadSettingsAsync();
        await LoadTimelineAsync();
        await LoadRecentFilesAsync();
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
        FlightRecorderOn = fr;
        _storeBytes = r.TryGetProperty("StoreBytes", out JsonElement sb) ? sb.GetInt64() : 0;
        OnChanged(nameof(StorageText));
        ReEncoding = r.TryGetProperty("ReEncoding", out JsonElement re) && re.GetBoolean();
        Status = roots == 0
            ? "No folders protected yet"
            : $"Protecting {roots} folder{(roots == 1 ? "" : "s")} · {versions:N0} versions · {FormatSize(Math.Max(0, _storeBytes))}";
        return roots;
    }

    private string _recentFingerprint = "";

    public async Task LoadRecentFilesAsync()
    {
        IpcResponse resp = await _pipe.SendAsync(new IpcRequest { Command = IpcCommand.GetRecentFiles, Limit = 100 });
        if (!resp.Ok || resp.Json is null)
        {
            return;
        }

        // The auto-refresh timer calls this every few seconds; rebuilding the list resets the
        // user's selection and scroll, so only touch it when the content actually changed.
        if (resp.Json == _recentFingerprint)
        {
            return;
        }

        _recentFingerprint = resp.Json;
        RecentFileEntry[] files = JsonSerializer.Deserialize<RecentFileEntry[]>(resp.Json) ?? [];
        RecentFiles.Clear();
        foreach (RecentFileEntry f in files)
        {
            string name = f.RelativePath.Contains('/') ? f.RelativePath[(f.RelativePath.LastIndexOf('/') + 1)..] : f.RelativePath;
            RecentFiles.Add(new RecentFileRow
            {
                RelativePath = f.RelativePath,
                DisplayName = name,
                Detail = $"{f.RelativePath} · {f.VersionCount} version{(f.VersionCount == 1 ? "" : "s")} · {f.LastCapturedUtc.ToLocalTime():MMM d, HH:mm}",
            });
        }
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
            });
        }
    }

    private static string DayLabel(DateTime local)
    {
        DateTime today = DateTime.Now.Date;
        return local.Date == today ? "Today"
            : local.Date == today.AddDays(-1) ? "Yesterday"
            : local.Year == today.Year ? local.ToString("MMMM d")
            : local.ToString("MMMM d, yyyy");
    }

    public async Task LoadHistoryAsync(string relativePath)
    {
        HistoryPath = relativePath;
        HistoryTitle = relativePath;
        IpcResponse resp = await _pipe.SendAsync(new IpcRequest { Command = IpcCommand.GetHistory, Arg1 = relativePath });
        History.Clear();
        if (!resp.Ok || resp.Json is null)
        {
            return;
        }

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
