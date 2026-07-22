using StepWind.Core.Engine;
using StepWind.Core.Storage;
using Xunit;

namespace StepWind.Core.Tests;

/// <summary>
/// P2-2: the opt-in ".gitignore-aware" exclusion. Pins the matcher's handling of the common
/// gitignore syntax and the end-to-end behavior — a file git ignores is skipped ONLY when the
/// user turned the option on, and is versioned normally when off (the safe default).
/// </summary>
public class GitIgnoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sw-gitignore", Guid.NewGuid().ToString("N"));

    public GitIgnoreTests() => Directory.CreateDirectory(_root);

    private static GitIgnoreMatcher M(params string[] lines) => GitIgnoreMatcher.Parse(lines);

    [Fact]
    public void Extension_pattern_matches_at_any_depth()
    {
        GitIgnoreMatcher m = M("*.log");
        Assert.True(m.IsIgnored("x.log"));
        Assert.True(m.IsIgnored("sub/deep/y.log"));
        Assert.False(m.IsIgnored("x.txt"));
    }

    [Fact]
    public void Directory_only_pattern_ignores_contents_not_a_like_named_file()
    {
        GitIgnoreMatcher m = M("build/");
        Assert.True(m.IsIgnored("build/out.js"));
        Assert.True(m.IsIgnored("a/build/out.js"));
        Assert.False(m.IsIgnored("build.txt"));
    }

    [Fact]
    public void Leading_slash_anchors_to_the_repo_root()
    {
        GitIgnoreMatcher m = M("/root.txt");
        Assert.True(m.IsIgnored("root.txt"));
        Assert.False(m.IsIgnored("sub/root.txt"));
    }

    [Fact]
    public void Bare_name_matches_at_any_depth()
    {
        GitIgnoreMatcher m = M("node_modules");
        Assert.True(m.IsIgnored("node_modules/react/index.js"));
        Assert.True(m.IsIgnored("packages/app/node_modules/x"));
    }

    [Fact]
    public void Double_star_spans_directories()
    {
        GitIgnoreMatcher m = M("**/temp");
        Assert.True(m.IsIgnored("temp/file"));
        Assert.True(m.IsIgnored("a/b/temp/file"));
    }

    [Fact]
    public void Negation_reinstates_a_previously_ignored_file()
    {
        GitIgnoreMatcher m = M("*.log", "!keep.log");
        Assert.True(m.IsIgnored("debug.log"));
        Assert.False(m.IsIgnored("keep.log"));
    }

    [Fact]
    public void Comments_and_blanks_are_ignored()
    {
        GitIgnoreMatcher m = M("# a comment", "", "  ", "*.tmp");
        Assert.True(m.IsIgnored("x.tmp"));
        Assert.False(m.IsIgnored("x.keep"));
    }

    [Fact]
    public void Engine_skips_gitignored_files_only_when_the_option_is_on()
    {
        // The watched root doubles as a git repo with a .gitignore.
        // Use dir/file names that AREN'T in the always-on builtin exclusions (no "build"/"dist"/…),
        // so this test isolates the .gitignore behavior rather than the builtin skips.
        string repo = Path.Combine(_root, "Docs");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        File.WriteAllText(Path.Combine(repo, ".gitignore"), "secret.env\ngenerated/\n");
        File.WriteAllText(Path.Combine(repo, "keep.txt"), "keep me");
        File.WriteAllText(Path.Combine(repo, "secret.env"), "API_KEY=xyz");
        Directory.CreateDirectory(Path.Combine(repo, "generated"));
        File.WriteAllText(Path.Combine(repo, "generated", "out.js"), "generated");

        var store = new VersionStore(
            new BlobStore(Path.Combine(_root, "store"), new GzipBlobCodec()),
            new VersionLog(Path.Combine(_root, "store", "versions.jsonl")));

        // OFF (default): everything is versioned, even git-ignored files.
        bool respect = false;
        using (var engine = new WatchEngine(store, new PathExclusions(), [repo], respectGitIgnore: () => respect))
        {
            Assert.True(engine.TryCapture(Path.Combine(repo, "secret.env")));
            Assert.True(engine.TryCapture(Path.Combine(repo, "generated", "out.js")));
        }

        Assert.NotEmpty(store.Log.History("Docs/secret.env"));

        // ON: git-ignored files are skipped; tracked files still captured.
        respect = true;
        var store2 = new VersionStore(
            new BlobStore(Path.Combine(_root, "store2"), new GzipBlobCodec()),
            new VersionLog(Path.Combine(_root, "store2", "versions.jsonl")));
        using var engine2 = new WatchEngine(store2, new PathExclusions(), [repo], respectGitIgnore: () => respect);

        Assert.True(engine2.TryCapture(Path.Combine(repo, "keep.txt")));
        Assert.False(engine2.TryCapture(Path.Combine(repo, "secret.env")));
        Assert.False(engine2.TryCapture(Path.Combine(repo, "generated", "out.js")));

        Assert.Single(store2.Log.History("Docs/keep.txt"));
        Assert.Empty(store2.Log.History("Docs/secret.env"));
        Assert.Empty(store2.Log.History("Docs/generated/out.js"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
