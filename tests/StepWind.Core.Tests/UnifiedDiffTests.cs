using StepWind.Core.Diffing;
using Xunit;

namespace StepWind.Core.Tests;

/// <summary>
/// UnifiedDiff uses Hirschberg's linear-space LCS split, which is easy to get subtly wrong at
/// the boundaries (empty ranges, single-line ranges, split points at the very edge). These
/// tests pin the observable contract (hunk format, correctness of the underlying alignment)
/// and — most importantly — differentially fuzz-test against a trivial, obviously-correct
/// O(n*m) reference LCS so a bug in the space-optimized algorithm can't hide.
/// </summary>
public class UnifiedDiffTests
{
    [Fact]
    public void Identical_text_has_no_differences()
    {
        Assert.Equal("(no differences)", UnifiedDiff.Diff("a\nb\nc", "a\nb\nc", "old", "new"));
    }

    [Fact]
    public void Empty_old_is_all_insertions()
    {
        string diff = UnifiedDiff.Diff("", "line1\nline2", "old", "new");
        Assert.Contains("+line1", diff);
        Assert.Contains("+line2", diff);

        // No actual deletion LINES (a hunk header like "@@ -0,0 +1,2 @@" legitimately contains
        // a '-' as part of the line-count field, so this must check line prefixes, not do a
        // blanket substring search across the whole diff).
        Assert.DoesNotContain(diff.Split('\n'), l => l.StartsWith('-') && !l.StartsWith("---"));
    }

    [Fact]
    public void Empty_new_is_all_deletions()
    {
        string diff = UnifiedDiff.Diff("line1\nline2", "", "old", "new");
        Assert.Contains("-line1", diff);
        Assert.Contains("-line2", diff);
    }

    [Fact]
    public void Single_line_change_produces_a_minimal_hunk()
    {
        string diff = UnifiedDiff.Diff("a\nb\nc\nd\ne", "a\nb\nX\nd\ne", "old", "new");
        Assert.Contains("-c", diff);
        Assert.Contains("+X", diff);
        Assert.Contains(" a", diff); // context line kept
        Assert.Contains(" d", diff); // context line kept
    }

    /// <summary>
    /// Builds "line0".."line{count-1}" as a real array (not a joined string later mutated with
    /// string.Replace) — replacing a substring like "line5" in the joined text would ALSO hit
    /// "line50".."line59" (they contain "line5" as a prefix), silently changing 11 lines
    /// instead of 1. Index-based mutation avoids that trap entirely.
    /// </summary>
    private static string[] NumberedLines(int count) => [.. Enumerable.Range(0, count).Select(i => $"line{i}")];

    [Fact]
    public void Common_prefix_and_suffix_are_not_shown_as_changed_outside_context()
    {
        string[] lines = NumberedLines(50);
        string a = string.Join('\n', lines);
        lines[25] = "REPLACED";
        string b = string.Join('\n', lines);
        string diff = UnifiedDiff.Diff(a, b, "old", "new");

        Assert.DoesNotContain("line0\n", diff); // far from the change, outside context window
        Assert.Contains("REPLACED", diff);
        Assert.Contains("line24", diff); // context line just before the change
        Assert.Contains("line26", diff); // context line just after the change
    }

    [Fact]
    public void Two_separate_changes_far_apart_produce_two_hunks()
    {
        string[] lines = NumberedLines(100);
        string a = string.Join('\n', lines);
        lines[5] = "A";
        lines[90] = "B";
        string diff = UnifiedDiff.Diff(a, string.Join('\n', lines), "old", "new");

        int hunkCount = diff.Split('\n').Count(l => l.StartsWith("@@"));
        Assert.Equal(2, hunkCount);
    }

    [Fact]
    public void Adjacent_changes_within_context_merge_into_one_hunk()
    {
        string[] lines = NumberedLines(30);
        string a = string.Join('\n', lines);
        lines[10] = "A";
        lines[12] = "B"; // 1 line apart, well within the default context of 3
        string diff = UnifiedDiff.Diff(a, string.Join('\n', lines), "old", "new");

        int hunkCount = diff.Split('\n').Count(l => l.StartsWith("@@"));
        Assert.Equal(1, hunkCount);
    }

    [Fact]
    public void Binary_detection_flags_a_null_byte()
    {
        Assert.True(UnifiedDiff.LooksBinary([0x50, 0x4B, 0x00, 0x03]));
        Assert.False(UnifiedDiff.LooksBinary("hello world"u8.ToArray()));
    }

    [Fact]
    public void Oversized_input_degrades_to_a_summary_instead_of_crashing()
    {
        string huge = string.Join('\n', Enumerable.Range(0, UnifiedDiff.MaxLines + 500).Select(i => $"l{i}"));
        string result = UnifiedDiff.Diff(huge, huge + "\nextra", "old", "new");
        Assert.Contains("too large", result);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(18)]
    [InlineData(19)]
    [InlineData(20)]
    public void Randomized_diffs_reconstruct_exactly_to_the_new_text(int seed)
    {
        // The real correctness property that matters: applying the emitted ops (Equal keeps
        // the old line, Delete drops it, Insert adds the new line) must reconstruct the exact
        // 'new' text, for every random pair — not just the hand-picked examples above.
        var rng = new Random(seed);
        string[] alphabet = ["alpha", "beta", "gamma", "delta", "epsilon", "zeta"];

        string[] a = RandomLines(rng, alphabet, count: rng.Next(0, 40));
        string[] b = MutateLines(rng, a, alphabet);

        string oldText = string.Join('\n', a);
        string newText = string.Join('\n', b);

        // A huge context forces every line into the (single) hunk, so replaying the printed
        // diff can be checked against the FULL new text — with the default context=3, distant
        // unchanged lines are correctly omitted from the output, which would make this
        // reconstruction check fail for reasons that have nothing to do with correctness.
        string diff = UnifiedDiff.Diff(oldText, newText, "old", "new", context: 1_000_000);
        if (diff == "(no differences)")
        {
            Assert.Equal(oldText, newText);
            return;
        }

        Assert.Equal(newText, ReconstructNewFromDiff(diff));
    }

