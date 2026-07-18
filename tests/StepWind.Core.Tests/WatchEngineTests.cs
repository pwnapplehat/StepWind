using StepWind.Core.Engine;
using StepWind.Core.Storage;
using Xunit;

namespace StepWind.Core.Tests;

public class ChangeDebouncerTests
{
    [Fact]
    public void A_path_is_ready_only_after_it_goes_quiet()
    {
        var d = new ChangeDebouncer { QuietPeriod = TimeSpan.FromSeconds(2) };
        var t0 = DateTime.UtcNow;

        d.Touch(@"C:\a.txt", t0);
        Assert.Empty(d.TakeReady(t0.AddSeconds(1)));           // still within quiet window
        d.Touch(@"C:\a.txt", t0.AddSeconds(1));                // another write resets the timer
        Assert.Empty(d.TakeReady(t0.AddSeconds(2.5)));         // 1.5s since last write
        Assert.Single(d.TakeReady(t0.AddSeconds(3.2)));        // now quiet 2.2s
    }

    [Fact]
    public void Ready_paths_are_taken_once()
    {
        var d = new ChangeDebouncer { QuietPeriod = TimeSpan.FromSeconds(1) };
        var t0 = DateTime.UtcNow;
        d.Touch(@"C:\a.txt", t0);
        Assert.Single(d.TakeReady(t0.AddSeconds(2)));
        Assert.Empty(d.TakeReady(t0.AddSeconds(3))); // already consumed
    }
}

public class WatchEngineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "stepwind-we", Guid.NewGuid().ToString("N"));
    private readonly string _watch;
    private readonly VersionStore _store;

    public WatchEngineTests()
    {
        _watch = Path.Combine(_root, "watch");
        Directory.CreateDirectory(_watch);
        _store = new VersionStore(
            new BlobStore(Path.Combine(_root, "store"), new GzipBlobCodec()),
            new VersionLog(Path.Combine(_root, "store", "versions.jsonl")));
    }

    private WatchEngine NewEngine() => new(_store, new PathExclusions(), [_watch]);

    [Fact]
    public void Direct_capture_versions_a_real_file()
    {
        using WatchEngine engine = NewEngine();
        string file = Path.Combine(_watch, "doc.txt");
        File.WriteAllText(file, "hello");

        Assert.True(engine.TryCapture(file));
        string rel = engine.RelativeToRoot(file)!;
        Assert.Single(_store.Log.History(rel));
    }

    [Fact]
    public void Capture_skips_excluded_files()
    {
        using WatchEngine engine = NewEngine();
        Directory.CreateDirectory(Path.Combine(_watch, "node_modules"));
        string junk = Path.Combine(_watch, "node_modules", "x.js");
        File.WriteAllText(junk, "code");

        Assert.False(engine.TryCapture(junk));
    }

    [Fact]
    public void Relative_path_is_rooted_at_the_watched_folder_name()
    {
        using WatchEngine engine = NewEngine();
        string file = Path.Combine(_watch, "sub", "a.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "x");

        string rel = engine.RelativeToRoot(file)!;
        Assert.Equal("watch/sub/a.txt", rel);
    }

    [Fact]
    public async Task Live_watcher_captures_a_settled_save()
    {
        using var engine = new WatchEngine(_store, new PathExclusions(), [_watch],
            quietPeriod: TimeSpan.FromMilliseconds(300));
        string file = Path.Combine(_watch, "live.txt");

        File.WriteAllText(file, "v1");
        await Task.Delay(2500); // let the watcher fire + debounce + drain timer run

        Assert.True(_store.Log.History("watch/live.txt").Count >= 1);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
