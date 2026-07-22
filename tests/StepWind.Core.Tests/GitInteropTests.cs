using StepWind.Core.Engine;
using StepWind.Core.Storage;
using Xunit;

namespace StepWind.Core.Tests;

/// <summary>
/// P2-2: git interplay. Pins the VCS-directory skips (present in the exclusion list but never
/// asserted before) and the git branch/commit annotation stamped onto versions captured inside a
/// repository, so a developer can tell versions apart by their git context.
/// </summary>
public class GitInteropTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sw-git", Guid.NewGuid().ToString("N"));

    public GitInteropTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData(@"C:\proj\.git\config")]
    [InlineData(@"C:\proj\.svn\entries")]
    [InlineData(@"C:\proj\.hg\store")]
    [InlineData(@"C:\proj\sub\.git\HEAD")]
    public void Version_control_metadata_dirs_are_never_versioned(string path)
    {
        var ex = new PathExclusions();
        Assert.True(ex.IsExcludedByDirectory(path), $"{path} should be excluded");
        Assert.False(ex.ShouldVersion(path, System.IO.FileAttributes.Normal, 10));
    }

    [Fact]
    public void GitInfo_returns_null_outside_a_repository()
    {
        string file = Path.Combine(_root, "loose.txt");
        File.WriteAllText(file, "not in a repo");
        Assert.Null(GitInfo.Describe(file));
    }

    [Fact]
    public void GitInfo_reads_the_branch_and_short_commit_from_a_repo()
    {
        // Fabricate a minimal .git the way real git lays it out: HEAD -> refs/heads/main -> sha.
        string repo = Path.Combine(_root, "repo");
        string gitDir = Path.Combine(repo, ".git");
        Directory.CreateDirectory(Path.Combine(gitDir, "refs", "heads"));
        File.WriteAllText(Path.Combine(gitDir, "HEAD"), "ref: refs/heads/main\n");
        File.WriteAllText(Path.Combine(gitDir, "refs", "heads", "main"), "3a1b2c1d4e5f60718293a4b5c6d7e8f901234567\n");

        string file = Path.Combine(repo, "src", "app.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "code");

        Assert.Equal("main @ 3a1b2c1", GitInfo.Describe(file));
    }

    [Fact]
    public void GitInfo_handles_detached_head()
    {
        string repo = Path.Combine(_root, "detached");
        string gitDir = Path.Combine(repo, ".git");
        Directory.CreateDirectory(gitDir);
        File.WriteAllText(Path.Combine(gitDir, "HEAD"), "9f8e7d6c5b4a39281706f5e4d3c2b1a098765432\n");

        string file = Path.Combine(repo, "x.txt");
        File.WriteAllText(file, "x");
        Assert.Equal("(detached) 9f8e7d6", GitInfo.Describe(file));
    }

    [Fact]
    public void A_captured_version_in_a_repo_is_annotated_with_its_git_ref()
    {
        string repo = Path.Combine(_root, "Docs"); // acts as the watched root AND a git repo
        string gitDir = Path.Combine(repo, ".git");
        Directory.CreateDirectory(Path.Combine(gitDir, "refs", "heads"));
        File.WriteAllText(Path.Combine(gitDir, "HEAD"), "ref: refs/heads/feature\n");
        File.WriteAllText(Path.Combine(gitDir, "refs", "heads", "feature"), "abcdef1234567890abcdef1234567890abcdef12\n");

        var store = new VersionStore(
            new BlobStore(Path.Combine(_root, "store"), new GzipBlobCodec()),
            new VersionLog(Path.Combine(_root, "store", "versions.jsonl")));

        string file = Path.Combine(repo, "notes.md");
        File.WriteAllText(file, "# notes");
        FileVersion v = store.Capture(file, "Docs/notes.md");

        Assert.Equal("feature @ abcdef1", v.GitRef);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
