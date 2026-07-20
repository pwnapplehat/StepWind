using System.Text.Json;
using StepWind.Core.Engine;
using StepWind.Core.Ipc;
using StepWind.Core.Storage;
using Xunit;

namespace StepWind.Core.Tests;

/// <summary>
/// The bug report: "I removed a protected folder but it kept storing versions."
/// These tests pin the whole removal story: captures must STOP, history must be
/// deletable on demand, and nothing may silently re-add folders.
/// </summary>
public class FolderRemovalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "stepwind-rem", Guid.NewGuid().ToString("N"));
    private readonly string _folderA;
    private readonly string _folderB;
    private readonly StepWindSettings _settings;
    private readonly StepWindHost _host;

    public FolderRemovalTests()
    {
        _folderA = Path.Combine(_root, "Docs");
        _folderB = Path.Combine(_root, "Desk");
        Directory.CreateDirectory(_folderA);
        Directory.CreateDirectory(_folderB);
        _settings = new StepWindSettings
        {
            StoreRoot = Path.Combine(_root, "store"),
            WatchedFolders = [_folderA, _folderB],
            FlightRecorderEnabled = false,
        };
        _host = new StepWindHost(_settings, new GzipBlobCodec());
    }

    private async Task<int> VersionCount(string rel)
    {
        IpcResponse resp = _host.Handle(new IpcRequest { Command = IpcCommand.GetHistory, Arg1 = rel });
        return resp.Ok && resp.Json is not null ? (JsonSerializer.Deserialize<VersionEntry[]>(resp.Json)?.Length ?? 0) : 0;
    }

    private async Task WaitForVersions(string rel, int atLeast)
    {
        for (int i = 0; i < 60; i++)
        {
            if (await VersionCount(rel) >= atLeast)
            {
                return;
            }

            await Task.Delay(250);
        }
    }

    private void RemoveFolderB()
    {
        IpcResponse resp = _host.Handle(new IpcRequest
        {
            Command = IpcCommand.SetSettings,
            Arg1 = JsonSerializer.Serialize(new { WatchedFolders = new[] { _folderA } }),
        });
        Assert.True(resp.Ok);
    }

    [Fact]
    public async Task Captures_stop_the_moment_a_folder_is_removed()
    {
        File.WriteAllText(Path.Combine(_folderB, "note.txt"), "v1 while protected");
        await WaitForVersions("Desk/note.txt", 1);

        RemoveFolderB();

        // Edit the file AFTER removal — repeatedly, past the debounce window.
        for (int i = 0; i < 3; i++)
        {
            File.WriteAllText(Path.Combine(_folderB, "note.txt"), $"edit {i} after removal");
            await Task.Delay(1500);
        }

        await Task.Delay(2500); // one more full quiet period
        Assert.Equal(1, await VersionCount("Desk/note.txt")); // still only the protected-era version

        // And the still-protected folder keeps working.
        File.WriteAllText(Path.Combine(_folderA, "alive.txt"), "still captured");
        await WaitForVersions("Docs/alive.txt", 1);
        Assert.Equal(1, await VersionCount("Docs/alive.txt"));
    }

    [Fact]
    public void Any_folder_change_marks_first_run_complete_so_nothing_reseeds()
    {
        Assert.False(_settings.FirstRunCompleted); // fresh store

        // Removing everything is a deliberate user decision…
        IpcResponse resp = _host.Handle(new IpcRequest
        {
            Command = IpcCommand.SetSettings,
            Arg1 = JsonSerializer.Serialize(new { WatchedFolders = Array.Empty<string>() }),
        });
        Assert.True(resp.Ok);

        // …and the service records that a human has made a choice: the GUI must never
        // auto-seed defaults again (that's how "removed" folders came back).
        Assert.True(_settings.FirstRunCompleted);
        Assert.Contains("\"FirstRunCompleted\":true", _host.Handle(new IpcRequest { Command = IpcCommand.GetSettings }).Json);
    }

    [Fact]
    public async Task Purge_a_removed_folders_history_frees_versions_and_disk()
    {
        File.WriteAllText(Path.Combine(_folderA, "keep.txt"), "docs content stays");
        File.WriteAllText(Path.Combine(_folderB, "gone.txt"), "desk content goes");
        await WaitForVersions("Docs/keep.txt", 1);
        await WaitForVersions("Desk/gone.txt", 1);

        RemoveFolderB();

        IpcResponse purge = _host.Handle(new IpcRequest { Command = IpcCommand.PurgeHistory, Arg1 = "Desk" });
        Assert.True(purge.Ok);
        using JsonDocument doc = JsonDocument.Parse(purge.Json!);
        Assert.True(doc.RootElement.GetProperty("RemovedVersions").GetInt32() >= 1);
        Assert.True(doc.RootElement.GetProperty("SweptBlobs").GetInt32() >= 1);

        Assert.Equal(0, await VersionCount("Desk/gone.txt"));   // gone
        Assert.Equal(1, await VersionCount("Docs/keep.txt"));   // untouched
        Assert.DoesNotContain("gone.txt", _host.Handle(new IpcRequest { Command = IpcCommand.GetRecentFiles }).Json);
    }

    [Fact]
    public async Task Purge_unprotected_removes_exactly_the_orphaned_history()
    {
        File.WriteAllText(Path.Combine(_folderA, "a.txt"), "protected forever");
        File.WriteAllText(Path.Combine(_folderB, "b.txt"), "will be orphaned");
        await WaitForVersions("Docs/a.txt", 1);
        await WaitForVersions("Desk/b.txt", 1);

        RemoveFolderB();
        IpcResponse purge = _host.Handle(new IpcRequest { Command = IpcCommand.PurgeHistory, Arg1 = "unprotected" });
        Assert.True(purge.Ok);

        Assert.Equal(0, await VersionCount("Desk/b.txt"));
        Assert.Equal(1, await VersionCount("Docs/a.txt"));
    }

    [Fact]
    public async Task Purge_everything_empties_the_store_completely()
    {
        File.WriteAllText(Path.Combine(_folderA, "x.txt"), "some content");
        File.WriteAllText(Path.Combine(_folderB, "y.txt"), "more content");
        await WaitForVersions("Docs/x.txt", 1);
        await WaitForVersions("Desk/y.txt", 1);

        IpcResponse purge = _host.Handle(new IpcRequest { Command = IpcCommand.PurgeHistory, Arg1 = "*" });
        Assert.True(purge.Ok);

        Assert.Equal(0, await VersionCount("Docs/x.txt"));
        Assert.Equal(0, await VersionCount("Desk/y.txt"));
        Assert.Equal("[]", _host.Handle(new IpcRequest { Command = IpcCommand.GetRecentFiles }).Json);

        // Blob directory actually emptied (disk space really freed).
        string blobRoot = Path.Combine(_root, "store", "blobs");
        Assert.Empty(Directory.EnumerateFiles(blobRoot, "*", SearchOption.AllDirectories));

        // And capture still works afterwards — the store isn't wedged.
        File.WriteAllText(Path.Combine(_folderA, "x.txt"), "life after purge");
        await WaitForVersions("Docs/x.txt", 1);
        Assert.Equal(1, await VersionCount("Docs/x.txt"));
    }

    [Fact]
    public async Task Purge_a_single_files_history_leaves_siblings_alone()
    {
        File.WriteAllText(Path.Combine(_folderA, "one.txt"), "file one");
        File.WriteAllText(Path.Combine(_folderA, "two.txt"), "file two");
        await WaitForVersions("Docs/one.txt", 1);
        await WaitForVersions("Docs/two.txt", 1);

        Assert.True(_host.Handle(new IpcRequest { Command = IpcCommand.PurgeHistory, Arg1 = "Docs/one.txt" }).Ok);

        Assert.Equal(0, await VersionCount("Docs/one.txt"));
        Assert.Equal(1, await VersionCount("Docs/two.txt"));
    }

    [Fact]
    public void Retention_settings_are_configurable_and_clamped()
    {
        IpcResponse resp = _host.Handle(new IpcRequest
        {
            Command = IpcCommand.SetSettings,
            Arg1 = JsonSerializer.Serialize(new
            {
                RetentionKeepAllHours = 72,
                RetentionHourlyDays = 14,
                RetentionDailyDays = 180,
                RetentionMaxAgeDays = 0,       // nonsense — must clamp up to 1, not nuke history
                RetentionMaxVersionsPerFile = 500,
            }),
        });
        Assert.True(resp.Ok);
        Assert.Equal(72, _settings.Retention.KeepAllHours);
        Assert.Equal(14, _settings.Retention.HourlyDays);
        Assert.Equal(180, _settings.Retention.DailyDays);
        Assert.Equal(1, _settings.Retention.MaxAgeDays);
        Assert.Equal(500, _settings.Retention.MaxVersionsPerFile);
        Assert.Contains("\"RetentionKeepAllHours\":72", resp.Json);
    }

    public void Dispose()
    {
        _host.Dispose();
        try { Directory.Delete(_root, true); } catch { }
    }
}
