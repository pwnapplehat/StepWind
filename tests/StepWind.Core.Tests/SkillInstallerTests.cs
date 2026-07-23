using StepWind.Core.Integration;
using Xunit;

namespace StepWind.Core.Tests;

/// <summary>
/// The agent skill installer drops StepWind's SKILL.md into an AI tool's skills folder (and only
/// ever touches our own "stepwind" subfolder). These pin the contract: install is idempotent and
/// atomic, remove is a friendly no-op when absent and never deletes anything that isn't ours, and
/// a missing shipped skill fails with a readable message instead of a stack trace.
/// </summary>
public class SkillInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sw-skill", Guid.NewGuid().ToString("N"));
    private readonly string _source;
    private readonly string _skillsRoot;

    public SkillInstallerTests()
    {
        Directory.CreateDirectory(_root);
        _source = Path.Combine(_root, "SKILL.md");
        _skillsRoot = Path.Combine(_root, "tool-skills");
        File.WriteAllText(_source, "---\nname: stepwind-safety-net\n---\n# StepWind safety net\n");
    }

    [Fact]
    public void Install_creates_the_skill_and_reports_installed()
    {
        SkillInstallResult r = SkillInstaller.InstallAt(_source, _skillsRoot, "Cursor");

        Assert.True(r.Ok, r.Message);
        Assert.Contains("Cursor", r.Message);
        Assert.True(SkillInstaller.IsInstalledAt(_skillsRoot));
        Assert.Equal(File.ReadAllText(_source),
            File.ReadAllText(Path.Combine(_skillsRoot, "stepwind", "SKILL.md")));
    }

    [Fact]
    public void Install_twice_refreshes_to_the_shipped_content()
    {
        Assert.True(SkillInstaller.InstallAt(_source, _skillsRoot, "Cursor").Ok);

        // The user (or an old version) modified the installed copy; reinstall refreshes it.
        File.WriteAllText(Path.Combine(_skillsRoot, "stepwind", "SKILL.md"), "stale edits");
        File.WriteAllText(_source, "---\nname: stepwind-safety-net\n---\n# v2\n");

        Assert.True(SkillInstaller.InstallAt(_source, _skillsRoot, "Cursor").Ok);
        Assert.Contains("# v2", File.ReadAllText(Path.Combine(_skillsRoot, "stepwind", "SKILL.md")));
    }

    [Fact]
    public void Missing_shipped_skill_fails_with_a_readable_message()
    {
        SkillInstallResult r = SkillInstaller.InstallAt(
            Path.Combine(_root, "does-not-exist.md"), _skillsRoot, "Cursor");

        Assert.False(r.Ok);
        Assert.Contains("not found", r.Message);
        Assert.False(SkillInstaller.IsInstalledAt(_skillsRoot));
    }

    [Fact]
    public void Remove_deletes_only_our_folder_and_noops_when_absent()
    {
        // Removing before anything was installed: friendly success, nothing created.
        Assert.True(SkillInstaller.RemoveAt(_skillsRoot).Ok);

        // A NEIGHBOR skill (the user's own) must survive our remove untouched.
        string neighbor = Path.Combine(_skillsRoot, "their-skill", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(neighbor)!);
        File.WriteAllText(neighbor, "user's own skill");

        Assert.True(SkillInstaller.InstallAt(_source, _skillsRoot, "Cursor").Ok);
        SkillInstallResult r = SkillInstaller.RemoveAt(_skillsRoot);

        Assert.True(r.Ok, r.Message);
        Assert.False(SkillInstaller.IsInstalledAt(_skillsRoot));
        Assert.False(Directory.Exists(Path.Combine(_skillsRoot, "stepwind"))); // our empty dir tidied
        Assert.Equal("user's own skill", File.ReadAllText(neighbor));          // theirs untouched
    }

    [Fact]
    public void Remove_keeps_our_folder_if_the_user_put_extra_files_in_it()
    {
        Assert.True(SkillInstaller.InstallAt(_source, _skillsRoot, "Cursor").Ok);
        string extra = Path.Combine(_skillsRoot, "stepwind", "notes.txt");
        File.WriteAllText(extra, "user notes");

        Assert.True(SkillInstaller.RemoveAt(_skillsRoot).Ok);

        Assert.False(SkillInstaller.IsInstalledAt(_skillsRoot)); // SKILL.md gone
        Assert.Equal("user notes", File.ReadAllText(extra));     // their file preserved
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
