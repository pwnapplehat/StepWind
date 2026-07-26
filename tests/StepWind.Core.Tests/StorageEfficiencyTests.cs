using System.IO.Compression;
using System.Text;
using StepWind.Core.Chunking;
using StepWind.Core.Storage;
using Xunit;

namespace StepWind.Core.Tests;

/// <summary>
/// Pins the storage-efficiency behaviour, and — more importantly — pins the compatibility that
/// makes it safe to have changed it. Before 1.0.2 the chunker averaged 1 MiB with a 256 KiB
/// minimum, so every file smaller than 256 KiB was a single chunk and each save re-stored the
/// whole thing; the compression level was Deflate's weakest. Both changed. Neither change may
/// break history that already exists on a user's disk.
/// </summary>
public class StorageEfficiencyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "stepwind-eff", Guid.NewGuid().ToString("N"));
    private readonly string _work;
    private readonly string _storeDir;

    // The chunker parameters shipped before 1.0.2, i.e. what is already on disk for early users.
    private static FastCdc PreviousParameters => new(256 * 1024, 1024 * 1024, 4 * 1024 * 1024);

    public StorageEfficiencyTests()
    {
        _work = Path.Combine(_root, "work");
        _storeDir = Path.Combine(_root, "store");
        Directory.CreateDirectory(_work);
    }

    private BlobStore NewBlobs() => new(_storeDir, new GzipBlobCodec());

    private VersionLog NewLog() => new(Path.Combine(_storeDir, "versions.jsonl"));

    /// <summary>Document-like text: compresses and chunks like real user content, not like noise.</summary>
    private static byte[] Document(int sizeBytes, int seed)
    {
        var rng = new Random(seed);
        string[] words = "the quick brown fox jumps over a lazy dog while StepWind records every change and keeps a version for later recovery".Split(' ');
        var sb = new StringBuilder(sizeBytes + 128);
        int line = 0;
        while (sb.Length < sizeBytes)
        {
            sb.Append(line++).Append(": ");
            int n = 6 + rng.Next(12);
            for (int i = 0; i < n; i++) { sb.Append(words[rng.Next(words.Length)]).Append(' '); }
            sb.Append('\n');
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static byte[] EditMiddleLine(byte[] content)
    {
        string[] lines = Encoding.UTF8.GetString(content).Split('\n');
        lines[lines.Length / 2] = "this line was rewritten by the user before saving again";
        return Encoding.UTF8.GetBytes(string.Join('\n', lines));
    }

    [Fact]
    public void Versions_written_with_the_previous_chunk_parameters_still_restore_byte_exact()
    {
        // THE compatibility test. A version records its own chunk list, so changing the chunker
        // must not affect anything already captured — including in a store that now holds both.
        string file = Path.Combine(_work, "legacy.txt");
        byte[] original = Document(3 * 1024 * 1024, 11);
        File.WriteAllBytes(file, original);

        FileVersion legacy;
        {
            var store = new VersionStore(NewBlobs(), NewLog(), PreviousParameters);
            legacy = store.Capture(file, "legacy.txt");
        }

        // Reopen the same store with today's parameters and read the old version back.
        var current = new VersionStore(NewBlobs(), NewLog());
        using (var ms = new MemoryStream())
        {
            current.WriteContent(legacy, ms);
            Assert.Equal(original, ms.ToArray());
        }

        // Capture again through the new parameters: the new version must be correct, and the old
        // one must STILL restore afterwards (a store with two chunkings in it stays coherent).
        byte[] edited = EditMiddleLine(original);
        File.WriteAllBytes(file, edited);
        FileVersion fresh = current.Capture(file, "legacy.txt");

        using (var ms = new MemoryStream())
        {
            current.WriteContent(fresh, ms);
            Assert.Equal(edited, ms.ToArray());
        }

        using (var ms = new MemoryStream())
        {
            current.WriteContent(legacy, ms);
            Assert.Equal(original, ms.ToArray());
        }
    }

    [Fact]
    public void Resaving_an_edited_document_stores_only_the_part_that_changed()
    {
        // The whole point of the change. Under the previous parameters a 512 KiB file was one
        // chunk, so this second save added the entire file again; the assertion below would fail
        // by roughly an order of magnitude.
        string file = Path.Combine(_work, "report.txt");
        byte[] content = Document(512 * 1024, 22);
        File.WriteAllBytes(file, content);

        BlobStore blobs = NewBlobs();
        var store = new VersionStore(blobs, NewLog());
        store.Capture(file, "report.txt");
        long wholeFileCost = blobs.TotalBytes;   // what storing this file once actually cost

        File.WriteAllBytes(file, EditMiddleLine(content));
        store.Capture(file, "report.txt");
        long added = blobs.TotalBytes - wholeFileCost;

        // Measured against the STORED cost of the file, not its raw size. Comparing against the
        // raw size is the trap: 512 KiB of prose compresses to ~108 KiB, so "added < raw/4" was
        // satisfied even when the second save re-stored the entire file, and the test proved
        // nothing. Against the stored cost, re-storing everything scores ~1.0 and fails loudly.
        Assert.True(added > 0, "the edited save must store something");
        Assert.True(added < wholeFileCost / 3,
            $"a one-line edit added {added:n0} bytes where storing the whole file cost " +
            $"{wholeFileCost:n0} — the edit is being stored as a whole new copy");
    }

    [Fact]
    public void A_quarter_megabyte_file_is_split_into_several_chunks()
    {
        // Guards the parameters themselves: with a 256 KiB minimum this was a single chunk, and no
        // amount of compression can make up for re-storing a whole file on every save.
        IReadOnlyList<Chunk> chunks = new FastCdc().Split(Document(256 * 1024, 33));
        Assert.True(chunks.Count >= 3,
            $"a 256 KiB document split into {chunks.Count} chunk(s); small edits cannot deduplicate");
    }

    [Fact]
    public void Blobs_written_at_the_previous_compression_level_still_read_back()
    {
        // Deflate is self-describing, so the level is an encoder-side choice only. This proves it
        // for the real path — written by the old encoder, read (and hash-verified) by today's.
        BlobStore legacyWriter = new(_storeDir, new PreviousLevelCodec());
        byte[] payload = Document(300 * 1024, 44);
        (BlobId id, bool written) = legacyWriter.Put(payload);
        Assert.True(written);

        BlobStore currentReader = NewBlobs();
        Assert.Equal(payload, currentReader.Get(id));
    }

    [Fact]
    public void An_identical_resave_stores_nothing_new()
    {
        string file = Path.Combine(_work, "same.txt");
        File.WriteAllBytes(file, Document(200 * 1024, 55));

        BlobStore blobs = NewBlobs();
        var store = new VersionStore(blobs, NewLog());
        store.Capture(file, "same.txt");
        long after = blobs.TotalBytes;

        File.SetLastWriteTimeUtc(file, DateTime.UtcNow); // a touch, not a change
        store.Capture(file, "same.txt");

        Assert.Equal(after, blobs.TotalBytes);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    /// <summary>The codec exactly as it shipped before 1.0.2: Deflate at its weakest level.</summary>
    private sealed class PreviousLevelCodec : IBlobCodec
    {
        public string Id => "deflate";

        public byte[] Encode(ReadOnlySpan<byte> plaintext)
        {
            using var output = new MemoryStream();
            using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
            {
                deflate.Write(plaintext);
            }

            return output.ToArray();
        }

        public byte[] Decode(ReadOnlySpan<byte> stored)
        {
            using var input = new MemoryStream(stored.ToArray());
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            deflate.CopyTo(output);
            return output.ToArray();
        }
    }
}
