using StepWind.Core.Engine;
using StepWind.Core.Storage;
using Xunit;

namespace StepWind.Core.Tests;

/// <summary>
/// P0-B: a file created and then deleted quickly must still leave a restorable version — the
/// prerequisite for undoing a delete. Before the fast create-baseline path, a create+delete
/// inside the debounce quiet window left ZERO stored versions (the debounced capture found the
/// file already gone), so the "undo a delete" promise was unbackable in that window.
/// </summary>
public class PreDeleteCaptureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "stepwind-predelete", Guid.NewGuid().ToString("N"));
    private readonly string _watch;
    private readonly VersionStore _store;

    public PreDeleteCaptureTests()
    {
        _watch = Path.Combine(_root, "watch");
        Directory.CreateDirectory(_watch);
        _store = new VersionStore(
            new BlobStore(Path.Combine(_root, "store"), new GzipBlobCodec()),
            new VersionLog(Path.Combine(_root, "store", "versions.jsonl")));
    }

    // Polls until a version exists for the relative path (deterministic — no fixed sleeps that
    // can lose the race under load). Fails the test if nothing is captured within the timeout,
    // which would be a real regression, not flakiness. The default cap stays UNDER the 2s debounce
    // so a version appearing means the fast create-baseline path produced it, not the settle pass.
    private async Task<bool> WaitForBaselineAsync(string rel, int timeoutMs = 1800)
    {
        for (int waited = 0; waited < timeoutMs; waited += 50)
        {
            if (_store.Log.History(rel).Count > 0)
            {
                return true;
            }

            await Task.Delay(50);
        }

        return _store.Log.History(rel).Count > 0;
    }

    [Fact]
    public async Task File_created_then_deleted_before_the_quiet_window_still_has_a_restorable_version()
    {
        // Quiet period 2s. The create-baseline drain runs every ~250ms, so the file is captured
        // well before the 2s settle — we wait for that baseline (deterministically), then delete.
        using var engine = new WatchEngine(_store, new PathExclusions(), [_watch],
            quietPeriod: TimeSpan.FromSeconds(2));

        string file = Path.Combine(_watch, "flash.txt");
        File.WriteAllText(file, "here and gone");

        Assert.True(await WaitForBaselineAsync("watch/flash.txt"), "create-baseline did not capture before the settle window");
        File.Delete(file); // now it's gone — but a version already exists

        IReadOnlyList<FileVersion> history = _store.Log.History("watch/flash.txt");
        Assert.NotEmpty(history);
        Assert.Equal("create", history[0].Reason); // proves it came from the fast path, not the debounce

        using var ms = new MemoryStream();
        _store.WriteContent(history[^1], ms);
        Assert.Equal("here and gone", System.Text.Encoding.UTF8.GetString(ms.ToArray()));
    }

    [Fact]
    public async Task Edit_then_delete_before_settle_leaves_the_prior_baseline_restorable()
    {
        using var engine = new WatchEngine(_store, new PathExclusions(), [_watch],
            quietPeriod: TimeSpan.FromSeconds(2));

        string file = Path.Combine(_watch, "notes.txt");
        File.WriteAllText(file, "original content");
        Assert.True(await WaitForBaselineAsync("watch/notes.txt"), "baseline did not capture");

        // Edit and immediately delete, faster than the 2s debounce could capture the edit.
        File.WriteAllText(file, "edited but about to vanish");
        File.Delete(file);

        // At minimum the baseline survives — a restorable version exists, never zero.
        IReadOnlyList<FileVersion> history = _store.Log.History("watch/notes.txt");
        Assert.NotEmpty(history);
        using var ms = new MemoryStream();
        _store.WriteContent(history[0], ms);
        Assert.Equal("original content", System.Text.Encoding.UTF8.GetString(ms.ToArray()));
    }

    [Fact]
    public async Task Create_baseline_dedups_with_the_settled_capture_for_an_unchanged_file()
    {
        // The fast baseline must not bloat history: an unchanged file that settles produces the
        // same content, so version-level dedup collapses it to a single version.
        using var engine = new WatchEngine(_store, new PathExclusions(), [_watch],
            quietPeriod: TimeSpan.FromMilliseconds(400));

        string file = Path.Combine(_watch, "stable.txt");
        File.WriteAllText(file, "written once, never touched again");

        await Task.Delay(2500); // baseline + settle both run
        Assert.Single(_store.Log.History("watch/stable.txt"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
