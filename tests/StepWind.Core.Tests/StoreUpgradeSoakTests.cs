using System.Security.Cryptography;
using System.Text;
using StepWind.Core.Chunking;
using StepWind.Core.Storage;
using Xunit;

namespace StepWind.Core.Tests;

/// <summary>
/// What actually happens to a real person's store when they update.
///
/// 1.0.2 changed the chunk sizes, so from the update onwards a file's chunks no longer match the
/// ones already on disk, and every store in the field becomes a MIXED store — old versions
/// chunked one way, new versions another, sharing one blob pool. Unit tests cover "an old version
/// still restores". These cover the parts that could still go wrong afterwards: garbage
/// collection deciding which blobs are unreferenced, the integrity checker, and the encryption
/// re-encode, all running across a mixture they were never exercised against before.
///
/// Sizes are chosen to sit exactly on the new boundaries (16 KiB minimum, 64 KiB average,
/// 256 KiB maximum), plus the degenerate cases — empty, one byte, incompressible, and long runs
/// of identical bytes that a content-defined chunker can never find a natural boundary in.
/// </summary>
public class StoreUpgradeSoakTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "stepwind-soak", Guid.NewGuid().ToString("N"));
    private readonly string _work;
    private readonly string _storeDir;
    private readonly byte[] _key = SHA256.HashData("soak-key-material"u8.ToArray());

    /// <summary>The chunker as it shipped in 1.0.0/1.0.1 — what existing history was written with.</summary>
    private static FastCdc PreUpgradeChunker => new(256 * 1024, 1024 * 1024, 4 * 1024 * 1024);

    public StoreUpgradeSoakTests()
    {
        _work = Path.Combine(_root, "work");
        _storeDir = Path.Combine(_root, "store");
        Directory.CreateDirectory(_work);
    }

    private VersionLog NewLog() => new(Path.Combine(_storeDir, "versions.jsonl"));

    private BlobStore NewBlobs(IBlobCodec? codec = null) => new(_storeDir, codec ?? new GzipBlobCodec());

    private MigratingBlobCodec EncryptingCodec(bool encrypt)
        => new(new GzipBlobCodec(), () => new AesGcmBlobCodec(_key), encrypt);

    /// <summary>The corpus: name → content, spanning every chunker edge case that matters.</summary>
    private static Dictionary<string, byte[]> Corpus()
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["empty.txt"] = [],
            ["one-byte.txt"] = "x"u8.ToArray(),
            ["tiny.txt"] = Text(900, 1),
            ["at-min-minus-one.txt"] = Text((16 * 1024) - 1, 2),
            ["at-min.txt"] = Text(16 * 1024, 3),
            ["at-min-plus-one.txt"] = Text((16 * 1024) + 1, 4),
            ["at-avg.txt"] = Text(64 * 1024, 5),
            ["at-max-minus-one.bin"] = Text((256 * 1024) - 1, 6),
            ["at-max.bin"] = Text(256 * 1024, 7),
            ["at-max-plus-one.bin"] = Text((256 * 1024) + 1, 8),
            ["spans-old-and-new.bin"] = Text(3 * 1024 * 1024, 9),
            // No natural boundary exists in a constant run, so the chunker must fall back to the
            // maximum size — and every resulting chunk is identical, which the store must dedup.
            ["all-zeroes.bin"] = new byte[2 * 1024 * 1024],
            // Incompressible: the codec must not corrupt (or inflate unboundedly) data that
            // cannot be compressed.
            ["random.bin"] = Random(1_500_000, 10),
        };
        return files;
    }

    private static byte[] Text(int size, int seed)
    {
        var rng = new Random(seed);
        string[] words = "the quick brown fox jumps over a lazy dog while StepWind records every change".Split(' ');
        var sb = new StringBuilder(size + 64);
        while (sb.Length < size) { sb.Append(words[rng.Next(words.Length)]).Append(' '); }
        return Encoding.UTF8.GetBytes(sb.ToString())[..size];
    }

    private static byte[] Random(int size, int seed)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static byte[] Edit(byte[] content, int pass)
    {
        if (content.Length == 0) { return Encoding.UTF8.GetBytes($"was empty, now written {pass}"); }
        var copy = (byte[])content.Clone();
        copy[content.Length / 2] = (byte)('A' + (pass % 26));   // one byte, in the middle
        return copy;
    }

    private string Write(string name, byte[] content)
    {
        string path = Path.Combine(_work, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static void AssertRestores(VersionStore store, FileVersion version, byte[] expected, string context)
    {
        using var ms = new MemoryStream();
        store.WriteContent(version, ms);
        byte[] actual = ms.ToArray();
        Assert.True(expected.AsSpan().SequenceEqual(actual),
            $"{context}: '{version.RelativePath}' restored {actual.Length:n0} bytes, expected {expected.Length:n0}");
    }

    [Fact]
    public void An_existing_store_survives_the_upgrade_then_gc_verify_and_encryption()
    {
        Dictionary<string, byte[]> corpus = Corpus();
        var expected = new List<(FileVersion Version, byte[] Content)>();

        // ── 1. History as it exists today on a user's disk: written by the old chunker ─────────
        {
            var old = new VersionStore(NewBlobs(), NewLog(), PreUpgradeChunker);
            foreach ((string name, byte[] content) in corpus)
            {
                expected.Add((old.Capture(Write(name, content), name), content));
            }
        }

        // ── 2. They update, and keep working: every file edited and saved twice more ──────────
        var current = new VersionStore(NewBlobs(), NewLog());
        for (int pass = 1; pass <= 2; pass++)
        {
            foreach ((string name, byte[] content) in corpus.ToList())
            {
                byte[] edited = Edit(content, pass);
                corpus[name] = edited;
                expected.Add((current.Capture(Write(name, edited), name), edited));
            }
        }

        // Every version, from both eras, must still be byte-exact.
        foreach ((FileVersion v, byte[] content) in expected)
        {
            AssertRestores(current, v, content, "after upgrade");
        }

        // ── 3. Integrity check must be clean on the mixed store ───────────────────────────────
        VerifyReport report = StoreMaintenance.Verify(current.Log, current.Blobs, deep: true);
        Assert.Equal(0, report.UnrestorableVersions);
        Assert.Equal(0, report.MissingChunks);

        // ── 4. Garbage collection across two chunkings ────────────────────────────────────────
        // The danger: sweeping a blob that only an OLD-chunked version references. Keep just the
        // newest version per file, then confirm every survivor still reconstructs.
        var policy = new RetentionPolicy { MaxVersionsPerFile = 1, KeepAllHours = 0, HourlyDays = 0, DailyDays = 0 };
        RetentionResult result = Retention.Apply(current.Log, current.Blobs, policy, DateTime.UtcNow.AddDays(1));
        Assert.True(result.VersionsKept < result.VersionsBefore, "retention should have pruned something");
        // Without this the test could pass while the sweep did nothing, which is precisely the
        // risky part: blobs belonging to old-chunked versions being deleted out from under a
        // store that still has to restore what remains.
        Assert.True(result.BlobsSwept > 0, "the sweep should have reclaimed the superseded chunks");

        foreach (FileVersion survivor in current.Log.All)
        {
            byte[] content = expected.Last(e => e.Version.RelativePath == survivor.RelativePath
                                             && e.Version.CapturedUtc == survivor.CapturedUtc).Content;
            AssertRestores(current, survivor, content, "after retention + sweep");
        }

        VerifyReport afterGc = StoreMaintenance.Verify(current.Log, current.Blobs, deep: true);
        Assert.Equal(0, afterGc.UnrestorableVersions);
        Assert.Equal(0, afterGc.MissingChunks);
        Assert.Equal(0, afterGc.OrphanBlobs);

        // ── 5. Turn encryption on afterwards, on the now-mixed store ──────────────────────────
        var encrypting = new VersionStore(NewBlobs(EncryptingCodec(encrypt: true)), NewLog());
        foreach (FileVersion survivor in encrypting.Log.All)
        {
            byte[] content = expected.Last(e => e.Version.RelativePath == survivor.RelativePath
                                             && e.Version.CapturedUtc == survivor.CapturedUtc).Content;
            AssertRestores(encrypting, survivor, content, "with encryption enabled");
        }

        // …and a fresh capture through the encrypting store still round-trips.
        byte[] newContent = Text(700 * 1024, 99);
        FileVersion encrypted = encrypting.Capture(Write("after-encryption.bin", newContent), "after-encryption.bin");
        AssertRestores(encrypting, encrypted, newContent, "captured while encrypted");
    }

    [Fact]
    public void Every_edge_case_file_round_trips_through_the_new_chunker()
    {
        var store = new VersionStore(NewBlobs(), NewLog());
        foreach ((string name, byte[] content) in Corpus())
        {
            FileVersion v = store.Capture(Write(name, content), name);
            Assert.Equal(content.Length, v.Size);
            AssertRestores(store, v, content, "fresh capture");
        }
    }

    [Fact]
    public void A_long_run_of_identical_bytes_collapses_to_one_stored_chunk()
    {
        // 2 MiB of zeroes has no content-defined boundary, so the chunker cuts at its maximum and
        // produces identical chunks — which must deduplicate to a single blob rather than eight.
        BlobStore blobs = NewBlobs();
        var store = new VersionStore(blobs, NewLog());
        byte[] zeroes = new byte[2 * 1024 * 1024];

        FileVersion v = store.Capture(Write("zeroes.bin", zeroes), "zeroes.bin");

        Assert.True(v.Chunks.Count > 1, "a 2 MiB file should be several chunks");
        Assert.Single(v.Chunks.Distinct(StringComparer.Ordinal));
        Assert.True(blobs.TotalBytes < 64 * 1024,
            $"identical chunks stored {blobs.TotalBytes:n0} bytes; deduplication failed");
        AssertRestores(store, v, zeroes, "all-zeroes");
    }

    [Fact]
    public void Incompressible_data_is_stored_without_growing_unreasonably()
    {
        // Deflate adds a few bytes per block when input cannot be compressed. That is fine, but it
        // must stay negligible — a version store that inflates random data is a bug.
        BlobStore blobs = NewBlobs();
        var store = new VersionStore(blobs, NewLog());
        byte[] noise = Random(2 * 1024 * 1024, 77);

        FileVersion v = store.Capture(Write("noise.bin", noise), "noise.bin");

        Assert.True(blobs.TotalBytes < noise.Length * 1.01,
            $"{noise.Length:n0} bytes of incompressible data occupied {blobs.TotalBytes:n0} on disk");
        AssertRestores(store, v, noise, "incompressible");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
