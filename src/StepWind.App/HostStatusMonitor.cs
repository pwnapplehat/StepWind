using System.Text.Json;
using StepWind.Core.Ipc;

namespace StepWind.App;

/// <summary>A tray notification to raise (title + body + whether it's a warning).</summary>
public sealed record TrayNotice(string Title, string Message, bool Warning);

/// <summary>
/// Polls the service in the background (independent of the web UI, which only refreshes while
/// open) so the tray can react to protection events even when StepWind is minimized: it keeps
/// the tray tooltip current and raises a notification when protection stops, capturing pauses
/// for disk space, or a verified update is ready to install. Transitions only — it notifies on
/// a CHANGE, never repeatedly for the same state, so it can't nag.
/// </summary>
public sealed class HostStatusMonitor : IDisposable
{
    private readonly PipeClient _pipe = new();
    private readonly Action<TrayNotice> _notify;
    private readonly Action<string> _setTooltip;
    private readonly CancellationTokenSource _cts = new();

    private bool _primed;
    private bool _reachable = true;
    private bool _recorder = true;
    private bool _paused;
    private string? _updateVersion;

    public HostStatusMonitor(Action<TrayNotice> notify, Action<string> setTooltip)
    {
        _notify = notify;
        _setTooltip = setTooltip;
    }

    public void Start() => _ = LoopAsync(_cts.Token);

    private async Task LoopAsync(CancellationToken ct)
    {
        // A first quick check, then every 15s.
        try { await Task.Delay(TimeSpan.FromSeconds(3), ct); } catch { return; }
        while (!ct.IsCancellationRequested)
        {
            await PollOnceAsync(ct);
            try { await Task.Delay(TimeSpan.FromSeconds(15), ct); } catch { return; }
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        try
        {
            IpcResponse resp = await _pipe.SendAsync(new IpcRequest { Command = IpcCommand.GetStatus }, ct: ct);
            if (!resp.Ok || resp.Json is null)
            {
                Update(reachable: false, recorder: false, paused: false, updateVersion: null,
                    tooltip: "StepWind — service not reachable");
                return;
            }

            using JsonDocument doc = JsonDocument.Parse(resp.Json);
            JsonElement r = doc.RootElement;
            bool recorder = GetBool(r, "FlightRecorder");
            bool paused = GetBool(r, "CapturePaused");
            int roots = GetInt(r, "WatchedRoots");
            long versions = GetLong(r, "TotalVersions");
            string? updateVersion = r.TryGetProperty("UpdateReadyVersion", out JsonElement uv) && uv.ValueKind == JsonValueKind.String
                ? uv.GetString() : null;
            string? pauseReason = r.TryGetProperty("PauseReason", out JsonElement pr) && pr.ValueKind == JsonValueKind.String
                ? pr.GetString() : null;

            string tooltip = paused
                ? "StepWind — capturing paused (low disk space)"
                : !recorder
                    ? "StepWind — protection is off"
                    : $"StepWind — protecting {roots} folder{(roots == 1 ? "" : "s")} · {versions:N0} versions";

            Update(reachable: true, recorder, paused, updateVersion, tooltip, pauseReason);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch
        {
            // transient — next tick retries; don't crash the tray
        }
    }

    private void Update(bool reachable, bool recorder, bool paused, string? updateVersion, string tooltip, string? pauseReason = null)
    {
        _setTooltip(tooltip);

        if (!_primed)
        {
            // Establish a baseline on the first poll without firing a burst of notifications —
            // except an already-staged update, which the user should know about immediately.
            _primed = true;
            _reachable = reachable;
            _recorder = recorder;
            _paused = paused;
            _updateVersion = updateVersion;
            if (updateVersion is not null)
            {
                _notify(new TrayNotice("Update ready", $"StepWind {updateVersion} is downloaded and verified. Open StepWind to install it.", false));
            }

            return;
        }

        if (reachable && !_reachable)
        {
            _notify(new TrayNotice("StepWind is back", "The protection service is reachable again.", false));
        }
        else if (!reachable && _reachable)
        {
            _notify(new TrayNotice("Protection stopped", "StepWind's background service isn't reachable. Your files aren't being protected right now.", true));
        }

        if (reachable)
        {
            if (!recorder && _recorder)
            {
                _notify(new TrayNotice("Flight recorder off", "Whole-machine operation recording has stopped. Turn it back on in StepWind → Settings.", true));
            }

            if (paused && !_paused)
            {
                _notify(new TrayNotice("Capturing paused", pauseReason ?? "Low disk space — free up space and StepWind resumes automatically.", true));
            }
            else if (!paused && _paused)
            {
                _notify(new TrayNotice("Capturing resumed", "There's room again — StepWind is saving versions as normal.", false));
            }

            if (updateVersion is not null && updateVersion != _updateVersion)
            {
                _notify(new TrayNotice("Update ready", $"StepWind {updateVersion} is downloaded and verified. Open StepWind to install it.", false));
            }
        }

        _reachable = reachable;
        _recorder = recorder;
        _paused = paused;
        _updateVersion = updateVersion;
    }

    private static bool GetBool(JsonElement e, string name)
        => e.TryGetProperty(name, out JsonElement v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False && v.GetBoolean();

    private static int GetInt(JsonElement e, string name)
        => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    private static long GetLong(JsonElement e, string name)
        => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
