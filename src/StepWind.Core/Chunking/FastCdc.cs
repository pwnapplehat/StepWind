namespace StepWind.Core.Chunking;

/// <summary>One content-defined chunk: its offset in the source and its length.</summary>
public readonly record struct Chunk(long Offset, int Length);

/// <summary>
/// Content-defined chunking (FastCDC — gear-hash rolling window with normalized chunking).
///
/// Why this and not whole-file copies: StepWind versions files on every save. A naive store
/// re-copies the entire file each time, so a 2&nbsp;GB mailbox or video project that changes a
/// little would burn gigabytes per save. CDC splits a file at boundaries chosen by the
/// *content* (a rolling hash), so inserting or removing bytes only shifts the chunks around
/// the edit — every unchanged chunk keeps its identity and is stored once. This is the same
/// principle restic/borg use; it's the single most important storage decision, which is why
/// it's here from the first commit rather than bolted on later.
///
/// Deterministic and dependency-free: the gear table is generated from a fixed seed so every
/// build chunks identically (a version stored today must still resolve years from now).
/// </summary>
public sealed class FastCdc
{
    // Boundary targets. Average ~1 MiB matches restic; min/max cap pathological runs.
    public const int MinSize = 256 * 1024;
    public const int AvgSize = 1024 * 1024;
    public const int MaxSize = 4 * 1024 * 1024;

    private readonly int _minSize;
    private readonly int _avgSize;
    private readonly int _maxSize;
    private readonly ulong _maskShort; // stricter mask before the average (splits later)
    private readonly ulong _maskLong;  // looser mask after the average (splits sooner)

    private static readonly ulong[] Gear = BuildGearTable();

    public FastCdc(int minSize = MinSize, int avgSize = AvgSize, int maxSize = MaxSize)
    {
        if (minSize <= 0 || avgSize < minSize || maxSize < avgSize)
        {
            throw new ArgumentException("Require 0 < min <= avg <= max.");
        }

        _minSize = minSize;
        _avgSize = avgSize;
        _maxSize = maxSize;

        // Normalized chunking: use a tighter mask below the target size and a looser one
        // above it, which concentrates real chunk sizes near the average (fewer tiny/huge
        // chunks than classic CDC). Bits chosen around log2(avg).
        int bits = (int)Math.Round(Math.Log2(avgSize));
        _maskShort = (1UL << (bits + 2)) - 1;
        _maskLong = (1UL << (bits - 2)) - 1;
    }

    /// <summary>Finds the next chunk boundary within <paramref name="data"/> starting at 0.</summary>
    public int NextCut(ReadOnlySpan<byte> data)
    {
        int n = data.Length;
        if (n <= _minSize)
        {
            return n; // whole remainder is one (final) chunk
        }

        if (n > _maxSize)
        {
            n = _maxSize;
        }

        int normal = Math.Min(_avgSize, n);
        ulong hash = 0;
        int i = _minSize; // never cut before the minimum

        // Region 1: [min, avg) with the stricter mask (resists cutting too early).
        for (; i < normal; i++)
        {
            hash = (hash << 1) + Gear[data[i]];
            if ((hash & _maskShort) == 0)
            {
                return i + 1;
            }
        }

        // Region 2: [avg, max) with the looser mask (forces a cut before max).
        for (; i < n; i++)
        {
            hash = (hash << 1) + Gear[data[i]];
            if ((hash & _maskLong) == 0)
            {
                return i + 1;
            }
        }

        return n; // hit max (or end) with no natural boundary
    }

    /// <summary>Splits an in-memory buffer into content-defined chunks.</summary>
    public IReadOnlyList<Chunk> Split(ReadOnlySpan<byte> data)
    {
        var chunks = new List<Chunk>();
        long offset = 0;
        while (data.Length > 0)
        {
            int cut = NextCut(data);
            chunks.Add(new Chunk(offset, cut));
            offset += cut;
            data = data[cut..];
        }

        return chunks;
    }

    /// <summary>
    /// Streams chunks from a stream without buffering the whole file — the path used for
    /// large files (multi-GB videos, VM disks). Yields (buffer, length) for each chunk;
    /// the buffer is reused between yields, so callers must consume (hash/store) before
    /// requesting the next chunk.
    /// </summary>
    public IEnumerable<ReadOnlyMemory<byte>> SplitStream(Stream source)
    {
        byte[] window = new byte[_maxSize * 2];
        int filled = 0;
        int start = 0;

        while (true)
        {
            // Refill so at least maxSize bytes are available from 'start' when possible.
            if (filled - start < _maxSize)
            {
                if (start > 0)
                {
                    Array.Copy(window, start, window, 0, filled - start);
                    filled -= start;
                    start = 0;
                }

                while (filled < window.Length)
                {
                    int read = source.Read(window, filled, window.Length - filled);
                    if (read == 0)
                    {
                        break;
                    }

                    filled += read;
                }
            }

            int available = filled - start;
            if (available <= 0)
            {
                yield break;
            }

            int cut = NextCut(window.AsSpan(start, available));
            yield return new ReadOnlyMemory<byte>(window, start, cut);
            start += cut;
        }
    }

    /// <summary>
    /// Deterministic 256-entry gear table from a fixed seed (SplitMix64). Must never change
    /// across versions or previously-stored files would re-chunk and lose deduplication.
    /// </summary>
    private static ulong[] BuildGearTable()
    {
        var table = new ulong[256];
        ulong state = 0x9E3779B97F4A7C15UL; // fixed seed
        for (int i = 0; i < 256; i++)
        {
            state += 0x9E3779B97F4A7C15UL;
            ulong z = state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            table[i] = z ^ (z >> 31);
        }

        return table;
    }
}
