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
    private long _totalBytes;

    public BlobStore(string root, IBlobCodec codec)
    {
        Root = root;
        _blobRoot = Path.Combine(root, "blobs");
        _tempRoot = Path.Combine(root, "tmp");
        _codec = codec;
        Directory.CreateDirectory(_blobRoot);
        Directory.CreateDirectory(_tempRoot);

        // One startup scan; afterwards Put/Delete/re-encode keep the number current, so the
        // UI can show live storage usage without re-walking the tree on every status poll.
        long bytes = 0;
        foreach (string file in Directory.EnumerateFiles(_blobRoot, "*", SearchOption.AllDirectories))
        {
            try { bytes += new FileInfo(file).Length; } catch { }
        }

        _totalBytes = bytes;
    }

    /// <summary>The store's root directory (used by the engine to exclude it from watching).</summary>
    public string Root { get; }

    /// <summary>Total on-disk size of all blobs, kept current as blobs are written/swept.</summary>
    public long TotalBytes => Interlocked.Read(ref _totalBytes);

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
            Interlocked.Add(ref _totalBytes, encoded.Length);
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

    /// <summary>
    /// Reads and verifies a chunk. Throws if missing or if the content is corrupt. When the
    /// codec supports a second format (<see cref="MigratingBlobCodec"/>), a blob that fails
    /// the primary decode-or-hash is retried in the other format — this is what keeps every
    /// version readable while an encryption toggle re-encodes the store in the background.
    /// </summary>
    public byte[] Get(BlobId id)
    {
        string path = Path.Combine(_blobRoot, id.RelativePath);
        byte[] stored = File.ReadAllBytes(path);

        byte[]? plaintext = TryDecodeVerified(stored, id, primary: true)
                         ?? TryDecodeVerified(stored, id, primary: false);
        return plaintext ?? throw new InvalidDataException(
            $"Blob {id} failed integrity check in every known format. The store may be corrupt.");
    }

    private byte[]? TryDecodeVerified(byte[] stored, BlobId id, bool primary)
    {
        byte[]? plaintext;
        try
        {
            plaintext = primary
                ? _codec.Decode(stored)
                : (_codec as MigratingBlobCodec)?.DecodeAlternate(stored);
        }
        catch
        {
            return null;
        }

        // The SHA-256 content id is the final arbiter: a decode that "succeeded" in the
        // wrong format (raw deflate has no checksum) can never pass this.
        return plaintext is not null && BlobId.OfContent(plaintext) == id ? plaintext : null;
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
    public bool Delete(BlobId id)
    {
        string path = Path.Combine(_blobRoot, id.RelativePath);
        long size = 0;
        try { size = new FileInfo(path).Length; } catch { }
        bool deleted = TryDelete(path);
        if (deleted && size > 0)
        {
            Interlocked.Add(ref _totalBytes, -size);
        }

        return deleted;
    }

    /// <summary>
    /// Re-encodes every blob into the codec's current target format (encryption toggled on:
    /// plain→cipher; toggled off: cipher→plain). Per-blob atomic (temp + rename), verified by
    /// content hash before the swap, and safe to interrupt at ANY point — a mixed store stays
    /// fully readable because <see cref="Get"/> accepts both formats. Returns how many blobs
    /// were re-encoded.
    /// </summary>
    public int ReEncodeAll(CancellationToken ct = default, Action<int, int>? progress = null)
    {
        List<BlobId> ids = [.. EnumerateAll()];
        int converted = 0;
        for (int i = 0; i < ids.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Invoke(i, ids.Count);
            string path = Path.Combine(_blobRoot, ids[i].RelativePath);
            try
            {
                byte[] stored = File.ReadAllBytes(path);

                // Already in the target format? (Hash-verified, so a wrong-format false
                // positive is impossible.)
                if (TryDecodeVerified(stored, ids[i], primary: true) is not null)
                {
                    continue;
                }

                byte[]? plaintext = TryDecodeVerified(stored, ids[i], primary: false);
                if (plaintext is null)
                {
                    continue; // unreadable either way — never make it worse by rewriting
                }

                byte[] reEncoded = _codec.Encode(plaintext);
                string tempPath = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N") + ".tmp");
                using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    fs.Write(reEncoded);
                    fs.Flush(flushToDisk: true);
                }

                File.Move(tempPath, path, overwrite: true);
                Interlocked.Add(ref _totalBytes, reEncoded.Length - stored.Length);
                converted++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // A blob GC'd or locked mid-pass — skip; the next pass converges it.
            }
        }

        return converted;
    }

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
