using System.IO;

namespace StepWind.Core.Engine;

/// <summary>
/// Decides what StepWind must NOT version. Getting this right on day one matters: versioning
/// regenerable build junk or huge caches would waste enormous space, and — the subtle one —
/// versioning a OneDrive/cloud "online-only" placeholder would force Windows to DOWNLOAD the
/// whole file just to copy it, defeating the point of freeing that space (a trap we already
/// hit in BitBroom). Pure and testable; the engine also hard-excludes its own store path.
/// </summary>
public sealed class PathExclusions
{
    // Regenerable dev artifacts and heavy caches — no point versioning these.
    private static readonly HashSet<string> ExcludedDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "bower_components", ".git", ".svn", ".hg",
        "bin", "obj", "target", "build", "dist", "out",
        ".venv", "venv", "env", "__pycache__", ".mypy_cache", ".pytest_cache",
        ".gradle", ".idea", ".vs", ".vscode-test",
        "$RECYCLE.BIN", "System Volume Information",
        ".stepwind", // never version our own store
    };

    private static readonly HashSet<string> ExcludedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".tmp", ".temp", ".log", ".swp", ".swo", ".bak", ".part", ".crdownload",
        ".pyc", ".pyo", ".obj", ".o", ".class",
        ".sys", ".pagefile", ".etl", ".dmp",
    };

    // Files this big are almost always disk images / VMs the user doesn't want per-save
    // history of; configurable ceiling keeps the store sane. 0 = no limit.
    public long MaxFileBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    private readonly List<string> _extraPrefixes = [];

    public void ExcludePrefix(string absolutePrefix)
        => _extraPrefixes.Add(absolutePrefix.TrimEnd('\\', '/'));

    /// <summary>
    /// True if any path segment is an excluded directory name. When <paramref name="root"/> (the
    /// protected folder the file lives in) is given, only the part BELOW it is judged: the user
    /// chose that folder on purpose, so a protected folder that happens to sit under — or be named —
    /// "build", "dist", "out", "env" and so on must not have every file in it skipped.
    /// </summary>
    public bool IsExcludedByDirectory(string fullPath, string? root = null)
        => ExcludedDirectoryName(fullPath, root) is not null;

    /// <summary>The excluded directory name that applies to <paramref name="fullPath"/>, or null.</summary>
    public string? ExcludedDirectoryName(string fullPath, string? root = null)
    {
        foreach (string segment in BelowRoot(fullPath, root).Split('\\', '/'))
        {
            if (ExcludedDirNames.Contains(segment))
            {
                return segment;
            }
        }

        return null;
    }

    private static string BelowRoot(string fullPath, string? root)
    {
        if (string.IsNullOrEmpty(root))
        {
            return fullPath;
        }

        string trimmed = root.TrimEnd('\\', '/');
        if (trimmed.Length > 0 && fullPath.Length > trimmed.Length
            && fullPath.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase)
            && (fullPath[trimmed.Length] == '\\' || fullPath[trimmed.Length] == '/'))
        {
            return fullPath[(trimmed.Length + 1)..];
        }

        return fullPath;
    }

    /// <summary>
    /// Why a file inside <paramref name="root"/> is skipped by the name/type/path rules, in words a
    /// person can act on, or null when none of them applies. Used to explain an empty history.
    /// </summary>
    public string? ExclusionReason(string fullPath, string? root = null)
    {
        if (ExcludedDirectoryName(fullPath, root) is { } dir)
        {
            return $"in a folder named \"{dir}\"";
        }

        if (IsExcludedByExtension(fullPath))
        {
            return $"{Path.GetExtension(fullPath).ToLowerInvariant()} files";
        }

        return IsUnderExcludedPrefix(fullPath) ? "inside a folder you excluded" : null;
    }

    public bool IsExcludedByExtension(string fullPath)
        => ExcludedExtensions.Contains(Path.GetExtension(fullPath));

    /// <summary>True when <paramref name="fullPath"/> is the prefix or a file inside it.</summary>
    public static bool IsUnderAnyPrefix(string? fullPath, IEnumerable<string> prefixes)
    {
        if (string.IsNullOrEmpty(fullPath))
        {
            return false;
        }

        foreach (string raw in prefixes)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            string prefix = raw.TrimEnd('\\', '/');
            if (prefix.Length == 0)
            {
                continue;
            }

            if (fullPath.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                || fullPath.StartsWith(prefix + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || fullPath.StartsWith(prefix + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public bool IsUnderExcludedPrefix(string fullPath)
    {
        foreach (string prefix in _extraPrefixes)
        {
            if (fullPath.StartsWith(prefix + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || fullPath.Equals(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Full decision for a candidate file. <paramref name="attributes"/> is the file's
    /// attributes (pass the real ones on Windows; the cloud-placeholder and size checks are
    /// skipped when unavailable). Never versions cloud online-only files (would hydrate them),
    /// reparse points, or over-size files.
    /// </summary>
    public bool ShouldVersion(string fullPath, FileAttributes attributes, long sizeBytes, string? root = null)
    {
        if (IsExcludedByDirectory(fullPath, root) || IsExcludedByExtension(fullPath) || IsUnderExcludedPrefix(fullPath))
        {
            return false;
        }

        if (IsCloudPlaceholder(attributes))
        {
            return false; // online-only — versioning it would download the whole file
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            return false; // symlink/junction — follow the real target elsewhere, don't dupe
        }

        if (MaxFileBytes > 0 && sizeBytes > MaxFileBytes)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// True for a OneDrive/Files-On-Demand "online-only" placeholder: the bytes aren't local,
    /// so touching it to version it would trigger a full download.
    /// FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS (0x00400000) or OFFLINE (0x00001000).
    /// </summary>
    public static bool IsCloudPlaceholder(FileAttributes attributes)
    {
        const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;
        return (attributes & RecallOnDataAccess) != 0
            || (attributes & FileAttributes.Offline) != 0;
    }
}
