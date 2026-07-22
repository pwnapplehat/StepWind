using System.Text;
using System.Text.RegularExpressions;

namespace StepWind.Core.Storage;

/// <summary>
/// Matches paths against a repository's root <c>.gitignore</c>, so StepWind can optionally skip
/// versioning the project-specific junk a developer already tells git to ignore (build output,
/// generated files) beyond the built-in exclusion list. Implements the common gitignore subset:
/// comments/blank lines, <c>!</c> negation (last match wins), leading <c>/</c> anchor, trailing
/// <c>/</c> directory-only, <c>*</c>/<c>?</c> within a segment, and <c>**</c> across segments;
/// basename patterns (no slash) match at any depth. Nested .gitignore files are NOT consulted —
/// the root one covers the overwhelming majority. Off by default (opt-in), because silently NOT
/// versioning a file is a protection gap, so the user must choose this behavior.
/// </summary>
public sealed class GitIgnoreMatcher
{
    private readonly List<Rule> _rules;

    private GitIgnoreMatcher(List<Rule> rules) => _rules = rules;

    private sealed record Rule(Regex Regex, bool Negate, bool DirOnly);

    /// <summary>Loads the matcher from a repo root's <c>.gitignore</c>, or null if there isn't one.</summary>
    public static GitIgnoreMatcher? ForRepo(string repoRoot)
    {
        try
        {
            string path = Path.Combine(repoRoot, ".gitignore");
            return File.Exists(path) ? Parse(File.ReadAllLines(path)) : null;
        }
        catch
        {
            return null;
        }
    }

    public static GitIgnoreMatcher Parse(IEnumerable<string> lines)
    {
        var rules = new List<Rule>();
        foreach (string raw in lines)
        {
            string line = raw.TrimEnd();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            bool negate = line[0] == '!';
            if (negate)
            {
                line = line[1..];
            }

            if (line.Length == 0)
            {
                continue;
            }

            bool dirOnly = line.EndsWith('/');
            if (dirOnly)
            {
                line = line[..^1];
            }

            // A slash anywhere but a trailing one anchors the pattern to the repo root; a bare
            // name (no slash) matches at any depth.
            bool anchored = line.TrimEnd('/').Contains('/');
            string core = line.TrimStart('/');

            rules.Add(new Rule(new Regex(BuildRegex(core, anchored), RegexOptions.CultureInvariant), negate, dirOnly));
        }

        return new GitIgnoreMatcher(rules);
    }

    /// <summary>
    /// Whether <paramref name="relativePath"/> (repo-relative, '/'-separated) is ignored. Last
    /// matching rule wins; a later <c>!</c> rule can un-ignore something an earlier rule matched.
    /// </summary>
    public bool IsIgnored(string relativePath, bool isDir = false)
    {
        string p = relativePath.Replace('\\', '/').Trim('/');
        bool ignored = false;
        foreach (Rule r in _rules)
        {
            if (r.DirOnly && !isDir && !HasParentMatch(r, p))
            {
                // A "build/" rule only ignores a FILE when it's under that directory.
                if (!r.Regex.IsMatch(FirstDirs(p)))
                {
                    continue;
                }
            }

            if (r.Regex.IsMatch(p))
            {
                ignored = !r.Negate;
            }
        }

        return ignored;
    }

    // For a dir-only rule matching a file path, the rule matches if some ANCESTOR directory of the
    // file matches — we test progressively shorter prefixes.
    private static bool HasParentMatch(Rule r, string path)
    {
        int slash = path.LastIndexOf('/');
        while (slash > 0)
        {
            if (r.Regex.IsMatch(path[..slash]))
            {
                return true;
            }

            slash = path.LastIndexOf('/', slash - 1);
        }

        return false;
    }

    private static string FirstDirs(string path)
    {
        int slash = path.IndexOf('/');
        return slash < 0 ? path : path[..slash];
    }

    /// <summary>Translates a gitignore glob to an anchored regex that also matches anything beneath it.</summary>
    private static string BuildRegex(string glob, bool anchored)
    {
        var sb = new StringBuilder();
        sb.Append('^');
        if (!anchored)
        {
            sb.Append("(?:.*/)?"); // basename patterns match at any depth
        }

        for (int i = 0; i < glob.Length; i++)
        {
            char c = glob[i];
            switch (c)
            {
                case '*':
                    if (i + 1 < glob.Length && glob[i + 1] == '*')
                    {
                        i++;
                        // "**/" → any number of dirs; "**" elsewhere → anything.
                        if (i + 1 < glob.Length && glob[i + 1] == '/')
                        {
                            i++;
                            sb.Append("(?:.*/)?");
                        }
                        else
                        {
                            sb.Append(".*");
                        }
                    }
                    else
                    {
                        sb.Append("[^/]*");
                    }

                    break;
                case '?':
                    sb.Append("[^/]");
                    break;
                case '.':
                case '(':
                case ')':
                case '+':
                case '|':
                case '^':
                case '$':
                case '{':
                case '}':
                case '[':
                case ']':
                case '\\':
                    sb.Append('\\').Append(c);
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }

        // Match the path itself OR anything under it (so "build" ignores "build/out.js").
        sb.Append("(?:/.*)?$");
        return sb.ToString();
    }
}