    [Theory]
    [InlineData(101)]
    [InlineData(102)]
    [InlineData(103)]
    [InlineData(104)]
    [InlineData(105)]
    public void Alignment_length_matches_an_independent_reference_LCS(int seed)
    {
        // Cross-check the space-optimized algorithm's chosen alignment length against a
        // trivial, obviously-correct O(n*m) reference implementation. If Hirschberg's split
        // math is subtly off, this is what would catch it (a merely-valid-but-suboptimal
        // alignment would still reconstruct correctly above, but would show as EXTRA
        // delete+insert pairs instead of Equal — i.e. a shorter LCS than optimal).
        var rng = new Random(seed);
        string[] alphabet = ["x", "y", "z"]; // small alphabet -> lots of repeats, stresses tie-breaking
        string[] a = RandomLines(rng, alphabet, count: rng.Next(1, 25));
        string[] b = RandomLines(rng, alphabet, count: rng.Next(1, 25));

        int expected = ReferenceLcsLength(a, b);

        // Same reasoning as above: force everything into one hunk so every Equal op is
        // actually printed and countable, regardless of how far apart the changes are.
        string diff = UnifiedDiff.Diff(string.Join('\n', a), string.Join('\n', b), "old", "new", context: 1_000_000);
        int equalLines = diff == "(no differences)"
            ? a.Length
            : diff.Split('\n').Count(l => l.StartsWith(' '));

        Assert.Equal(expected, equalLines);
    }

    [Theory]
    [InlineData(201)]
    [InlineData(202)]
    [InlineData(203)]
    public void Large_inputs_reconstruct_exactly_and_exercise_the_recursive_split(int seed)
    {
        // Small inputs above mostly bottom out in the n==1 base case after a couple of
        // splits. A few hundred lines forces multiple levels of the actual Hirschberg
        // recursion (LcsRow / LcsRowFromEnd at shrinking sub-ranges), which is where an
        // off-by-one in the split-point search would most likely surface.
        var rng = new Random(seed);
        string[] alphabet = Enumerable.Range(0, 12).Select(i => $"tok{i}").ToArray();
        string[] a = RandomLines(rng, alphabet, count: 400 + rng.Next(400));
        string[] b = MutateLines(rng, a, alphabet);
        for (int extra = 0; extra < 30; extra++)
        {
            b = MutateLines(rng, b, alphabet); // compound edits so changes scatter throughout
        }

        string oldText = string.Join('\n', a);
        string newText = string.Join('\n', b);
        string diff = UnifiedDiff.Diff(oldText, newText, "old", "new", context: 1_000_000);

        if (diff == "(no differences)")
        {
            Assert.Equal(oldText, newText);
            return;
        }

        Assert.Equal(newText, ReconstructNewFromDiff(diff));

        int expectedLcs = ReferenceLcsLength(a, b);
        int equalLines = diff.Split('\n').Count(l => l.StartsWith(' '));
        Assert.Equal(expectedLcs, equalLines);
    }

    private static string[] RandomLines(Random rng, string[] alphabet, int count)
        => Enumerable.Range(0, count).Select(_ => alphabet[rng.Next(alphabet.Length)]).ToArray();

    private static string[] MutateLines(Random rng, string[] source, string[] alphabet)
    {
        var result = new List<string>(source);
        int edits = rng.Next(0, 6);
        for (int i = 0; i < edits && result.Count >= 0; i++)
        {
            int op = rng.Next(3);
            int pos = result.Count == 0 ? 0 : rng.Next(result.Count + 1);
            switch (op)
            {
                case 0: // insert
                    result.Insert(pos, alphabet[rng.Next(alphabet.Length)]);
                    break;
                case 1 when result.Count > 0: // delete
                    result.RemoveAt(rng.Next(result.Count));
                    break;
                case 2 when result.Count > 0: // replace
                    result[rng.Next(result.Count)] = alphabet[rng.Next(alphabet.Length)];
                    break;
            }
        }

        return result.ToArray();
    }

    private static int ReferenceLcsLength(string[] a, string[] b)
    {
        var dp = new int[a.Length + 1, b.Length + 1];
        for (int i = a.Length - 1; i >= 0; i--)
        {
            for (int j = b.Length - 1; j >= 0; j--)
            {
                dp[i, j] = a[i] == b[j] ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);
            }
        }

        return dp[0, 0];
    }

    /// <summary>
    /// Replays a unified diff's ' '/'+' lines (ignoring '-') to reconstruct the new text.
    /// Callers must handle the "(no differences)" sentinel themselves before calling this.
    /// </summary>
    private static string ReconstructNewFromDiff(string diff)
    {
        var lines = new List<string>();
        foreach (string raw in diff.Split('\n'))
        {
            if (raw.StartsWith("---") || raw.StartsWith("+++") || raw.StartsWith("@@") || raw.Length == 0)
            {
                continue;
            }

            char prefix = raw[0];
            string content = raw[1..];
            if (prefix is ' ' or '+')
            {
                lines.Add(content);
            }
        }

        return string.Join('\n', lines);
    }
}
