using System.Collections.Concurrent;

namespace StepWind.Core.Storage;

/// <summary>
/// Reads the current git branch + short commit for a file's repository, WITHOUT shelling out to
/// git or taking a dependency — just reads <c>.git/HEAD</c> and the referenced ref file. Used to
/// annotate a captured version with "which branch/commit was checked out when this was saved",
/// so a developer scrolling history can tell a version apart by its git context. Best-effort:
/// returns null outside a repo or on any read hiccup, and never throws into capture.
/// </summary>
public static class GitInfo
{
    // Cache the repo-root (or "not a repo") for a directory so we don't walk up the tree on every
    // capture. Keyed by the file's directory; bounded implicitly by the number of distinct dirs.
    private static readonly ConcurrentDictionary<string, string?> RepoRootCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A short "branch @ shortsha" (or "(detached) shortsha") for the file's repo, or null.</summary>
    public static string? Describe(string filePath)
    {
        try
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
            if (dir is null)
            {
                return null;
            }

            string? gitDir = FindGitDir(dir);
            if (gitDir is null)
            {
                return null;
            }

            string headPath = Path.Combine(gitDir, "HEAD");
            if (!File.Exists(headPath))
            {
                return null;
            }

            string head = File.ReadAllText(headPath).Trim();
            if (head.StartsWith("ref:", StringComparison.Ordinal))
            {
                // "ref: refs/heads/main" — a normal branch checkout.
                string reference = head[4..].Trim();
                string branch = reference.StartsWith("refs/heads/", StringComparison.Ordinal)
                    ? reference["refs/heads/".Length..]
                    : reference;
                string? sha = ResolveRef(gitDir, reference);
                return sha is null ? branch : $"{branch} @ {Short(sha)}";
            }

            // Detached HEAD — the file itself is a 40-hex commit id.
            return IsSha(head) ? $"(detached) {Short(head)}" : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Walks up from <paramref name="dir"/> to find the repo's git directory (handles a `.git` file too).</summary>
    private static string? FindGitDir(string dir)
    {
        return RepoRootCache.GetOrAdd(dir, static d =>
        {
            var current = new DirectoryInfo(d);
            while (current is not null)
            {
                string dotGit = Path.Combine(current.FullName, ".git");
                if (Directory.Exists(dotGit))
                {
                    return dotGit;
                }

                if (File.Exists(dotGit))
                {
                    // Worktree/submodule: ".git" is a file "gitdir: <path>".
                    string content = File.ReadAllText(dotGit).Trim();
                    const string prefix = "gitdir:";
                    if (content.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        string target = content[prefix.Length..].Trim();
                        string resolved = Path.IsPathFullyQualified(target)
                            ? target
                            : Path.GetFullPath(Path.Combine(current.FullName, target));
                        return Directory.Exists(resolved) ? resolved : null;
                    }
                }

                current = current.Parent;
            }

            return null;
        });
    }

    private static string? ResolveRef(string gitDir, string reference)
    {
        try
        {
            string loose = Path.Combine(gitDir, reference.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(loose))
            {
                string sha = File.ReadAllText(loose).Trim();
                return IsSha(sha) ? sha : null;
            }

            // Fall back to packed-refs (git packs refs it hasn't touched recently).
            string packed = Path.Combine(gitDir, "packed-refs");
            if (File.Exists(packed))
            {
                foreach (string raw in File.ReadLines(packed))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] is '#' or '^')
                    {
                        continue;
                    }

                    int sp = line.IndexOf(' ');
                    if (sp == 40 && line[(sp + 1)..] == reference)
                    {
                        return line[..40];
                    }
                }
            }
        }
        catch
        {
            // ignore — annotation is best-effort
        }

        return null;
    }

    private static bool IsSha(string s) => s.Length >= 7 && s.Length <= 64 && s.All(Uri.IsHexDigit);

    private static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;
}
