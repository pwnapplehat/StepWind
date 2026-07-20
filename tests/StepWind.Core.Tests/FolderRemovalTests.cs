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
    public async Task Disposing_the_engine_aborts_an_inflight_baseline_scan()
    {
        // The real incident: a folder with THOUSANDS of files is added (baseline scan starts
        // in the background), then the user removes it — the scan must stop, not keep
        // capturing the whole tree to completion.
        string big = Path.Combine(_root, "BigFolder");
        Directory.CreateDirectory(big);
        for (int i = 0; i < 400; i++)
        {
            File.WriteAllText(Path.Combine(big, $"file{i:D4}.txt"), $"content of file {i} " + new string('x', 600));
        }

        var store = new VersionStore(
            new BlobStore(Path.Combine(_root, "store2"), new GzipBlobCodec()),
            new VersionLog(Path.Combine(_root, "store2", "versions.jsonl")));
        var engine = new WatchEngine(store, new PathExclusions(), [big]);

        Task<int> scan = Task.Run(() => engine.Reconcile());

        // Wait until the baseline is visibly under way, then "remove the folder".
        for (int i = 0; i < 200 && store.Log.All.Count < 20; i++)
        {
            await Task.Delay(25);
        }

        Assert.True(store.Log.All.Count >= 20, "baseline never started");
        engine.Dispose();
        int captured = await scan;

        Assert.True(captured < 400, $"scan ran to completion ({captured}) despite dispose");

        // And nothing keeps trickling in afterwards.
        int afterDispose = store.Log.All.Count;
        await Task.Delay(1500);
        Assert.Equal(afterDispose, store.Log.All.Count);
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

    [Fact]
    public async Task Browse_navigates_the_store_as_a_folder_tree()
    {
        // Build a small tree under two protected roots.
        Directory.CreateDirectory(Path.Combine(_folderA, "Work", "ProjectX"));
        File.WriteAllText(Path.Combine(_folderA, "top.txt"), "top of Docs");
        File.WriteAllText(Path.Combine(_folderA, "Work", "notes.txt"), "work notes");
        File.WriteAllText(Path.Combine(_folderA, "Work", "ProjectX", "main.cs"), "code");
        File.WriteAllText(Path.Combine(_folderB, "desk.txt"), "on the desk");
        await WaitForVersions("Docs/top.txt", 1);
        await WaitForVersions("Docs/Work/notes.txt", 1);
        await WaitForVersions("Docs/Work/ProjectX/main.cs", 1);
        await WaitForVersions("Desk/desk.txt", 1);

        // Root lists the two protected folders as drill-in entries.
        BrowseEntry[] root = Browse("", null);
        Assert.Contains(root, e => e is { IsFolder: true, Name: "Docs" });
        Assert.Contains(root, e => e is { IsFolder: true, Name: "Desk" });

        // Into Docs: a subfolder "Work" plus the direct file "top.txt".
        BrowseEntry[] docs = Browse("Docs", null);
        BrowseEntry workFolder = Assert.Single(docs, e => e.IsFolder && e.Name == "Work");
        Assert.Equal(2, workFolder.FileCount); // notes.txt + ProjectX/main.cs beneath
        Assert.Contains(docs, e => e is { IsFolder: false, Name: "top.txt" });

        // Into Docs/Work: subfolder ProjectX + file notes.txt.
        BrowseEntry[] workChildren = Browse("Docs/Work", null);
        Assert.Contains(workChildren, e => e is { IsFolder: true, Name: "ProjectX" });
        Assert.Contains(workChildren, e => e is { IsFolder: false, Name: "notes.txt" });

        // Leaf folder shows the file only.
        BrowseEntry[] proj = Browse("Docs/Work/ProjectX", null);
        Assert.Single(proj);
        Assert.Equal("main.cs", proj[0].Name);
        Assert.False(proj[0].IsFolder);
    }

    [Fact]
    public async Task Browse_search_finds_files_recursively_under_the_current_folder()
    {
        Directory.CreateDirectory(Path.Combine(_folderA, "Work"));
        File.WriteAllText(Path.Combine(_folderA, "Work", "budget.xlsx"), "numbers");
        File.WriteAllText(Path.Combine(_folderA, "readme.txt"), "docs readme");
        File.WriteAllText(Path.Combine(_folderB, "budget-desk.xlsx"), "other numbers");
        await WaitForVersions("Docs/Work/budget.xlsx", 1);
        await WaitForVersions("Docs/readme.txt", 1);
        await WaitForVersions("Desk/budget-desk.xlsx", 1);

        // Global search from root matches both budgets.
        BrowseEntry[] all = Browse("", "budget");
        Assert.Equal(2, all.Length);
        Assert.All(all, e => Assert.False(e.IsFolder));

        // Scoped search under Docs only matches the one beneath Docs.
        BrowseEntry[] scoped = Browse("Docs", "budget");
        Assert.Single(scoped);
        Assert.Equal("Docs/Work/budget.xlsx", scoped[0].RelativePath);
    }

    [Fact]
    public void Flight_recorder_toggles_live_or_fails_honestly()
    {
        static bool StatusSaysRecorder(StepWindHost host)
        {
            IpcResponse st = host.Handle(new IpcRequest { Command = IpcCommand.GetStatus });
            using JsonDocument doc = JsonDocument.Parse(st.Json!);
            return doc.RootElement.GetProperty("FlightRecorder").GetBoolean();
        }

        Assert.False(StatusSaysRecorder(_host)); // constructed with it off

        IpcResponse on = _host.Handle(new IpcRequest
        {
            Command = IpcCommand.SetSettings,
            Arg1 = JsonSerializer.Serialize(new { FlightRecorderEnabled = true }),
        });

        if (on.Ok)
        {
            // Privileged environment: it must actually be running now, and stop cleanly.
            Assert.True(StatusSaysRecorder(_host));
            Assert.True(_settings.FlightRecorderEnabled);

            IpcResponse off = _host.Handle(new IpcRequest
            {
                Command = IpcCommand.SetSettings,
                Arg1 = JsonSerializer.Serialize(new { FlightRecorderEnabled = false }),
            });
            Assert.True(off.Ok);
            Assert.False(StatusSaysRecorder(_host));
            Assert.False(_settings.FlightRecorderEnabled);
        }
        else
        {
            // Unprivileged environment: the failure must be honest — clear error, setting
            // NOT flipped, recorder NOT reported as running.
            Assert.Contains("flight recorder", on.Error, StringComparison.OrdinalIgnoreCase);
            Assert.False(_settings.FlightRecorderEnabled);
            Assert.False(StatusSaysRecorder(_host));
        }
    }

    private BrowseEntry[] Browse(string prefix, string? query)
    {
        IpcResponse resp = _host.Handle(new IpcRequest
        {
            Command = IpcCommand.BrowseVersions,
            Arg1 = prefix,
            Arg2 = query,
            Limit = 500,
        });
        Assert.True(resp.Ok);
        return JsonSerializer.Deserialize<BrowseEntry[]>(resp.Json!)!;
    }

    public void Dispose()
    {
        _host.Dispose();
        try { Directory.Delete(_root, true); } catch { }
    }
}
