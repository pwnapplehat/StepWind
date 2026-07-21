using System.Text.Json;
using StepWind.Core.Engine;
using StepWind.Core.Ipc;
using StepWind.Core.Storage;
using Xunit;

namespace StepWind.Core.Tests;

/// <summary>
/// The AI/MCP surface: GetVersionContent, DiffVersions, CaptureNow. These exist so an AI
/// coding agent (Cursor, Claude, etc.) can see what it changed in a protected folder, diff
/// against an earlier checkpoint, and restore — without needing history-deletion or settings
/// access. The core scenario under test throughout: checkpoint a file, let an "agent" edit it,
/// diff the checkpoint against what's on disk now, and confirm restoring the checkpoint
/// actually undoes the edit.
/// </summary>
public class AiMcpSurfaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "stepwind-ai", Guid.NewGuid().ToString("N"));
    private readonly string _watch;
    private readonly StepWindHost _host;

    public AiMcpSurfaceTests()
    {
        _watch = Path.Combine(_root, "Project");
        Directory.CreateDirectory(_watch);
        var settings = new StepWindSettings
        {
            StoreRoot = Path.Combine(_root, "store"),
            WatchedFolders = [_watch],
            FlightRecorderEnabled = false,
        };
        _host = new StepWindHost(settings, new GzipBlobCodec());

        // The host kicks off a BACKGROUND startup reconcile scan of the (currently empty)
        // watched folder. Under heavy parallel-test load that scan can still be mid-flight
        // when a test creates its first file a moment later; if reconcile's live directory
        // enumeration happens to observe it, it can capture the file (as "baseline") in a
        // race against this class's own explicit CaptureNow calls. Since the directory is
        // empty right now, the scan has nothing to do and settles near-instantly — this just
        // gives it that instant before any test creates a file, closing the race.
        Thread.Sleep(300);
    }

    private IpcResponse Send(IpcCommand cmd, string? arg1 = null, string? arg2 = null)
        => _host.Handle(new IpcRequest { Command = cmd, Arg1 = arg1, Arg2 = arg2 });

    /// <summary>Forces an immediate checkpoint via CaptureNow (no debounce wait) and returns the VersionEntry.</summary>
    private VersionEntry Checkpoint(string relOrAbsolutePath)
    {
        IpcResponse resp = Send(IpcCommand.CaptureNow, relOrAbsolutePath);
        Assert.True(resp.Ok, resp.Error);
        return JsonSerializer.Deserialize<VersionEntry>(resp.Json!)!;
    }

    private ContentResult ReadContent(string selector)
    {
        IpcResponse resp = Send(IpcCommand.GetVersionContent, selector);
        Assert.True(resp.Ok, resp.Error);
        return JsonSerializer.Deserialize<ContentResult>(resp.Json!)!;
    }

    private DiffResult Diff(string oldSelector, string newSelector)
    {
        IpcResponse resp = Send(IpcCommand.DiffVersions, oldSelector, newSelector);
        Assert.True(resp.Ok, resp.Error);
        return JsonSerializer.Deserialize<DiffResult>(resp.Json!)!;
    }

    [Fact]
    public void CaptureNow_creates_a_checkpoint_immediately_without_waiting_for_the_watcher()
    {
        string file = Path.Combine(_watch, "notes.txt");
        File.WriteAllText(file, "first draft");

        VersionEntry v = Checkpoint("Project/notes.txt");

        Assert.Equal("checkpoint", v.Reason);
        Assert.Equal("Project/notes.txt", v.RelativePath);
        Assert.Contains('|', v.VersionId);
    }

    [Fact]
    public void CaptureNow_accepts_an_absolute_path_too()
    {
        string file = Path.Combine(_watch, "abs.txt");
        File.WriteAllText(file, "content");

        VersionEntry v = Checkpoint(file); // absolute, not "Project/abs.txt"
        Assert.Equal("Project/abs.txt", v.RelativePath);
    }

    [Fact]
    public void CaptureNow_fails_cleanly_for_a_path_outside_any_watched_folder()
    {
        string outside = Path.Combine(Path.GetTempPath(), "stepwind-ai-outside-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(outside, "not protected");
        try
        {
            IpcResponse resp = Send(IpcCommand.CaptureNow, outside);
            Assert.False(resp.Ok);
            Assert.NotNull(resp.Error);
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public void GetVersionContent_latest_returns_the_most_recent_checkpoint_text()
    {
        string file = Path.Combine(_watch, "readme.md");
        File.WriteAllText(file, "version one");
        Checkpoint("Project/readme.md");
        File.WriteAllText(file, "version two");
        Checkpoint("Project/readme.md");

        ContentResult r = ReadContent("latest:Project/readme.md");
        Assert.Equal("version two", r.Content);
        Assert.False(r.IsBinary);
        Assert.False(r.Truncated);
        Assert.Contains("checkpoint", r.Label);
    }

    [Fact]
    public void GetVersionContent_by_exact_versionid_selects_that_specific_version_not_the_latest()
    {
        string file = Path.Combine(_watch, "a.txt");
        File.WriteAllText(file, "old content");
        VersionEntry first = Checkpoint("Project/a.txt");
        File.WriteAllText(file, "new content");
        Checkpoint("Project/a.txt");

        ContentResult r = ReadContent(first.VersionId);
        Assert.Equal("old content", r.Content);
    }

    [Fact]
    public void GetVersionContent_current_reflects_uncaptured_edits_the_watcher_has_not_seen_yet()
    {
        // This is the exact AI-agent scenario: an agent edits a file faster than the
        // watcher's debounce window, and needs to see the TRUE on-disk content right now,
        // not the stale last-captured version.
        string file = Path.Combine(_watch, "live.txt");
        File.WriteAllText(file, "checkpointed content");
        Checkpoint("Project/live.txt");

        File.WriteAllText(file, "an agent just changed this, unsaved by the watcher yet");

        ContentResult current = ReadContent("current:Project/live.txt");
        ContentResult latest = ReadContent("latest:Project/live.txt");

        Assert.Equal("an agent just changed this, unsaved by the watcher yet", current.Content);
        Assert.Equal("checkpointed content", latest.Content); // unchanged — watcher hasn't captured yet
        Assert.Contains("current on disk", current.Label);
    }

    [Fact]
    public void GetVersionContent_flags_binary_data_and_omits_content()
    {
        string file = Path.Combine(_watch, "image.bin");
        File.WriteAllBytes(file, [0x89, 0x50, 0x4E, 0x47, 0x00, 0x0D, 0x0A]);
        Checkpoint("Project/image.bin");

        ContentResult r = ReadContent("latest:Project/image.bin");
        Assert.True(r.IsBinary);
        Assert.Null(r.Content);
        Assert.Equal(7, r.Size);
    }

    [Fact]
    public void GetVersionContent_truncates_oversized_content_but_still_reports_the_real_size()
    {
        string file = Path.Combine(_watch, "big.txt");
        // Comfortably over the 4 MB cap.
        File.WriteAllText(file, new string('x', 5 * 1024 * 1024));
        Checkpoint("Project/big.txt");

        ContentResult r = ReadContent("latest:Project/big.txt");
        Assert.True(r.Truncated);
        Assert.Null(r.Content);
        Assert.Equal(5 * 1024 * 1024, r.Size);
    }

    [Fact]
    public void GetVersionContent_unknown_selector_fails_with_a_readable_error()
    {
        IpcResponse resp = Send(IpcCommand.GetVersionContent, "not-a-real-selector-format");
        Assert.False(resp.Ok);
        Assert.NotNull(resp.Error);
    }

    [Fact]
    public void DiffVersions_between_two_checkpoints_shows_only_the_real_change()
    {
        string file = Path.Combine(_watch, "doc.txt");
        File.WriteAllText(file, "line one\nline two\nline three");
        VersionEntry v1 = Checkpoint("Project/doc.txt");
        File.WriteAllText(file, "line one\nCHANGED\nline three");
        VersionEntry v2 = Checkpoint("Project/doc.txt");

        DiffResult d = Diff(v1.VersionId, v2.VersionId);
        Assert.False(d.Binary);
        Assert.Contains("-line two", d.Diff);
        Assert.Contains("+CHANGED", d.Diff);
        Assert.Contains(" line one", d.Diff);
    }

    [Fact]
    public void DiffVersions_latest_vs_current_reveals_what_an_agent_changed_since_the_checkpoint()
    {
        string file = Path.Combine(_watch, "code.cs");
        File.WriteAllText(file, "int x = 1;");
        Checkpoint("Project/code.cs");

        File.WriteAllText(file, "int x = 2; // agent edit");

        DiffResult d = Diff("latest:Project/code.cs", "current:Project/code.cs");
        Assert.Contains("-int x = 1;", d.Diff);
        Assert.Contains("+int x = 2; // agent edit", d.Diff);
    }

    [Fact]
    public void DiffVersions_reports_no_differences_when_content_is_identical()
    {
        string file = Path.Combine(_watch, "same.txt");
        File.WriteAllText(file, "unchanged");
        VersionEntry v = Checkpoint("Project/same.txt");

        DiffResult d = Diff(v.VersionId, "current:Project/same.txt");
        Assert.Equal("(no differences)", d.Diff);
    }

    [Fact]
    public void DiffVersions_fails_cleanly_when_a_side_does_not_resolve()
    {
        string file = Path.Combine(_watch, "exists.txt");
        File.WriteAllText(file, "content");
        VersionEntry v = Checkpoint("Project/exists.txt");

        IpcResponse resp = Send(IpcCommand.DiffVersions, v.VersionId, "latest:Project/does-not-exist.txt");
        Assert.False(resp.Ok);
        Assert.NotNull(resp.Error);
    }

    [Fact]
    public void Full_agent_workflow_checkpoint_edit_diff_and_restore_undoes_a_bad_edit()
    {
        // The exact loop an AI agent should follow: checkpoint before a risky change, make
        // the change, inspect the diff, and if it's wrong, restore the checkpoint.
        string file = Path.Combine(_watch, "config.json");
        File.WriteAllText(file, "{ \"setting\": \"safe-value\" }");
        VersionEntry checkpoint = Checkpoint("Project/config.json");

        // The "agent" makes a risky edit.
        File.WriteAllText(file, "{ \"setting\": \"BROKEN\" }");

        // The agent checks what it changed.
        DiffResult d = Diff(checkpoint.VersionId, "current:Project/config.json");
        Assert.Contains("BROKEN", d.Diff);
        Assert.Contains("safe-value", d.Diff);

        // It decides to undo — restore the checkpoint version (RestoreVersion never
        // overwrites in place, so this lands beside the broken file).
        IpcResponse restoreResp = Send(IpcCommand.RestoreVersion, checkpoint.VersionId);
        Assert.True(restoreResp.Ok, restoreResp.Error);
        string restoredPath = JsonDocument.Parse(restoreResp.Json!).RootElement.GetProperty("RestoredPath").GetString()!;

        Assert.Equal("{ \"setting\": \"safe-value\" }", File.ReadAllText(restoredPath));
        Assert.NotEqual(file, restoredPath); // never clobbered the (still-broken) live file
    }

    public void Dispose()
    {
        _host.Dispose();
        try { Directory.Delete(_root, true); } catch { }
    }
}
