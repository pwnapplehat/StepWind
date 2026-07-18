using StepWind.Core.Chunking;

namespace StepWind.Core.Storage;

/// <summary>
/// The folder time-machine: captures versions of files (content-defined chunked, deduplicated,
/// codec-encoded blobs + an append-only version log) and restores any past version byte-exact.
/// Restores never overwrite in place — callers get the bytes/stream and decide where they land
/// (the engine writes them to a safe, collision-checked path), so a restore can never repeat
/// the original data-loss by clobbering current work.
/// </summary>
public sealed class VersionStore
{
    private readonly BlobStore _blobs;
    private readonly VersionLog _log;
    private readonly FastCdc _chunker = new();

    public VersionStore(BlobStore blobs, VersionLog log)
    {
        _blobs = blobs;
        _log = log;
    }

    public VersionLog Log => _log;

    public BlobStore Blobs => _blobs;

    /// <summary>
    /// Captures the current content of <paramref name="sourcePath"/> as a new version under
    /// <paramref name="relativePath"/>. Streams the file through the chunker, so a multi-GB
    /// file is never buffered whole. Deduplicates: unchanged chunks are already present and
    /// aren't rewritten. Returns the recorded version.
    /// </summary>
    public FileVersion Capture(string sourcePath, string relativePath, string reason = "change")
    {
        var chunkIds = new List<string>();
        long size = 0;

        using (FileStream input = OpenShared(sourcePath))
        {
            foreach (ReadOnlyMemory<byte> chunk in _chunker.SplitStream(input))
            {
                (BlobId id, _) = _blobs.Put(chunk.Span);
                chunkIds.Add(id.Hex);
                size += chunk.Length;
            }
        }

        DateTime modified;
        try
        {
            modified = File.GetLastWriteTimeUtc(sourcePath);
        }
        catch
        {
            modified = DateTime.UtcNow;
        }

        return _log.Append(new FileVersion
        {
            RelativePath = NormalizePath(relativePath),
            CapturedUtc = DateTime.UtcNow,
            ModifiedUtc = modified,
            Size = size,
            Chunks = chunkIds,
            Reason = reason,
        });
    }

    /// <summary>Reconstructs a version's content into <paramref name="destination"/> (streamed).</summary>
    public long WriteContent(FileVersion version, Stream destination)
    {
        long written = 0;
        foreach (string hex in version.Chunks)
        {
            byte[] chunk = _blobs.Get(BlobId.Parse(hex)); // verifies integrity per chunk
            destination.Write(chunk);
            written += chunk.Length;
        }

        return written;
    }

    /// <summary>
    /// Restores a version to <paramref name="destinationPath"/> WITHOUT overwriting: if the
    /// path exists, a non-colliding name is chosen ("file (restored 2026-07-18 173000).ext").
    /// Returns the actual path written. Original modified time is reapplied best-effort.
    /// </summary>
    public string RestoreToSafePath(FileVersion version, string destinationPath)
    {
        string finalPath = MakeNonColliding(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

        string temp = finalPath + ".swtmp";
        using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            WriteContent(version, fs);
            fs.Flush(flushToDisk: true);
        }

        File.Move(temp, finalPath, overwrite: false);

        try
        {
            if (version.ModifiedUtc > DateTime.MinValue)
            {
                File.SetLastWriteTimeUtc(finalPath, version.ModifiedUtc);
            }
        }
        catch
        {
            // timestamp restoration is a nicety, never fatal
        }

        return finalPath;
    }

    private static string MakeNonColliding(string desired)
    {
        if (!File.Exists(desired))
        {
            return desired;
        }

        string dir = Path.GetDirectoryName(desired)!;
        string name = Path.GetFileNameWithoutExtension(desired);
        string ext = Path.GetExtension(desired);
        string stamp = DateTime.Now.ToString("yyyy-MM-dd HHmmss");
        string candidate = Path.Combine(dir, $"{name} (restored {stamp}){ext}");
        int n = 1;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(dir, $"{name} (restored {stamp} #{n++}){ext}");
        }

        return candidate;
    }

    private static FileStream OpenShared(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    private static string NormalizePath(string relativePath)
        => relativePath.Replace('\\', '/').TrimStart('/');
}
