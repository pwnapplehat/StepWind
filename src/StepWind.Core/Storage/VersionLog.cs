using System.Text;
using System.Text.Json;

namespace StepWind.Core.Storage;

/// <summary>
/// Append-only log of every <see cref="FileVersion"/>, one JSON object per line
/// (<c>versions.jsonl</c>). Append-only is deliberate: a new version is a single durable
/// append (no rewrite of a big index that a crash could corrupt), and the full history of a
/// file is just every line that mentions its path, in order. The in-memory index is rebuilt
/// by reading the log at startup; a truncated final line (crash mid-append) is skipped
/// rather than fatal.
/// </summary>
public sealed class VersionLog
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly string _path;
    private readonly object _appendLock = new();
    private readonly List<FileVersion> _versions = [];

    // Optional metadata encryption. IMPORTANT asymmetry: the cipher is used for READING whenever
    // it's present (so encrypted lines are always decryptable and can never be orphaned), but only
    // WRITES when _encryptOnWrite is set — so turning index encryption off still reads existing
    // encrypted lines and writes plaintext going forward (retention's Rewrite converges the file).
    private readonly IIndexCipher? _cipher;
    private readonly bool _encryptOnWrite;

    public VersionLog(string path, IIndexCipher? cipher = null, bool encryptOnWrite = false)
    {
        _path = path;
        _cipher = cipher;
        _encryptOnWrite = encryptOnWrite && cipher is not null;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Load();
    }

    /// <summary>Serializes a version to the on-disk line form (encrypted iff writing encrypted).</summary>
    private string FormatLine(FileVersion version)
    {
        string json = JsonSerializer.Serialize(version, Json);
        return _encryptOnWrite ? _cipher!.Encrypt(json) : json;
    }

    /// <summary>Parses one on-disk line (plaintext JSON or an encrypted token). Null if unreadable.</summary>
    private FileVersion? ParseLine(string line)
    {
        string json;
        if (line.StartsWith('{'))
        {
            json = line; // legacy / plaintext line — readable regardless of cipher
        }
        else if (_cipher is not null)
        {
            try { json = _cipher.Decrypt(line); }
            catch { return null; } // wrong key / corrupt token
        }
        else
        {
            return null; // encrypted line but no key available to read it
        }

        try { return JsonSerializer.Deserialize<FileVersion>(json, Json); }
        catch (JsonException) { return null; } // truncated final line from a crash — safe to skip
    }

    /// <summary>
    /// A point-in-time snapshot of all versions. Copies under the lock so callers (the pipe
    /// thread serving GetHistory, the retention pass) never enumerate the list while a capture
    /// on the watch thread is mutating it — the classic "collection modified" crash.
    /// </summary>
    public IReadOnlyList<FileVersion> All
    {
        get { lock (_appendLock) { return [.. _versions]; } }
    }

    /// <summary>
    /// Every distinct FIRST path segment present in the store (the root namespaces), including
    /// ones whose folders are no longer protected. Used when assigning a namespace to a newly
    /// protected folder: a new root must never silently adopt a segment that dead history already
    /// occupies (that would merge two different folders' files into one timeline).
    /// </summary>
    public IReadOnlyCollection<string> DistinctRootSegments()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        lock (_appendLock)
        {
            foreach (FileVersion v in _versions)
            {
                int slash = v.RelativePath.IndexOf('/');
                set.Add(slash < 0 ? v.RelativePath : v.RelativePath[..slash]);
            }
        }

        return set;
    }

    /// <summary>All versions of one file, oldest first (snapshotted under the lock).</summary>
    public IReadOnlyList<FileVersion> History(string relativePath)
    {
        lock (_appendLock)
        {
            return [.. _versions.Where(v => v.RelativePath.Equals(relativePath, StringComparison.OrdinalIgnoreCase))
                                .OrderBy(v => v.CapturedUtc)];
        }
    }

    /// <summary>The most recently captured version of a file, or null if none (used for dedup).</summary>
    public FileVersion? LatestFor(string relativePath)
    {
        lock (_appendLock)
        {
            FileVersion? latest = null;
            foreach (FileVersion v in _versions)
            {
                if (v.RelativePath.Equals(relativePath, StringComparison.OrdinalIgnoreCase)
                    && (latest is null || v.CapturedUtc >= latest.CapturedUtc))
                {
                    latest = v;
                }
            }

            return latest;
        }
    }

    /// <summary>Durably appends a version and returns it.</summary>
    public FileVersion Append(FileVersion version)
    {
        string line = FormatLine(version);
        lock (_appendLock)
        {
            using (var fs = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read))
            using (var writer = new StreamWriter(fs, new UTF8Encoding(false)))
            {
                writer.WriteLine(line);
                writer.Flush();
                fs.Flush(flushToDisk: true);
            }

            _versions.Add(version);
        }

        return version;
    }

    /// <summary>
    /// Rewrites the log to only the given versions (used after retention pruning). Written
    /// to a temp file and atomically renamed so a crash can't lose the whole history.
    /// </summary>
    public void Rewrite(IReadOnlyList<FileVersion> keep)
    {
        lock (_appendLock)
        {
            string temp = _path + ".tmp";
            using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(fs, new UTF8Encoding(false)))
            {
                foreach (FileVersion v in keep)
                {
                    writer.WriteLine(FormatLine(v)); // re-encrypts (or de-encrypts) to the current mode
                }

                writer.Flush();
                fs.Flush(flushToDisk: true);
            }

            File.Move(temp, _path, overwrite: true);
            _versions.Clear();
            _versions.AddRange(keep);
        }
    }

    /// <summary>
    /// Snapshots the index to <c>versions.jsonl.bak</c> (atomic temp+rename). The index is the
    /// single file that maps every version to its chunks; a corrupt or lost index orphans the
    /// whole store, so a known-good copy is kept and refreshed on the retention pass and before
    /// any destructive rewrite/repair. Best-effort; never throws into the caller.
    /// </summary>
    public void Backup()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            string tmp = _path + ".bak.tmp";
            File.Copy(_path, tmp, overwrite: true);
            File.Move(tmp, _path + ".bak", overwrite: true);
        }
        catch
        {
            // best effort — a failed backup must never disrupt capture/retention
        }
    }

    /// <summary>
    /// Restores the index from its <c>.bak</c> snapshot (disaster recovery when the live index is
    /// truncated/corrupt). Returns the number of versions recovered, or -1 if there's no backup.
    /// </summary>
    public int RestoreFromBackup()
    {
        string bak = _path + ".bak";
        if (!File.Exists(bak))
        {
            return -1;
        }

        lock (_appendLock)
        {
            File.Copy(bak, _path, overwrite: true);
            _versions.Clear();
            Load();
            return _versions.Count;
        }
    }

    private void Load()
    {
        if (!File.Exists(_path))
        {
            return;
        }

        foreach (string line in File.ReadLines(_path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            // Handles plaintext, encrypted, and truncated lines (see ParseLine). A mixed file
            // (plaintext legacy lines + encrypted new lines) reads cleanly during migration.
            FileVersion? v = ParseLine(line.Trim());
            if (v is not null)
            {
                _versions.Add(v);
            }
        }
    }
}
