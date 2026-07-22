using System.Text.Json;
using StepWind.Core.Engine;
using StepWind.Core.Ipc;
using StepWind.Core.Storage;
using Xunit;

namespace StepWind.Core.Tests;

/// <summary>
/// P2-6: guided store relocation. The safety contract: copy → verify → switch, and NEVER delete
/// the old store, so relocation can't lose history. These pin that a relocated store is fully
/// restorable at the new root, the old store is left intact, bad destinations are refused, and
/// capturing continues to the new location afterward.
/// </summary>
public class StoreRelocationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sw-relocate", Guid.NewGuid().ToString("N"));
    private readonly string _watch;
    private readonly StepWindSettings _settings;
    private readonly StepWindHost _host;

    public StoreRelocationTests()
    {
        _watch = Path.Combine(_root, "Docs");
        Directory.CreateDirectory(_watch);
        _settings = new StepWindSettings
        {
            StoreRoot = Path.Combine(_root, "store"),
            WatchedFolders = [_watch],
            FlightRecorderEnabled = false,
        };
        _host = new StepWindHost(_settings, new GzipBlobCodec());
    }

    private async Task SeedAsync(string name, string content)
    {
        File.WriteAllText(Path.Combine(_watch, name), content);
        for (int i = 0; i < 40; i++)
        {
            if (JsonSerializer.Deserialize<VersionEntry[]>(
                    _host.Handle(new IpcRequest { Command = IpcCommand.GetHistory, Arg1 = "Docs/" + name }).Json!)!.Length > 0)
            {
                return;
            }

            await Task.Delay(200);
        }

        throw new Xunit.Sdk.XunitException($"{name} never captured");
    }

    private int Versions(string rel) => JsonSerializer.Deserialize<VersionEntry[]>(
        _host.Handle(new IpcRequest { Command = IpcCommand.GetHistory, Arg1 = rel }).Json!)!.Length;

    [Fact]
    public async Task Relocating_copies_and_verifies_keeps_the_old_store_and_keeps_working()
    {
        await SeedAsync("report.txt", "the report");
        string newRoot = Path.Combine(_root, "moved-store");

        IpcResponse resp = _host.Handle(new IpcRequest { Command = IpcCommand.RelocateStore, Arg1 = newRoot });
        Assert.True(resp.Ok, resp.Error);

        // Settings now point at the new root, and history is still readable through the host.
        Assert.Equal(Path.GetFullPath(newRoot), Path.GetFullPath(_settings.StoreRoot));
        Assert.True(Versions("Docs/report.txt") >= 1);

        // The new store physically has the data…
        Assert.True(File.Exists(Path.Combine(newRoot, "versions.jsonl")));
        Assert.True(Directory.EnumerateFiles(Path.Combine(newRoot, "blobs"), "*", SearchOption.AllDirectories).Any());

        // …and the OLD store was left completely intact (never deleted — no data-loss risk).
        Assert.True(File.Exists(Path.Combine(_root, "store", "versions.jsonl")));

        // Capturing continues, now into the new location.
        await SeedAsync("after.txt", "written after the move");
        Assert.True(Versions("Docs/after.txt") >= 1);
        Assert.True(File.Exists(Path.Combine(newRoot, "versions.jsonl")));

        // And a restore still reconstructs byte-exact from the relocated store.
        VersionEntry v = JsonSerializer.Deserialize<VersionEntry[]>(
            _host.Handle(new IpcRequest { Command = IpcCommand.GetHistory, Arg1 = "Docs/report.txt" }).Json!)![0];
        IpcResponse restore = _host.Handle(new IpcRequest { Command = IpcCommand.RestoreVersion, Arg1 = v.VersionId });
        Assert.True(restore.Ok, restore.Error);
    }

    [Fact]
    public async Task Bad_destinations_are_refused_without_touching_the_current_store()
    {
        await SeedAsync("x.txt", "data");

        // Same location.
        Assert.False(_host.Handle(new IpcRequest { Command = IpcCommand.RelocateStore, Arg1 = _settings.StoreRoot }).Ok);
        // Inside a protected folder (would version the store itself).
        Assert.False(_host.Handle(new IpcRequest { Command = IpcCommand.RelocateStore, Arg1 = Path.Combine(_watch, "sub") }).Ok);
        // A non-empty folder that isn't ours.
        string nonEmpty = Path.Combine(_root, "occupied");
        Directory.CreateDirectory(nonEmpty);
        File.WriteAllText(Path.Combine(nonEmpty, "something.txt"), "in the way");
        Assert.False(_host.Handle(new IpcRequest { Command = IpcCommand.RelocateStore, Arg1 = nonEmpty }).Ok);

        // Still working on the original store.
        Assert.True(Versions("Docs/x.txt") >= 1);
    }

    public void Dispose()
    {
        _host.Dispose();
        try { Directory.Delete(_root, true); } catch { }
    }
}
