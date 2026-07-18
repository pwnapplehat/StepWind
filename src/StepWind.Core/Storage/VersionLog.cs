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

    public VersionLog(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Load();
    }

    public IReadOnlyList<FileVersion> All => _versions;

    /// <summary>All versions of one file, oldest first.</summary>
    public IReadOnlyList<FileVersion> History(string relativePath)
        => [.. _versions.Where(v => v.RelativePath.Equals(relativePath, StringComparison.OrdinalIgnoreCase))
                        .OrderBy(v => v.CapturedUtc)];

    /// <summary>Durably appends a version and returns it.</summary>
    public FileVersion Append(FileVersion version)
    {
        string line = JsonSerializer.Serialize(version, Json);
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
                    writer.WriteLine(JsonSerializer.Serialize(v, Json));
                }

                writer.Flush();
                fs.Flush(flushToDisk: true);
            }

            File.Move(temp, _path, overwrite: true);
            _versions.Clear();
            _versions.AddRange(keep);
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

            try
            {
                FileVersion? v = JsonSerializer.Deserialize<FileVersion>(line, Json);
                if (v is not null)
                {
                    _versions.Add(v);
                }
            }
            catch (JsonException)
            {
                // Truncated final line from a crash mid-append — safe to skip; everything
                // before it is intact because appends are line-atomic.
            }
        }
    }
}
