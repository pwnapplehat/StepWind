using System.IO;
using StepWind.Core.Engine;
using StepWind.Core.Storage;
using Xunit;

namespace StepWind.Core.Tests;

/// <summary>
/// HillsCloud, 1.0.9: "0 versions, 0 B ... Every file scanned is excluded, so nothing was saved" for
/// folders that were not excluded. The folder-name rule (build, dist, out, bin, env ...) looked at the
/// WHOLE path, including the folders above the protected folder, so a protected folder that sits
/// under — or is itself named — one of those had every file skipped. The scan note also reported
/// "excluded" for files that were skipped or failed for other reasons.
/// </summary>
public class ProtectedRootExclusionTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "stepwind-rootexcl", Guid.NewGuid().ToString("N"));

    private VersionStore NewStore()
        => new(new BlobStore(Path.Combine(_tmp, "store"), new GzipBlobCodec()),
               new VersionLog(Path.Combine(_tmp, "store", "versions.jsonl")));

    [Theory]
    [InlineData(@"D:\build\Docs\a.txt", @"D:\build\Docs")]
    [InlineData(@"D:\Work\dist\a.txt", @"D:\Work\dist")]            // the protected folder itself is named "dist"
    [InlineData(@"D:\out\bin\Projects\notes\a.txt", @"D:\out\bin\Projects")]
    [InlineData(@"D:\env\Docs\sub\a.txt", @"D:\env\Docs\")]          // trailing separator on the root
    public void A_protected_folder_is_never_excluded_by_its_own_path(string file, string root)
    {
        var ex = new PathExclusions();
        Assert.True(ex.ShouldVersion(file, FileAttributes.Normal, 100, root));
        Assert.Null(ex.ExclusionReason(file, root));

        // Without a root the old whole-path judgement still applies (callers that have no root).
        Assert.False(ex.ShouldVersion(file, FileAttributes.Normal, 100));
    }

    [Theory]
    [InlineData(@"D:\build\Docs\node_modules\x.js", @"D:\build\Docs", "node_modules")]
    [InlineData(@"D:\Work\Docs\obj\x.dll", @"D:\Work\Docs", "obj")]
    [InlineData(@"D:\Work\Docs\sub\.git\config", @"D:\Work\Docs", ".git")]
    public void Folders_below_the_protected_folder_are_still_excluded(string file, string root, string name)
    {
        var ex = new PathExclusions();
        Assert.False(ex.ShouldVersion(file, FileAttributes.Normal, 100, root));
        Assert.Equal($"in a folder named \"{name}\"", ex.ExclusionReason(file, root));
    }

    [Fact]
    public void A_sibling_root_with_a_matching_prefix_is_not_mistaken_for_the_root()
    {
        var ex = new PathExclusions();
        // "D:\Work\Docs2" is not inside "D:\Work\Docs", so the root must not be stripped from it.
        Assert.False(ex.ShouldVersion(@"D:\Work\Docs2\build\a.txt", FileAttributes.Normal, 100, @"D:\Work\Docs"));
    }

    [Fact]
    public void Reconcile_saves_files_in_a_protected_folder_that_sits_under_build_and_dist()
    {
        string root = Path.Combine(_tmp, "build", "dist", "Docs");
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        File.WriteAllText(Path.Combine(root, "a.txt"), "one");
        File.WriteAllText(Path.Combine(root, "sub", "b.txt"), "two");
        Directory.CreateDirectory(Path.Combine(root, "node_modules"));
        File.WriteAllText(Path.Combine(root, "node_modules", "skip.js"), "junk");

        VersionStore store = NewStore();
        using var engine = new WatchEngine(store, new PathExclusions(), [root]);

        Assert.Equal(2, engine.Reconcile());
        Assert.Single(store.Log.History("Docs/a.txt"));
        Assert.Single(store.Log.History("Docs/sub/b.txt"));
        Assert.Empty(store.Log.History("Docs/node_modules/skip.js"));
        Assert.Equal("Saved 2 file(s).", engine.CatchUp.Note);
    }

    [Fact]
    public void A_live_save_in_a_protected_folder_under_build_is_captured()
    {
        string root = Path.Combine(_tmp, "out", "Docs");
        Directory.CreateDirectory(root);
        string file = Path.Combine(root, "live.txt");
        File.WriteAllText(file, "hello");

        VersionStore store = NewStore();
        using var engine = new WatchEngine(store, new PathExclusions(), [root]);

        Assert.True(engine.TryCapture(file));
        Assert.Single(store.Log.History("Docs/live.txt"));
    }

    [Fact]
    public void The_scan_note_names_the_real_rule_and_count_instead_of_saying_everything_is_excluded()
    {
        string root = Path.Combine(_tmp, "Logs");
        Directory.CreateDirectory(root);
        for (int i = 0; i < 4; i++)
        {
            File.WriteAllText(Path.Combine(root, $"run-{i}.log"), "x");
        }

        using var engine = new WatchEngine(NewStore(), new PathExclusions(), [root]);
        Assert.Equal(0, engine.Reconcile());

        Assert.Equal("Nothing new was saved: 4 skipped by exclusion rules (4 .log files).", engine.CatchUp.Note);
        Assert.DoesNotContain("Every file scanned is excluded", engine.CatchUp.Note);
    }

    [Fact]
    public void A_file_that_cannot_be_read_is_reported_as_not_saved_not_as_excluded()
    {
        string root = Path.Combine(_tmp, "Docs");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "skip.log"), "excluded by type");
        string locked = Path.Combine(root, "locked.docx");
        File.WriteAllText(locked, "held open exclusively");

        VersionStore store = NewStore();
        using var engine = new WatchEngine(store, new PathExclusions(), [root]);
        using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(0, engine.Reconcile());
        }

        string note = engine.CatchUp.Note;
        Assert.Contains("1 could not be saved (", note);
        Assert.Contains("1 skipped by exclusion rules (1 .log files)", note);
        Assert.DoesNotContain("Every file scanned is excluded", note);
    }

    [Fact]
    public void DescribeScan_reports_each_reason_with_its_own_count()
    {
        var why = new Dictionary<string, int> { ["in a folder named \"build\""] = 30, [".log files"] = 2 };
        Assert.Equal(
            "Nothing new was saved: 5 could not be saved (IOException: busy); 7 are online-only cloud copies, so they were not saved; " +
            "32 skipped by exclusion rules (30 in a folder named \"build\", 2 .log files); " +
            "3 skipped (too large, a link, or ignored by .gitignore); 9 already saved.",
            WatchEngine.DescribeScan(0, 7, why, 3, 5, "IOException: busy", 9));

        Assert.Equal("Saved 4 file(s).", WatchEngine.DescribeScan(4, 1, why, 1, 1, "x", 1));
        Assert.Equal("Scan finished and nothing was saved.",
            WatchEngine.DescribeScan(0, 0, new Dictionary<string, int>(), 0, 0, null, 0));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* best effort */ }
    }
}
