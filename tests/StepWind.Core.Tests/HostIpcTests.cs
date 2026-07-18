using System.Text.Json;
using StepWind.Core.Engine;
using StepWind.Core.Ipc;
using StepWind.Core.Storage;
using Xunit;

namespace StepWind.Core.Tests;

/// <summary>
/// Exercises the service's IPC command surface in-process (no admin, flight recorder off):
/// a live save is captured, then queried and restored through the exact <see cref="StepWindHost.Handle"/>
/// path the GUI uses over the pipe.
/// </summary>
public class HostIpcTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "stepwind-host", Guid.NewGuid().ToString("N"));
    private readonly string _watch;
    private readonly StepWindHost _host;

    public HostIpcTests()
    {
        _watch = Path.Combine(_root, "Docs");
        Directory.CreateDirectory(_watch);
        var settings = new StepWindSettings
        {
            StoreRoot = Path.Combine(_root, "store"),
            WatchedFolders = [_watch],
            FlightRecorderEnabled = false, // needs admin; not under test here
        };
        _host = new StepWindHost(settings, new GzipBlobCodec());
    }

    [Fact]
    public void Ping_and_status_respond()
    {
        Assert.True(_host.Handle(new IpcRequest { Command = IpcCommand.Ping }).Ok);

        IpcResponse status = _host.Handle(new IpcRequest { Command = IpcCommand.GetStatus });
        Assert.True(status.Ok);
        using JsonDocument doc = JsonDocument.Parse(status.Json!);
        Assert.Equal(1, doc.RootElement.GetProperty("WatchedRoots").GetInt32());
    }

    [Fact]
    public async Task Capture_query_history_then_restore_over_ipc()
    {
        string file = Path.Combine(_watch, "report.txt");
        File.WriteAllText(file, "the version I want back");
        await WaitForCapture("Docs/report.txt");

        // History via IPC.
        IpcResponse hist = _host.Handle(new IpcRequest { Command = IpcCommand.GetHistory, Arg1 = "Docs/report.txt" });
        Assert.True(hist.Ok);
        VersionEntry[] versions = JsonSerializer.Deserialize<VersionEntry[]>(hist.Json!)!;
        Assert.NotEmpty(versions);

        // Ruin then delete the live file.
        File.WriteAllText(file, "ruined");
        File.Delete(file);

        // Restore the captured version via IPC.
        IpcResponse restore = _host.Handle(new IpcRequest
        {
            Command = IpcCommand.RestoreVersion,
            Arg1 = versions[0].VersionId,
        });
        Assert.True(restore.Ok, restore.Error);

        string restoredPath = JsonSerializer.Deserialize<JsonElement>(restore.Json!).GetProperty("RestoredPath").GetString()!;
        Assert.True(File.Exists(restoredPath));
        Assert.Equal("the version I want back", File.ReadAllText(restoredPath));
    }

    [Fact]
    public void Timeline_is_empty_without_the_flight_recorder()
    {
        IpcResponse r = _host.Handle(new IpcRequest { Command = IpcCommand.GetTimeline });
        Assert.True(r.Ok);
        Assert.Empty(JsonSerializer.Deserialize<TimelineEntry[]>(r.Json!)!);
    }

    private async Task WaitForCapture(string rel)
    {
        for (int i = 0; i < 40; i++)
        {
            IpcResponse h = _host.Handle(new IpcRequest { Command = IpcCommand.GetHistory, Arg1 = rel });
            if (h.Ok && JsonSerializer.Deserialize<VersionEntry[]>(h.Json!)!.Length > 0)
            {
                return;
            }

            await Task.Delay(250);
        }
    }

    public void Dispose()
    {
        _host.Dispose();
        try { Directory.Delete(_root, true); } catch { }
    }
}
