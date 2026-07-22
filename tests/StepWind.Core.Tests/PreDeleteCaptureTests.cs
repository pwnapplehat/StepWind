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

    [Fact]
    public async Task File_created_then_deleted_before_the_quiet_window_still_has_a_restorable_version()
    {
        // Quiet period 2s (the default). The create-baseline drain runs every ~250ms, so a file
        // that lives for ~1s — well under the debounce window — is captured before it's deleted.
        using var engine = new WatchEngine(_store, new PathExclusions(), [_watch],
            quietPeriod: TimeSpan.FromSeconds(2));

        string file = Path.Combine(_watch, "flash.txt");
        File.WriteAllText(file, "here and gone");

        // Give the fast create path time to grab a baseline, then delete before the 2s settle.
        await Task.Delay(900);
        File.Delete(file);

        // A version exists and reconstructs byte-exact even though the file is now gone.
        IReadOnlyList<FileVersion> history = _store.Log.History("watch/flash.txt");
        Assert.NotEmpty(history);

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
        await Task.Delay(900); // baseline captured

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
