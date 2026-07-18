using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Threading;
using StepWind.Core.Ipc;

namespace StepWind.App.ViewModels;

/// <summary>A row on the operation timeline (flight recorder).</summary>
public sealed class TimelineRow
{
    public required string Kind { get; init; }
    public required string When { get; init; }
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

/// <summary>
/// Talks to the elevated service over the pipe and drives the window: connection status, the
/// live operation timeline (with one-click reverse), and per-file version history (with
/// restore). All work is async and failure-tolerant — if the service is down, the UI says so
/// instead of throwing.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly PipeClient _pipe = new();
    private readonly DispatcherTimer _refreshTimer;
    private string _status = "Connecting to StepWind service…";
    private bool _serviceUp;
    private string _historyPath = "";

    public MainViewModel()
    {
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(3) };
        _refreshTimer.Tick += async (_, _) => await RefreshAsync();
        _refreshTimer.Start();
        _ = RefreshAsync();
    }

    public ObservableCollection<TimelineRow> Timeline { get; } = [];

    public ObservableCollection<VersionRow> History { get; } = [];

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

    public string HistoryPath
    {
        get => _historyPath;
        set { _historyPath = value; OnChanged(); }
    }

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
        using (JsonDocument doc = JsonDocument.Parse(status.Json!))
        {
            JsonElement r = doc.RootElement;
            int roots = r.GetProperty("WatchedRoots").GetInt32();
            long versions = r.TryGetProperty("TotalVersions", out JsonElement tv) ? tv.GetInt64() : 0;
            bool fr = r.TryGetProperty("FlightRecorder", out JsonElement f) && f.GetBoolean();
            Status = $"Protecting {roots} folder(s) · {versions:N0} versions kept · flight recorder {(fr ? "on" : "off")}";
        }

        await LoadTimelineAsync();
        if (!string.IsNullOrWhiteSpace(_historyPath))
        {
            await LoadHistoryAsync(_historyPath);
        }
    }

    private async Task LoadTimelineAsync()
    {
        IpcResponse resp = await _pipe.SendAsync(new IpcRequest { Command = IpcCommand.GetTimeline, Limit = 200 });
        if (!resp.Ok || resp.Json is null)
        {
            return;
        }

        TimelineEntry[] entries = JsonSerializer.Deserialize<TimelineEntry[]>(resp.Json) ?? [];
        Timeline.Clear();
        foreach (TimelineEntry e in entries)
        {
            Timeline.Add(new TimelineRow
            {
                Kind = e.Kind,
                When = e.TimestampUtc.ToLocalTime().ToString("MMM d, HH:mm:ss"),
                Description = Describe(e),
                ByProcess = e.ByProcess,
                Reversible = e.Reversible,
                OperationId = e.OperationId,
            });
        }
    }

    public async Task LoadHistoryAsync(string relativePath)
    {
        HistoryPath = relativePath;
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
