using StepWind.Core.Engine;
using StepWind.Core.Storage;
using Xunit;

namespace StepWind.Core.Tests;

/// <summary>
/// P1-9: files in deep folder trees (paths over the legacy 260-char limit) must still be
/// versioned — developers hit this constantly. Pins the extended-length prefixing and an
/// end-to-end capture of a genuinely-too-long path.
/// </summary>
public class LongPathTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sw-long", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(@"C:\short\path.txt", false)]
    [InlineData(@"\\?\C:\already\extended.txt", false)]
    [InlineData(@"relative\path.txt", false)]
    public void Short_or_already_extended_or_relative_paths_are_unchanged(string path, bool _)
    {
        Assert.Equal(path, LongPath.Of(path));
    }

    [Fact]
    public void A_long_local_path_gets_the_extended_prefix()
    {
        string deep = @"C:\" + string.Join('\\', Enumerable.Repeat("segmentsegmentsegment", 15)) + @"\file.txt";
        Assert.True(deep.Length >= 248);
        Assert.Equal(@"\\?\" + deep, LongPath.Of(deep));
    }

    [Fact]
    public void A_long_unc_path_uses_the_unc_extended_form()
    {
        string deep = @"\\server\share\" + string.Join('\\', Enumerable.Repeat("segmentsegmentsegment", 15)) + @"\file.txt";
        Assert.True(deep.Length >= 248);
        Assert.Equal(@"\\?\UNC\server\share\" + string.Join('\\', Enumerable.Repeat("segmentsegmentsegment", 15)) + @"\file.txt",
            LongPath.Of(deep));
    }

    [Fact]
    public void Capture_versions_a_file_at_a_path_over_260_chars()
    {
        // Build a watched root and, under it, a directory chain long enough that the file path
        // exceeds 260 chars. Create it via the extended prefix so the TEST setup doesn't itself
        // depend on process long-path awareness — the capture path is what's under test.
        string watch = Path.Combine(_root, "Docs");
        Directory.CreateDirectory(watch);

        string deepRel = string.Join('\\', Enumerable.Repeat("deepfolder_name_padding", 9));
        string deepDir = Path.Combine(watch, deepRel);
        Directory.CreateDirectory(LongPath.Of(deepDir));
        string file = Path.Combine(deepDir, "burried.txt");
        Assert.True(file.Length > 260, $"path was only {file.Length} chars");

        File.WriteAllText(LongPath.Of(file), "recoverable even when deeply nested");

        var store = new VersionStore(
            new BlobStore(Path.Combine(_root, "store"), new GzipBlobCodec()),
            new VersionLog(Path.Combine(_root, "store", "versions.jsonl")));
        using var engine = new WatchEngine(store, new PathExclusions(), [watch]);

        Assert.True(engine.TryCapture(file), "a deep-path file should still be captured");
        string rel = engine.RelativeToRoot(file)!;
        IReadOnlyList<FileVersion> history = store.Log.History(rel);
        Assert.Single(history);

        using var ms = new MemoryStream();
        store.WriteContent(history[0], ms);
        Assert.Equal("recoverable even when deeply nested", System.Text.Encoding.UTF8.GetString(ms.ToArray()));
    }

    public void Dispose()
    {
        try { Directory.Delete(LongPath.Of(_root), true); } catch { }
    }
}
