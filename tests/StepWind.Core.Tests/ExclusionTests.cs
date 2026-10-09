using System.IO;
using StepWind.Core.Engine;
using Xunit;

namespace StepWind.Core.Tests;

public class ExclusionTests
{
    private readonly PathExclusions _ex = new();

    [Theory]
    [InlineData(@"C:\proj\node_modules\react\index.js")]
    [InlineData(@"C:\proj\target\debug\app.exe")]
    [InlineData(@"C:\proj\.venv\lib\thing.py")]
    [InlineData(@"C:\proj\bin\Debug\x.dll")]
    [InlineData(@"C:\proj\__pycache__\m.cpython-312.pyc")]
    [InlineData(@"C:\Users\me\.stepwind\blobs\ab\cd")]
    public void Excludes_regenerable_and_own_store_paths(string path)
        => Assert.False(_ex.ShouldVersion(path, FileAttributes.Normal, 1000));

    [Theory]
    [InlineData(@"C:\work\report.docx")]
    [InlineData(@"C:\work\src\main.cs")]
    [InlineData(@"C:\Users\me\Documents\thesis.tex")]
    public void Versions_real_user_documents(string path)
        => Assert.True(_ex.ShouldVersion(path, FileAttributes.Normal, 50_000));

    [Fact]
    public void Never_versions_a_cloud_online_only_placeholder()
    {
        // Would otherwise force OneDrive to download the whole file just to copy it.
        const FileAttributes recall = (FileAttributes)0x00400000;
        Assert.False(_ex.ShouldVersion(@"C:\Users\me\OneDrive\big.psd", FileAttributes.Normal | recall, 1_000_000));
        Assert.True(PathExclusions.IsCloudPlaceholder(FileAttributes.Offline));
    }

    [Fact]
    public void An_excluded_prefix_covers_files_inside_it_and_not_a_sibling()
    {
        string[] prefixes = [@"D:\Archive\Old"];
        Assert.True(PathExclusions.IsUnderAnyPrefix(@"D:\Archive\Old\note.txt", prefixes));
        Assert.True(PathExclusions.IsUnderAnyPrefix(@"D:\Archive\Old", prefixes));
        Assert.False(PathExclusions.IsUnderAnyPrefix(@"D:\Archive\Older\note.txt", prefixes));
        Assert.False(PathExclusions.IsUnderAnyPrefix(@"C:\Users\me\note.txt", prefixes));
    }

    [Fact]
    public void Volume_ignore_matches_drive_ids()
    {
        var settings = new StepWindSettings { IgnoredVolumes = ["e:\\"] };
        Assert.True(settings.IsVolumeIgnored("E:"));
        Assert.True(settings.IsVolumeIgnored(@"e:\"));
        Assert.False(settings.IsVolumeIgnored("C:"));
        Assert.Equal("E:", StepWindSettings.NormalizeVolumeId("e"));
    }

    [Fact]
    public void Skips_reparse_points()
        => Assert.False(_ex.ShouldVersion(@"C:\link\target.txt", FileAttributes.Normal | FileAttributes.ReparsePoint, 100));

    [Fact]
    public void Skips_files_over_the_size_ceiling()
    {
        _ex.MaxFileBytes = 100 * 1024 * 1024;
        Assert.False(_ex.ShouldVersion(@"C:\vm\disk.vhdx", FileAttributes.Normal, 8L * 1024 * 1024 * 1024));
        Assert.True(_ex.ShouldVersion(@"C:\docs\a.pdf", FileAttributes.Normal, 5 * 1024 * 1024));
    }

    [Fact]
    public void Honors_extra_excluded_prefixes()
    {
        _ex.ExcludePrefix(@"C:\Games");
        Assert.False(_ex.ShouldVersion(@"C:\Games\save.dat", FileAttributes.Normal, 100));
        Assert.True(_ex.ShouldVersion(@"C:\GamesJournal\notes.txt", FileAttributes.Normal, 100)); // prefix must be a real segment boundary
    }

    [Theory]
    [InlineData(@"C:\a\file.tmp")]
    [InlineData(@"C:\a\build.log")]
    [InlineData(@"C:\a\doc.bak")]
    [InlineData(@"C:\a\download.crdownload")]
    public void Excludes_transient_extensions(string path)
        => Assert.False(_ex.ShouldVersion(path, FileAttributes.Normal, 100));
}
