using StepWind.Core.Engine;
using StepWind.Core.Storage;
using Xunit;

namespace StepWind.Core.Tests;

/// <summary>
/// P0-E1: StepWind must never fill the disk or silently stop protecting when space runs out —
/// the exact failure that made people distrust Windows File History. These pin the pause/resume
/// decision and that a paused engine skips captures rather than throwing or losing the file
/// (a reconcile re-captures once space returns).
/// </summary>
public class StorageGuardTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "stepwind-storage", Guid.NewGuid().ToString("N"));

    public StorageGuardTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void A_huge_free_space_floor_pauses_capture()
    {
        // Demand more free space than any drive has → always paused, with a clear reason.
        var guard = new StorageGuard(_root, minFreeBytes: long.MaxValue, maxStoreBytes: 0);
        StorageState state = guard.Evaluate(storeBytes: 0);

        Assert.True(state.Paused);
        Assert.Contains("disk space", state.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_tiny_floor_does_not_pause_on_a_normal_drive()
    {
        var guard = new StorageGuard(_root, minFreeBytes: 1, maxStoreBytes: 0);
        Assert.False(guard.Evaluate(storeBytes: 0).Paused);
    }

    [Fact]
    public void An_exceeded_store_quota_pauses_capture()
    {
        var guard = new StorageGuard(_root, minFreeBytes: 1, maxStoreBytes: 1000);
        Assert.True(guard.Evaluate(storeBytes: 1000).Paused);
        Assert.False(guard.Evaluate(storeBytes: 999).Paused);
    }

    [Fact]
    public void Zero_min_free_uses_the_safe_default_not_unlimited()
    {
        var guard = new StorageGuard(_root, minFreeBytes: 0, maxStoreBytes: 0);
        Assert.Equal(StorageGuard.DefaultMinFreeBytes, guard.MinFreeBytes);
    }

    [Fact]
    public void A_paused_engine_skips_capture_but_captures_again_once_resumed()
    {
        string watch = Path.Combine(_root, "watch");
        Directory.CreateDirectory(watch);
        var store = new VersionStore(
            new BlobStore(Path.Combine(_root, "store"), new GzipBlobCodec()),
            new VersionLog(Path.Combine(_root, "store", "versions.jsonl")));

        bool paused = true;
        using var engine = new WatchEngine(store, new PathExclusions(), [watch], canCapture: () => !paused);

        string file = Path.Combine(watch, "doc.txt");
        File.WriteAllText(file, "content while disk is full");

        // Paused: capture is skipped, no version stored, and nothing throws.
        Assert.False(engine.TryCapture(file));
        Assert.Empty(store.Log.History("watch/doc.txt"));

        // Space returns: the very next capture (or reconcile) stores the file — nothing was lost.
        paused = false;
        Assert.True(engine.TryCapture(file));
        Assert.Single(store.Log.History("watch/doc.txt"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
