using System.Text;

namespace StepWind.Core.Diffing;

/// <summary>
/// Unified-diff engine for the AI/MCP surface: agents ask "what changed in this file" and get
/// back a standard @@ -a,b +c,d @@ hunk format they already know how to read.
///
/// Uses Hirschberg's linear-space LCS algorithm (recursively finds the length-maximizing split
/// point via one forward and one backward DP row, then recurses on each half) instead of a
/// naive full O(n*m) table. A plain table for two 20,000-line files would be 400 million cells
/// (~1.6 GB) — a real crash risk inside an always-on, elevated service. Hirschberg keeps
/// working memory at O(min(n,m)) per level while still finding an optimal alignment, not a
/// heuristic one.
///
/// <see cref="MaxLines"/> caps input size for a second reason: the named pipe that serves this
/// (and every other) request handles one connection at a time (see PipeServer), so a slow diff
/// would stall the GUI's status polling and any concurrent MCP call. The cap keeps worst-case
/// latency to a few tens of milliseconds.
/// </summary>
public static class UnifiedDiff
{
    public const int MaxLines = 6_000;

    /// <summary>True if the content looks binary (a null byte in the first 8 KB).</summary>
    public static bool LooksBinary(ReadOnlySpan<byte> content)
    {
        int probe = Math.Min(content.Length, 8192);
        for (int i = 0; i < probe; i++)
        {
            if (content[i] == 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Produces a unified diff of two texts. Returns "(no differences)" when equal.</summary>
    public static string Diff(string oldText, string newText, string oldLabel, string newLabel, int context = 3)
    {
        string[] a = SplitLines(oldText);
        string[] b = SplitLines(newText);
        if (a.Length > MaxLines || b.Length > MaxLines)
        {
            return $"(files too large to diff line-by-line: {a.Length:N0} vs {b.Length:N0} lines — showing sizes only)";
        }

        // Intern lines to small ints so every comparison downstream is an O(1) int compare
        // instead of a string compare.
        var table = new Dictionary<string, int>(StringComparer.Ordinal);
        int[] ia = Intern(a, table);
        int[] ib = Intern(b, table);

        var ops = new List<Op>(a.Length + b.Length);
        BuildDiff(ia, 0, ia.Length, ib, 0, ib.Length, ops);

        if (ops.TrueForAll(o => o.Kind == OpKind.Equal))
        {
            return "(no differences)";
        }

        return Format(ops, a, b, oldLabel, newLabel, context);
    }

    private static string[] SplitLines(string text)
        => text.Length == 0 ? [] : text.Replace("\r\n", "\n").Split('\n');

    private static int[] Intern(string[] lines, Dictionary<string, int> table)
    {
        var ids = new int[lines.Length];
        for (int i = 0; i < lines.Length; i++)
        {
            if (!table.TryGetValue(lines[i], out int id))
            {
                id = table.Count;
                table[lines[i]] = id;
            }

            ids[i] = id;
        }

        return ids;
    }

    private enum OpKind { Equal, Delete, Insert }

    private readonly record struct Op(OpKind Kind, int AIndex, int BIndex);

    /// <summary>
    /// Recursively diffs a[aLo,aHi) against b[bLo,bHi) and appends the resulting Equal/Delete/
    /// Insert ops, in left-to-right order, to <paramref name="ops"/>.
    /// </summary>
    private static void BuildDiff(int[] a, int aLo, int aHi, int[] b, int bLo, int bHi, List<Op> ops)
    {
        while (aLo < aHi && bLo < bHi && a[aLo] == b[bLo])
        {
            ops.Add(new Op(OpKind.Equal, aLo, bLo));
            aLo++;
            bLo++;
        }

        // Trim the common suffix too, but collect it — it must be appended AFTER whatever we
        // emit for the (now-shrunk) middle, so it can't go into `ops` yet.
        var suffix = new List<Op>();
        while (aHi > aLo && bHi > bLo && a[aHi - 1] == b[bHi - 1])
        {
            aHi--;
            bHi--;
            suffix.Add(new Op(OpKind.Equal, aHi, bHi));
        }

        if (aLo >= aHi)
        {
            for (int j = bLo; j < bHi; j++)
            {
                ops.Add(new Op(OpKind.Insert, -1, j));
            }
        }
        else if (bLo >= bHi)
        {
            for (int i = aLo; i < aHi; i++)
            {
                ops.Add(new Op(OpKind.Delete, i, -1));
            }
        }
        else if (aHi - aLo == 1)
        {
            // Base case: a single 'a' line either matches exactly one 'b' line (any occurrence
            // gives the same optimal LCS length of 1 — the first is as good as any) or none.
            int line = a[aLo];
            int matchAt = -1;
            for (int j = bLo; j < bHi; j++)
            {
                if (b[j] == line)
                {
                    matchAt = j;
                    break;
                }
            }

            if (matchAt < 0)
            {
                ops.Add(new Op(OpKind.Delete, aLo, -1));
                for (int j = bLo; j < bHi; j++)
                {
                    ops.Add(new Op(OpKind.Insert, -1, j));
                }
            }
            else
            {
                for (int j = bLo; j < matchAt; j++)
                {
                    ops.Add(new Op(OpKind.Insert, -1, j));
                }

                ops.Add(new Op(OpKind.Equal, aLo, matchAt));
                for (int j = matchAt + 1; j < bHi; j++)
                {
                    ops.Add(new Op(OpKind.Insert, -1, j));
                }
            }
        }
        else
        {
            int mid = aLo + (aHi - aLo) / 2;
            int m = bHi - bLo;
            int[] forward = LcsRow(a, aLo, mid, b, bLo, bHi);
            int[] backward = LcsRowFromEnd(a, mid, aHi, b, bLo, bHi);

            int bestJ = 0, bestScore = -1;
            for (int j = 0; j <= m; j++)
            {
                int score = forward[j] + backward[m - j];
                if (score > bestScore)
                {
                    bestScore = score;
                    bestJ = j;
                }
            }

            int bMid = bLo + bestJ;
            BuildDiff(a, aLo, mid, b, bLo, bMid, ops);
            BuildDiff(a, mid, aHi, b, bMid, bHi, ops);
        }

        for (int k = suffix.Count - 1; k >= 0; k--)
        {
            ops.Add(suffix[k]);
        }
    }

    /// <summary>row[j] = LCS length of a[aLo,aHi) and the first j lines of b[bLo,bHi). O(min) space.</summary>
    private static int[] LcsRow(int[] a, int aLo, int aHi, int[] b, int bLo, int bHi)
    {
        int m = bHi - bLo;
        var prev = new int[m + 1];
        var curr = new int[m + 1];
        for (int i = aLo; i < aHi; i++)
        {
            for (int j = 1; j <= m; j++)
            {
                curr[j] = a[i] == b[bLo + j - 1] ? prev[j - 1] + 1 : Math.Max(prev[j], curr[j - 1]);
            }

            (prev, curr) = (curr, prev);
        }

        return prev;
    }

    /// <summary>row[k] = LCS length of a[aLo,aHi) and the LAST k lines of b[bLo,bHi). O(min) space.</summary>
    private static int[] LcsRowFromEnd(int[] a, int aLo, int aHi, int[] b, int bLo, int bHi)
    {
        int m = bHi - bLo;
        var prev = new int[m + 1];
        var curr = new int[m + 1];
        for (int i = aHi - 1; i >= aLo; i--)
        {
            for (int k = 1; k <= m; k++)
            {
                curr[k] = a[i] == b[bHi - k] ? prev[k - 1] + 1 : Math.Max(prev[k], curr[k - 1]);
            }

            (prev, curr) = (curr, prev);
        }

        return prev;
    }

    /// <summary>Cosmetic only: within each contiguous non-equal run, print deletes before inserts.</summary>
    private static void ReorderChangeRuns(List<Op> ops)
    {
        int i = 0;
        while (i < ops.Count)
        {
            if (ops[i].Kind == OpKind.Equal)
            {
                i++;
                continue;
            }

            int start = i;
            while (i < ops.Count && ops[i].Kind != OpKind.Equal)
            {
                i++;
            }

            var deletes = new List<Op>();
            var inserts = new List<Op>();
            for (int k = start; k < i; k++)
            {
                (ops[k].Kind == OpKind.Delete ? deletes : inserts).Add(ops[k]);
            }

            int w = start;
            foreach (Op d in deletes)
            {
                ops[w++] = d;
            }

            foreach (Op ins in inserts)
            {
                ops[w++] = ins;
            }
        }
    }

    private static string Format(List<Op> ops, string[] a, string[] b, string oldLabel, string newLabel, int context)
    {
        ReorderChangeRuns(ops);

        int n = ops.Count;
        var isChange = new bool[n];
        for (int i = 0; i < n; i++)
        {
            isChange[i] = ops[i].Kind != OpKind.Equal;
        }

        var sb = new StringBuilder();
        sb.Append("--- ").Append(oldLabel).Append('\n');
        sb.Append("+++ ").Append(newLabel).Append('\n');

        int pos = 0;
        while (pos < n)
        {
            if (!isChange[pos])
            {
                pos++;
                continue;
            }

            int hunkStart = Math.Max(0, pos - context);
            int lastChange = pos;
            int scan = pos + 1;
            while (true)
            {
                int nextChange = scan;
                while (nextChange < n && !isChange[nextChange])
                {
                    nextChange++;
                }

                if (nextChange >= n || nextChange - lastChange - 1 > 2 * context)
                {
                    break;
                }

                lastChange = nextChange;
                scan = nextChange + 1;
            }

            int hunkEnd = Math.Min(n, lastChange + 1 + context);
            AppendHunk(sb, ops, hunkStart, hunkEnd, a, b);
            pos = hunkEnd;
        }

        return sb.ToString();
    }

    private static void AppendHunk(StringBuilder sb, List<Op> ops, int start, int end, string[] a, string[] b)
    {
        int aFirst = -1, bFirst = -1, aCount = 0, bCount = 0;
        for (int i = start; i < end; i++)
        {
            Op op = ops[i];
            if (op.Kind != OpKind.Insert)
            {
                aCount++;
                if (aFirst < 0)
                {
                    aFirst = op.AIndex;
                }
            }

            if (op.Kind != OpKind.Delete)
            {
                bCount++;
                if (bFirst < 0)
                {
                    bFirst = op.BIndex;
                }
            }
        }

        int aStart = aFirst >= 0 ? aFirst + 1 : LineNumberBefore(ops, start, useA: true);
        int bStart = bFirst >= 0 ? bFirst + 1 : LineNumberBefore(ops, start, useA: false);

        sb.Append("@@ -").Append(aStart).Append(',').Append(aCount)
          .Append(" +").Append(bStart).Append(',').Append(bCount).Append(" @@\n");

        for (int i = start; i < end; i++)
        {
            Op op = ops[i];
            char prefix = op.Kind switch { OpKind.Equal => ' ', OpKind.Delete => '-', _ => '+' };
            string line = op.Kind == OpKind.Insert ? b[op.BIndex] : a[op.AIndex];
            sb.Append(prefix).Append(line).Append('\n');
        }
    }

    private static int LineNumberBefore(List<Op> ops, int beforeIndex, bool useA)
    {
        for (int i = beforeIndex - 1; i >= 0; i--)
        {
            int idx = useA ? ops[i].AIndex : ops[i].BIndex;
            if (idx >= 0)
            {
                return idx + 1;
            }
        }

        return 0;
    }
}
