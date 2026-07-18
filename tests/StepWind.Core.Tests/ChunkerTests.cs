using StepWind.Core.Chunking;
using Xunit;

namespace StepWind.Core.Tests;

/// <summary>
/// The chunker is the storage foundation, so its two load-bearing properties get pinned:
/// determinism (same content → same boundaries, always, or old versions stop resolving) and
/// shift-resistance (an edit near the start must NOT re-chunk the rest of the file, which is
/// the entire reason for content-defined chunking over fixed blocks).
/// </summary>
public class ChunkerTests
{
    private static byte[] Pattern(int length, int seed)
    {
        // Pseudo-random but reproducible; must have enough entropy for real boundaries.
        byte[] data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    [Fact]
    public void Splitting_is_deterministic()
    {
        byte[] data = Pattern(20 * 1024 * 1024, 1);
        var cdc = new FastCdc();

        IReadOnlyList<Chunk> a = cdc.Split(data);
        IReadOnlyList<Chunk> b = cdc.Split(data);

        Assert.Equal(a.Count, b.Count);
        Assert.True(a.Count > 5, "a 20 MiB random file should split into several chunks");
        for (int i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i], b[i]);
        }
    }

    [Fact]
    public void Chunks_cover_the_whole_input_without_gaps_or_overlap()
    {
        byte[] data = Pattern(9 * 1024 * 1024, 7);
        IReadOnlyList<Chunk> chunks = new FastCdc().Split(data);

        long expected = 0;
        foreach (Chunk c in chunks)
        {
            Assert.Equal(expected, c.Offset);
            expected += c.Length;
        }

        Assert.Equal(data.Length, expected);
    }

    [Fact]
    public void Insertion_near_start_leaves_later_chunks_unchanged()
    {
        byte[] original = Pattern(16 * 1024 * 1024, 42);
        // Insert 10 bytes near the front — a fixed-block chunker would shift (and thus
        // rewrite) every block after the edit; CDC must re-sync and share the tail.
        byte[] edited = new byte[original.Length + 10];
        Array.Copy(original, 0, edited, 0, 1000);
        for (int i = 0; i < 10; i++) edited[1000 + i] = (byte)i;
        Array.Copy(original, 1000, edited, 1010, original.Length - 1000);

        var cdc = new FastCdc();
        HashSet<string> originalChunks = [.. cdc.Split(original).Select(c => Hex(original, c))];
        List<string> editedChunks = [.. cdc.Split(edited).Select(c => Hex(edited, c))];

        int shared = editedChunks.Count(h => originalChunks.Contains(h));
        Assert.True(shared >= editedChunks.Count - 3,
            $"expected almost all chunks shared after a small insert, shared {shared}/{editedChunks.Count}");
    }

    [Fact]
    public void Stream_and_buffer_split_agree()
    {
        byte[] data = Pattern(12 * 1024 * 1024, 99);
        var cdc = new FastCdc();

        IReadOnlyList<Chunk> buffered = cdc.Split(data);
        var streamed = new List<int>();
        using (var ms = new MemoryStream(data))
        {
            foreach (ReadOnlyMemory<byte> c in cdc.SplitStream(ms))
            {
                streamed.Add(c.Length);
            }
        }

        Assert.Equal(buffered.Select(c => c.Length), streamed);
    }

    [Fact]
    public void Respects_min_and_max_bounds()
    {
        byte[] zeros = new byte[10 * 1024 * 1024]; // worst case: no natural boundaries
        IReadOnlyList<Chunk> chunks = new FastCdc().Split(zeros);

        foreach (Chunk c in chunks.SkipLast(1)) // all but the last (final chunk may be short)
        {
            Assert.InRange(c.Length, FastCdc.MinSize, FastCdc.MaxSize);
        }
    }

    private static string Hex(byte[] source, Chunk c)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(source.AsSpan((int)c.Offset, c.Length)));
}
