namespace StepWind.Core.Storage;

/// <summary>
/// Content-addressed chunk store on disk. Blobs live under a sharded tree
/// (<c>blobs/ab/cdef…</c>) named by the SHA-256 of their plaintext, encoded through an
/// <see cref="IBlobCodec"/> (compress, optionally encrypt).
///
/// Two guarantees a data-protection tool must never break:
///   • Atomic writes — a blob is written to a temp file, flushed to disk, then atomically
///     renamed into place, so a crash or power loss can never leave a half-written blob that
///     later reads back as corruption. Because the name is the content hash, a duplicate
///     write is a no-op (dedup) and a crashed temp file is just orphaned garbage the GC removes.
///   • Read-time verification — <see cref="Get"/> re-hashes the decoded plaintext and refuses
///     to return bytes that don't match the requested id, turning silent corruption into a
///     loud, catchable error rather than a bad restore.
/// </summary>
public sealed class BlobStore
{
    private readonly string _blobRoot;
    private readonly string _tempRoot;
    private readonly IBlobCodec _codec;

    public BlobStore(string root, IBlobCodec codec)
    {
        Root = root;
        _blobRoot = Path.Combine(root, "blobs");
        _tempRoot = Path.Combine(root, "tmp");
        _codec = codec;
        Directory.CreateDirectory(_blobRoot);
        Directory.CreateDirectory(_tempRoot);
    }

    /// <summary>The store's root directory (used by the engine to exclude it from watching).</summary>
    public string Root { get; }

    public bool Exists(BlobId id) => File.Exists(Path.Combine(_blobRoot, id.RelativePath));

    /// <summary>
    /// Stores a chunk and returns its id. Idempotent: if the content already exists (same
    /// hash) nothing is written. Returns whether a new blob was actually created.
    /// </summary>
    public (BlobId Id, bool Written) Put(ReadOnlySpan<byte> plaintext)
    {
        BlobId id = BlobId.OfContent(plaintext);
        string finalPath = Path.Combine(_blobRoot, id.RelativePath);
        if (File.Exists(finalPath))
        {
            return (id, false); // dedup hit
        }

        byte[] encoded = _codec.Encode(plaintext);
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

        string tempPath = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N") + ".tmp");
        using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            fs.Write(encoded);
            fs.Flush(flushToDisk: true); // durable before the rename makes it visible
        }

        try
        {
            // Atomic publish. If another thread/process won the race, our temp is redundant.
            File.Move(tempPath, finalPath, overwrite: false);
            return (id, true);
        }
        catch (IOException) when (File.Exists(finalPath))
        {
            File.Delete(tempPath);
            return (id, false);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    /// <summary>Reads and verifies a chunk. Throws if missing or if the content is corrupt.</summary>
    public byte[] Get(BlobId id)
    {
        string path = Path.Combine(_blobRoot, id.RelativePath);
        byte[] stored = File.ReadAllBytes(path);
        byte[] plaintext = _codec.Decode(stored);

        BlobId actual = BlobId.OfContent(plaintext);
        if (actual != id)
        {
            throw new InvalidDataException(
                $"Blob {id} failed integrity check (decoded content hashes to {actual}). The store may be corrupt.");
        }

        return plaintext;
    }

    /// <summary>Every blob id currently on disk (used by the GC's mark-and-sweep).</summary>
    public IEnumerable<BlobId> EnumerateAll()
    {
        if (!Directory.Exists(_blobRoot))
        {
            yield break;
        }

        foreach (string shard in Directory.EnumerateDirectories(_blobRoot))
        {
            string prefix = Path.GetFileName(shard);
            if (prefix.Length != 2)
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(shard))
            {
                string rest = Path.GetFileName(file);
                if (rest.Length == 62 && (prefix + rest).All(Uri.IsHexDigit))
                {
                    yield return BlobId.Parse(prefix + rest);
                }
            }
        }
    }

    /// <summary>Deletes a blob (GC sweep of an unreferenced chunk). Safe if already gone.</summary>
    public bool Delete(BlobId id) => TryDelete(Path.Combine(_blobRoot, id.RelativePath));

    /// <summary>Removes leftover temp files from an interrupted write (called at startup).</summary>
    public int CleanTemp()
    {
        int removed = 0;
        foreach (string file in Directory.EnumerateFiles(_tempRoot))
        {
            if (TryDelete(file))
            {
                removed++;
            }
        }

        return removed;
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                return true;
            }
        }
        catch
        {
            // best effort
        }

        return false;
    }
}
