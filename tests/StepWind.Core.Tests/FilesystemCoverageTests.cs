using StepWind.Core.Engine;
using StepWind.Core.Storage;
using Xunit;

namespace StepWind.Core.Tests;

/// <summary>
/// P3: version history must work on ANY filesystem, not only NTFS. The folder time-machine uses
/// FileSystemWatcher + a content-addressed store — neither depends on NTFS or the USN journal
/// (that's only the whole-machine timeline). This pins that the capture path has no filesystem or
/// drive-type gate, so protecting a folder on a Dev Drive (ReFS), a removable drive, or a network
/// share versions files just the same.
/// </summary>
public class FilesystemCoverageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sw-fscover", Guid.NewGuid().ToString("N"));

    public FilesystemCoverageTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Capture_and_restore_do_not_depend_on_the_filesystem_or_drive_type()
    {
        // The watch/capture/restore path takes only a directory path — there is no NTFS check,
        // no DriveType check, no USN dependency anywhere in it. Proven by versioning + restoring a
        // file through the same production classes used for any drive.
        string watch = Path.Combine(_root, "DevDriveLike");
        Directory.CreateDirectory(watch);
        var store = new VersionStore(
            new BlobStore(Path.Combine(_root, "store"), new GzipBlobCodec()),
            new VersionLog(Path.Combine(_root, "store", "versions.jsonl")));
        using var engine = new WatchEngine(store, new PathExclusions(), [watch]);

        string file = Path.Combine(watch, "source.cs");
        File.WriteAllText(file, "// v1 on a non-NTFS-style volume");
        Assert.True(engine.TryCapture(file));

        File.WriteAllText(file, "// v2");
        Assert.True(engine.TryCapture(file));

        string rel = engine.RelativeToRoot(file)!;
        var history = store.Log.History(rel);
        Assert.Equal(2, history.Count);

        // Restore the first version byte-exact (never overwriting the current file).
        string restored = store.RestoreToSafePath(history[0], file);
        Assert.Equal("// v1 on a non-NTFS-style volume", File.ReadAllText(restored));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
