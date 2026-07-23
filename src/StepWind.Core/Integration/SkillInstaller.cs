namespace StepWind.Core.Integration;

/// <summary>Outcome of a skill install/remove, with an honest human-readable message.</summary>
public sealed record SkillInstallResult(bool Ok, string Message);

/// <summary>
/// Installs StepWind's agent skill (SKILL.md) into AI tools that support file-based Agent Skills.
/// The MCP server gives an agent the TOOLS; the skill teaches it the HABITS — checkpoint before a
/// risky edit, diff after, restore on regret — so models actually reach for the safety net at the
/// right moments instead of only when a user asks.
///
/// Everything lands in the tool's per-user skills directory under our own "stepwind" folder, so
/// install/remove only ever touches files WE own — no user data is read, merged, or backed up
/// (unlike MCP config files, which mix our entry into theirs). Writes are atomic (temp + rename)
/// and idempotent: reinstalling refreshes the skill to the shipped version.
/// </summary>
public static class SkillInstaller
{
    /// <summary>Folder name for our skill inside each tool's skills directory.</summary>
    public const string SkillFolderName = "stepwind";

    /// <summary>The skill file shipped beside the app (see StepWind.App/skills/stepwind/SKILL.md).</summary>
    public static string ShippedSkillPath => Path.Combine(AppContext.BaseDirectory, "skills", "stepwind", "SKILL.md");

    /// <summary>
    /// The per-user skills root for a given MCP client id, or null if that tool has no file-based
    /// skills convention we can verify (those tools still get the MCP server; users of other
    /// skill-capable tools can copy the skill text from the app's manual-setup card).
    /// </summary>
    public static string? SkillsRootFor(string clientId)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return clientId switch
        {
            "cursor" => Path.Combine(home, ".cursor", "skills"),
            "claude-code" => Path.Combine(home, ".claude", "skills"),
            _ => null,
        };
    }

    public static bool SupportsSkill(string clientId) => SkillsRootFor(clientId) is not null;

    public static bool IsInstalled(string clientId)
        => SkillsRootFor(clientId) is { } root && IsInstalledAt(root);

    /// <summary>Installs (or refreshes) the skill for one client. No-op result if unsupported.</summary>
    public static SkillInstallResult Install(string clientId, string displayName)
    {
        if (SkillsRootFor(clientId) is not { } root)
        {
            return new SkillInstallResult(true, "");
        }

        return InstallAt(ShippedSkillPath, root, displayName);
    }

    /// <summary>Removes the skill for one client (friendly no-op when absent or unsupported).</summary>
    public static SkillInstallResult Remove(string clientId)
    {
        return SkillsRootFor(clientId) is { } root
            ? RemoveAt(root)
            : new SkillInstallResult(true, "");
    }

    // ─── Testable cores (explicit paths, no environment lookups) ───

    internal static bool IsInstalledAt(string skillsRoot)
        => File.Exists(Path.Combine(skillsRoot, SkillFolderName, "SKILL.md"));

    internal static SkillInstallResult InstallAt(string sourceFile, string skillsRoot, string displayName)
    {
        try
        {
            if (!File.Exists(sourceFile))
            {
                return new SkillInstallResult(false,
                    $"The StepWind skill file was not found at '{sourceFile}'. Repair the StepWind installation first.");
            }

            string dir = Path.Combine(skillsRoot, SkillFolderName);
            Directory.CreateDirectory(dir);
            string dest = Path.Combine(dir, "SKILL.md");

            // Atomic write: a crash mid-copy can't leave a truncated skill for the agent to read.
            string tmp = dest + ".stepwind-tmp";
            File.Copy(sourceFile, tmp, overwrite: true);
            File.Move(tmp, dest, overwrite: true);

            return File.Exists(dest)
                ? new SkillInstallResult(true, $"Agent skill installed for {displayName}.")
                : new SkillInstallResult(false, $"The skill file did not appear at '{dest}' after writing.");
        }
        catch (Exception ex)
        {
            return new SkillInstallResult(false, $"Could not install the agent skill: {ex.Message}");
        }
    }

    internal static SkillInstallResult RemoveAt(string skillsRoot)
    {
        try
        {
            string dir = Path.Combine(skillsRoot, SkillFolderName);
            string dest = Path.Combine(dir, "SKILL.md");
            if (!File.Exists(dest))
            {
                return new SkillInstallResult(true, "");
            }

            File.Delete(dest);

            // Tidy up our folder if we were the only thing in it — never touch anything else.
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir);
            }

            return new SkillInstallResult(true, "Agent skill removed.");
        }
        catch (Exception ex)
        {
            return new SkillInstallResult(false, $"Could not remove the agent skill: {ex.Message}");
        }
    }
}
