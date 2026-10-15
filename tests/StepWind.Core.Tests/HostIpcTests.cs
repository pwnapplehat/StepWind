using System.Text.Json;
using StepWind.Core.Engine;
using StepWind.Core.Ipc;
using StepWind.Core.Journal;
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

    // ── Timeline paging with filters (HillsCloud, 1.0.8: "Show older" did nothing, creates vanished).
    //    The GUI filtered the newest N rows itself, so a burst of activity OUTSIDE the protected
    //    folders left nothing to show. The service must filter first and count the page after. ──

    private static FileOperation TlOp(OperationKind kind, int n, string path) => new()
    {
        Kind = kind,
        FileReferenceNumber = (ulong)(5000 + n),
        TimestampUtc = new DateTime(2026, 10, 14, 8, 0, 0, DateTimeKind.Utc).AddSeconds(n),
        Name = Path.GetFileName(path),
        OldPath = kind == OperationKind.Delete ? path : null,
        NewPath = kind == OperationKind.Delete ? null : path,
    };

    /// <summary>Newest first, as <c>TimelineLog.Recent</c> hands them over.</summary>
    private static List<FileOperation> NewestFirst(IEnumerable<FileOperation> ops)
        => [.. ops.OrderByDescending(o => o.TimestampUtc)];

    private List<TimelineEntry> Page(IEnumerable<FileOperation> ops, int limit, string? kind, string? scope)
        => _host.BuildTimelineFrom(NewestFirst(ops), 25000, limit, kind, scope, CallerContext.LocalTrusted);

    [Fact]
    public void Protected_only_finds_a_create_buried_under_a_thousand_newer_events_elsewhere()
    {
        string outside = Path.Combine(_root, "Elsewhere");
        var ops = new List<FileOperation> { TlOp(OperationKind.Create, 1, Path.Combine(_watch, "marker.txt")) };
        for (int i = 2; i <= 1500; i++)
        {
            ops.Add(TlOp(OperationKind.Create, i, Path.Combine(outside, $"noise-{i}.txt")));
        }

        // Without the scope filter the marker is past the first 1000 rows — this is what the user saw.
        Assert.DoesNotContain(Page(ops, 1000, null, "all"), e => e.Name == "marker.txt");

        // With it, the first page is made of protected rows, so the marker is right there.
        List<TimelineEntry> page = Page(ops, 1000, null, "protected");
        Assert.Single(page);
        Assert.Equal("marker.txt", page[0].Name);
    }

    [Fact]
    public void A_kind_filter_reaches_a_delete_older_than_a_thousand_newer_modifies_and_creates()
    {
        var ops = new List<FileOperation> { TlOp(OperationKind.Delete, 1, Path.Combine(_watch, "gone.docx")) };
        for (int i = 2; i <= 600; i++)
        {
            ops.Add(TlOp(OperationKind.Modify, i, Path.Combine(_watch, $"m-{i}.txt")));
            ops.Add(TlOp(OperationKind.Create, i + 1000, Path.Combine(_watch, $"c-{i}.txt")));
        }

        Assert.DoesNotContain(Page(ops, 1000, "", null), e => e.Name == "gone.docx");

        List<TimelineEntry> deletes = Page(ops, 1000, "Delete", null);
        Assert.Single(deletes);
        Assert.Equal("gone.docx", deletes[0].Name);
        Assert.Equal("Delete", deletes[0].Kind);
    }

    [Fact]
    public void A_full_protected_page_means_more_older_rows_exist_and_a_bigger_page_returns_them()
    {
        var ops = new List<FileOperation>();
        for (int i = 1; i <= 1500; i++)
        {
            // Every third row is outside, so the filter really has to skip rows to fill a page.
            string dir = i % 3 == 0 ? Path.Combine(_root, "Elsewhere") : _watch;
            ops.Add(TlOp(OperationKind.Create, i, Path.Combine(dir, $"f-{i}.txt")));
        }

        Assert.Equal(1000, Page(ops, 1000, null, "protected").Count); // full page: the GUI offers "Show older"
        Assert.Equal(1000, Page(ops, 2000, null, "protected").Count); // 1500 rows, 500 outside → exactly 1000 protected
    }

    [Fact]
    public void A_move_into_a_protected_folder_counts_as_protected_activity()
    {
        var move = new FileOperation
        {
            Kind = OperationKind.Move,
            FileReferenceNumber = 9001,
            TimestampUtc = new DateTime(2026, 10, 14, 9, 0, 0, DateTimeKind.Utc),
            Name = "report.docx",
            OldPath = Path.Combine(_root, "Elsewhere", "report.docx"),
            NewPath = Path.Combine(_watch, "report.docx"),
        };

        Assert.Single(Page([move], 100, null, "protected"));
    }

    [Fact]
    public void An_unknown_or_numeric_kind_is_ignored_rather_than_hiding_everything()
    {
        var ops = new[] { TlOp(OperationKind.Create, 1, Path.Combine(_watch, "a.txt")) };

        Assert.Single(Page(ops, 10, "Bogus", null));
        Assert.Single(Page(ops, 10, "99", null));
        Assert.Single(Page(ops, 10, "All", null));
    }

    [Fact]
    public async Task History_can_be_queried_by_absolute_path()
    {
        // The GUI's Browse button sends an absolute path; the host resolves it to the
        // store-relative path against the watched roots.
        string file = Path.Combine(_watch, "sub", "memo.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "v1");
        await WaitForCapture("Docs/sub/memo.txt");

        IpcResponse resp = _host.Handle(new IpcRequest { Command = IpcCommand.GetHistory, Arg1 = file });
        Assert.True(resp.Ok);
        Assert.NotEmpty(JsonSerializer.Deserialize<VersionEntry[]>(resp.Json!)!);
    }

    [Fact]
    public async Task GetRecentFiles_lists_distinct_files_most_recent_first()
    {
        string a = Path.Combine(_watch, "alpha.txt");
        string b = Path.Combine(_watch, "beta.txt");
        File.WriteAllText(a, "a1");
        await WaitForCapture("Docs/alpha.txt");
        File.WriteAllText(b, "b1");
        await WaitForCapture("Docs/beta.txt");
        File.WriteAllText(b, "b2"); // second version of beta
        // Wait until beta actually has its 2nd version (debounce quiet period is ~2s).
        for (int i = 0; i < 40; i++)
        {
            if (_host.Handle(new IpcRequest { Command = IpcCommand.GetHistory, Arg1 = "Docs/beta.txt" }) is { Ok: true } h
                && JsonSerializer.Deserialize<VersionEntry[]>(h.Json!)!.Length >= 2)
            {
                break;
            }

            await Task.Delay(250);
        }

        IpcResponse resp = _host.Handle(new IpcRequest { Command = IpcCommand.GetRecentFiles });
        Assert.True(resp.Ok);
        RecentFileEntry[] files = JsonSerializer.Deserialize<RecentFileEntry[]>(resp.Json!)!;

        Assert.Equal(2, files.Length);              // distinct files, not versions
        Assert.Equal("Docs/beta.txt", files[0].RelativePath); // most recently changed first
        Assert.True(files[0].VersionCount >= 2);
    }

    [Fact]
    public async Task Removing_a_folder_keeps_history_and_restores_to_an_accessible_path()
    {
        // Capture a version while the folder is protected…
        string file = Path.Combine(_watch, "keepme.txt");
        File.WriteAllText(file, "history must survive folder removal");
        await WaitForCapture("Docs/keepme.txt");

        // …then stop protecting the folder (what the ✕ on a folder card sends).
        IpcResponse set = _host.Handle(new IpcRequest
        {
            Command = IpcCommand.SetSettings,
            Arg1 = JsonSerializer.Serialize(new { WatchedFolders = new List<string>() }),
        });
        Assert.True(set.Ok);

        // History is NOT deleted: still listed and still restorable.
        IpcResponse hist = _host.Handle(new IpcRequest { Command = IpcCommand.GetHistory, Arg1 = "Docs/keepme.txt" });
        VersionEntry[] versions = JsonSerializer.Deserialize<VersionEntry[]>(hist.Json!)!;
        Assert.NotEmpty(versions);

        IpcResponse recent = _host.Handle(new IpcRequest { Command = IpcCommand.GetRecentFiles });
        Assert.Contains("keepme.txt", recent.Json);

        // The restore of an un-protected folder's file must land somewhere the user can
        // open (Public Documents fallback) — never inside the ACL-locked store.
        IpcResponse restore = _host.Handle(new IpcRequest
        {
            Command = IpcCommand.RestoreVersion,
            Arg1 = versions[0].VersionId,
        });
        Assert.True(restore.Ok);
        string restoredPath = JsonDocument.Parse(restore.Json!).RootElement.GetProperty("RestoredPath").GetString()!;
        Assert.DoesNotContain(Path.Combine(_root, "store"), restoredPath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("StepWind Restored", restoredPath);
        Assert.Equal("history must survive folder removal", File.ReadAllText(restoredPath));
        File.Delete(restoredPath); // tidy the shared Public Documents location
    }

    [Fact]
    public void SetSettings_adds_a_watched_folder_and_getsettings_reflects_it()
    {
        string extra = Path.Combine(_root, "Extra");
        Directory.CreateDirectory(extra);

        string patch = JsonSerializer.Serialize(new { WatchedFolders = new[] { _watch, extra } });
        IpcResponse set = _host.Handle(new IpcRequest { Command = IpcCommand.SetSettings, Arg1 = patch });
        Assert.True(set.Ok, set.Error);

        IpcResponse get = _host.Handle(new IpcRequest { Command = IpcCommand.GetSettings });
        using JsonDocument doc = JsonDocument.Parse(get.Json!);
        var folders = doc.RootElement.GetProperty("WatchedFolders").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains(extra, folders);
        Assert.Equal(2, folders.Count);
    }

    [Fact]
    public void SetSettings_ignores_nonexistent_folders()
    {
        string patch = JsonSerializer.Serialize(new { WatchedFolders = new[] { _watch, @"C:\definitely\not\here\xyz123" } });
        _host.Handle(new IpcRequest { Command = IpcCommand.SetSettings, Arg1 = patch });

        IpcResponse get = _host.Handle(new IpcRequest { Command = IpcCommand.GetSettings });
        using JsonDocument doc = JsonDocument.Parse(get.Json!);
        var folders = doc.RootElement.GetProperty("WatchedFolders").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Single(folders); // only the real one survived
    }

    [Fact]
    public async Task Newly_added_folder_starts_capturing()
    {
        string extra = Path.Combine(_root, "Later");
        Directory.CreateDirectory(extra);
        _host.Handle(new IpcRequest
        {
            Command = IpcCommand.SetSettings,
            Arg1 = JsonSerializer.Serialize(new { WatchedFolders = new[] { _watch, extra } }),
        });

        string file = Path.Combine(extra, "new.txt");
        File.WriteAllText(file, "hello");

        // The rebuilt watch engine should capture saves in the newly added folder.
        for (int i = 0; i < 40; i++)
        {
            IpcResponse h = _host.Handle(new IpcRequest { Command = IpcCommand.GetHistory, Arg1 = file });
            if (h.Ok && JsonSerializer.Deserialize<VersionEntry[]>(h.Json!)!.Length > 0)
            {
                return; // captured — pass
            }

            await Task.Delay(250);
        }

        Assert.Fail("newly added folder did not start capturing");
    }

    [Fact]
    public async Task A_deleted_protected_file_exposes_a_restorable_version_for_timeline_undo()
    {
        // P0-C: the timeline offers "Restore" on a deleted file that has saved history. This
        // pins the host mapping the flight recorder uses (RecoverableVersionFor) without needing
        // ETW/admin: a protected file with history yields a VersionId; restoring it recovers the
        // bytes to the original path once the file is gone.
        string file = Path.Combine(_watch, "deleteme.txt");
        File.WriteAllText(file, "recover me after delete");
        await WaitForCapture("Docs/deleteme.txt");

        string? versionId = _host.RecoverableVersionIdFor(file);
        Assert.NotNull(versionId);

        // A path that was never protected has nothing to recover — honest null, not a guess.
        Assert.Null(_host.RecoverableVersionIdFor(Path.Combine(_root, "Unprotected", "ghost.txt")));

        // The delete happens; the timeline's Restore action sends this VersionId to RestoreVersion.
        File.Delete(file);
        IpcResponse restore = _host.Handle(new IpcRequest { Command = IpcCommand.RestoreVersion, Arg1 = versionId });
        Assert.True(restore.Ok, restore.Error);

        string restoredPath = JsonDocument.Parse(restore.Json!).RootElement.GetProperty("RestoredPath").GetString()!;
        Assert.True(File.Exists(restoredPath));
        Assert.Equal("recover me after delete", File.ReadAllText(restoredPath));
    }

    [Fact]
    public void ReverseBatch_reports_per_item_and_never_throws_on_unknown_ops()
    {
        // Batch undo must attempt every item and report each — a partial failure can't silently
        // stop the rest. With no flight recorder (no admin here), every handle is unknown, which
        // exercises exactly the "reported, not thrown" contract.
        string payload = JsonSerializer.Serialize(new[] { "tok-a", "tok-b", "tok-c" });
        IpcResponse r = _host.Handle(new IpcRequest { Command = IpcCommand.ReverseBatch, Arg1 = payload });
        Assert.True(r.Ok, r.Error);

        using JsonDocument doc = JsonDocument.Parse(r.Json!);
        Assert.Equal(3, doc.RootElement.GetProperty("Total").GetInt32());
        Assert.Equal(0, doc.RootElement.GetProperty("Succeeded").GetInt32());
        Assert.Equal(3, doc.RootElement.GetProperty("Failed").GetInt32());
        Assert.Equal(3, doc.RootElement.GetProperty("Results").GetArrayLength());

        // An empty batch is a clean error, not a crash.
        Assert.False(_host.Handle(new IpcRequest { Command = IpcCommand.ReverseBatch, Arg1 = "[]" }).Ok);
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
