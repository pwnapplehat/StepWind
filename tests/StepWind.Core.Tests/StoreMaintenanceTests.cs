using StepWind.Core.Storage;
using Xunit;

namespace StepWind.Core.Tests;

/// <summary>
/// P1-5: a backup product must be able to answer "is my history actually restorable?" and
/// reclaim space when it isn't. These pin verify (detects a version whose chunk was lost),
/// repair (quarantines only the damaged version, keeps the good ones, sweeps orphans), and the
/// index backup/restore that guards against a corrupt index orphaning the whole store.
/// </summary>
public class StoreMaintenanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "stepwind-maint", Guid.NewGuid().ToString("N"));
    private readonly string _watch;

    public StoreMaintenanceTests()
    {
        _watch = Path.Combine(_root, "Docs");
        Directory.CreateDirectory(_watch);
    }

    private string StoreRoot => Path.Combine(_root, "store");
    private string BlobRoot => Path.Combine(StoreRoot, "blobs");

    private VersionStore NewStore() => new(
        new BlobStore(StoreRoot, new GzipBlobCodec()),
        new VersionLog(Path.Combine(StoreRoot, "versions.jsonl")));

    private FileVersion Capture(VersionStore store, string name, string content)
    {
        string file = Path.Combine(_watch, name);
        File.WriteAllText(file, content);
        return store.Capture(file, "Docs/" + name);
    }

    [Fact]
    public void Verify_passes_a_healthy_store()
    {
        VersionStore store = NewStore();
        Capture(store, "a.txt", "alpha");
        Capture(store, "b.txt", "bravo");

        VerifyReport r = StoreMaintenance.Verify(store.Log, store.Blobs);
        Assert.Equal(2, r.TotalVersions);
        Assert.Equal(2, r.OkVersions);
        Assert.Equal(0, r.UnrestorableVersions);
        Assert.Equal(0, r.OrphanBlobs);
    }

    [Fact]
    public void Verify_flags_a_version_whose_chunk_was_lost()
    {
        VersionStore store = NewStore();
        Capture(store, "keep.txt", "still here");
        Capture(store, "gone.txt", "about to lose a chunk " + new string('x', 500));

        // Simulate on-disk loss: delete every blob (both files' chunks). "keep" and "gone" have
        // distinct content, so deleting all blobs makes both unrestorable — delete just one file's.
        FileVersion gone = store.Log.History("Docs/gone.txt")[0];
        foreach (string hex in gone.Chunks)
        {
            File.Delete(Path.Combine(BlobRoot, BlobId.Parse(hex).RelativePath));
        }

        VerifyReport r = StoreMaintenance.Verify(store.Log, store.Blobs);
        Assert.Equal(2, r.TotalVersions);
        Assert.Equal(1, r.UnrestorableVersions);
        Assert.True(r.MissingChunks >= 1);
    }

    [Fact]
    public void Repair_removes_only_the_unrestorable_version_and_keeps_the_good_one()
    {
        VersionStore store = NewStore();
        Capture(store, "keep.txt", "healthy content");
        FileVersion gone = Capture(store, "gone.txt", "doomed content " + new string('y', 400));
        foreach (string hex in gone.Chunks)
        {
            File.Delete(Path.Combine(BlobRoot, BlobId.Parse(hex).RelativePath));
        }

        VerifyReport r = StoreMaintenance.Repair(store.Log, store.Blobs);
        Assert.Equal(1, r.RemovedVersions);

        Assert.Empty(store.Log.History("Docs/gone.txt"));       // damaged version quarantined
        Assert.Single(store.Log.History("Docs/keep.txt"));      // good version untouched

        // The surviving version still restores byte-exact.
        using var ms = new MemoryStream();
        store.WriteContent(store.Log.History("Docs/keep.txt")[0], ms);
        Assert.Equal("healthy content", System.Text.Encoding.UTF8.GetString(ms.ToArray()));
    }

    [Fact]
    public void Index_backup_and_restore_round_trips()
    {
        VersionStore store = NewStore();
        Capture(store, "a.txt", "one");
        Capture(store, "b.txt", "two");
        store.Log.Backup(); // snapshot the healthy index

        // Wipe the live index entirely (simulate a truncated/corrupt versions.jsonl), reopen.
        File.WriteAllText(Path.Combine(StoreRoot, "versions.jsonl"), "");
        VersionStore reopened = NewStore();
        Assert.Empty(reopened.Log.All); // index is gone

        int recovered = reopened.Log.RestoreFromBackup();
        Assert.Equal(2, recovered);
        Assert.Single(reopened.Log.History("Docs/a.txt"));
        Assert.Single(reopened.Log.History("Docs/b.txt"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
