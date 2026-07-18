using System.Text;
using StepWind.Core.Storage;
using Xunit;

namespace StepWind.Core.Tests;

public class VersionStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "stepwind-vs", Guid.NewGuid().ToString("N"));
    private readonly string _work;

    public VersionStoreTests()
    {
        _work = Path.Combine(_root, "work");
        Directory.CreateDirectory(_work);
    }

    private VersionStore NewStore()
        => new(new BlobStore(Path.Combine(_root, "store"), new GzipBlobCodec()),
               new VersionLog(Path.Combine(_root, "store", "versions.jsonl")));

    [Fact]
    public void Captures_versions_and_restores_each_byte_exact()
    {
        VersionStore store = NewStore();
        string file = Path.Combine(_work, "novel.txt");

        File.WriteAllText(file, "chapter one — the good version");
        FileVersion v1 = store.Capture(file, "novel.txt");
        File.WriteAllText(file, "chapter one — RUINED");
        FileVersion v2 = store.Capture(file, "novel.txt");

        // Overwrite and delete the live file entirely.
        File.WriteAllText(file, "garbage");
        File.Delete(file);

        string restored1 = store.RestoreToSafePath(v1, Path.Combine(_work, "novel.txt"));
        Assert.Equal("chapter one — the good version", File.ReadAllText(restored1));

        using var ms = new MemoryStream();
        store.WriteContent(v2, ms);
        Assert.Equal("chapter one — RUINED", Encoding.UTF8.GetString(ms.ToArray()));
    }

    [Fact]
    public void Restore_never_overwrites_an_existing_file()
    {
        VersionStore store = NewStore();
        string file = Path.Combine(_work, "keep.txt");
        File.WriteAllText(file, "version A");
        FileVersion v = store.Capture(file, "keep.txt");

        // The live file now holds DIFFERENT, current work.
        File.WriteAllText(file, "current work I must not lose");

        string restored = store.RestoreToSafePath(v, file);

        Assert.NotEqual(file, restored); // wrote alongside, not over
        Assert.Equal("current work I must not lose", File.ReadAllText(file));
        Assert.Equal("version A", File.ReadAllText(restored));
    }

    [Fact]
    public void History_lists_versions_oldest_first()
    {
        VersionStore store = NewStore();
        string file = Path.Combine(_work, "doc.txt");
        for (int i = 0; i < 4; i++)
        {
            File.WriteAllText(file, "content " + i);
            store.Capture(file, "doc.txt");
        }

        IReadOnlyList<FileVersion> history = store.Log.History("doc.txt");
        Assert.Equal(4, history.Count);
        for (int i = 1; i < history.Count; i++)
        {
            Assert.True(history[i].CapturedUtc >= history[i - 1].CapturedUtc);
        }
    }

    [Fact]
    public void Unchanged_regions_dedup_across_versions()
    {
        VersionStore store = NewStore();
        string file = Path.Combine(_work, "big.bin");

        byte[] data = new byte[8 * 1024 * 1024];
        new Random(11).NextBytes(data);
        File.WriteAllBytes(file, data);
        store.Capture(file, "big.bin");
        int blobsAfterFirst = store.Blobs.EnumerateAll().Count();

        // Change only the first few KB; the rest of the multi-MB file is identical.
        for (int i = 0; i < 2048; i++) data[i] ^= 0xFF;
        File.WriteAllBytes(file, data);
        store.Capture(file, "big.bin");
        int blobsAfterSecond = store.Blobs.EnumerateAll().Count();

        int added = blobsAfterSecond - blobsAfterFirst;
        Assert.InRange(added, 1, 3); // only the changed chunk(s), not the whole file again
    }

    [Fact]
    public void Version_log_survives_a_truncated_final_line()
    {
        string logPath = Path.Combine(_root, "store", "versions.jsonl");
        VersionStore store = NewStore();
        string file = Path.Combine(_work, "d.txt");
        File.WriteAllText(file, "one");
        store.Capture(file, "d.txt");
        File.WriteAllText(file, "two");
        store.Capture(file, "d.txt");

        // Simulate a crash mid-append: append a half-written JSON line.
        File.AppendAllText(logPath, "{\"RelativePath\":\"d.txt\",\"Cap");

        var reloaded = new VersionLog(logPath);
        Assert.Equal(2, reloaded.All.Count); // two intact versions; garbage tail skipped
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
