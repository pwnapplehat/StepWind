using System.Runtime.Versioning;
using System.Security.Principal;
using StepWind.Core.Ipc;

namespace StepWind.Core.Engine;

/// <summary>
/// Decides whether an IPC caller may see or act on a protected root's history. The elevated
/// service can read every user's files, so without this any local user could read, restore, or
/// purge another user's version history over the shared pipe. Authorization is, in order:
///
///   1. privileged callers (admin / SYSTEM / in-process trusted) — always allowed;
///   2. the caller's SID is recorded as an owner of the root — allowed (fast path);
///   3. the caller's token can ACTUALLY read the folder on disk right now — allowed. This live
///      check is the safety net: you may see StepWind's history for any folder you can already
///      open yourself (never an escalation), so a stale/missing owner record can't lock you out;
///   4. otherwise denied.
///
/// The folder-leaf ("Documents") is the store namespace segment. Adding a second protected
/// folder that shares a leaf with an existing one is refused elsewhere, so a leaf maps to a
/// single owner set with no ambiguity.
/// </summary>
[SupportedOSPlatform("windows")]
public static class RootAccess
{
    /// <summary>Can this caller read/restore/purge the history under <paramref name="firstSegment"/>?</summary>
    public static bool CanAccess(CallerContext caller, string firstSegment,
        IReadOnlyDictionary<string, List<string>> owners, string? currentRootPath)
    {
        if (caller.IsPrivileged)
        {
            return true;
        }

        if (caller.UserSid is { Length: > 0 } sid
            && owners.TryGetValue(firstSegment, out List<string>? list)
            && list.Contains(sid, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        // Safety net: if the caller can open the live folder itself, its history isn't secret
        // from them. Also covers grandfathered roots with no recorded owner.
        return currentRootPath is { Length: > 0 } && CallerCanReadDirectory(caller, currentRootPath);
    }

    /// <summary>
    /// True if the caller's own token can enumerate <paramref name="directory"/>. Runs the check
    /// UNDER the caller's identity (impersonation) so it reflects the caller's real rights, not
    /// the SYSTEM service's. In-process trusted callers (tests/CLI) short-circuit to true.
    /// </summary>
    public static bool CallerCanReadDirectory(CallerContext caller, string directory)
    {
        if (caller.FullTrust)
        {
            return true;
        }

        if (caller.PipeStream is null)
        {
            // A resolved-but-unprivileged caller with no impersonation handle can't prove access.
            return false;
        }

        try
        {
            bool ok = false;
            caller.PipeStream.RunAsClient(() =>
            {
                try
                {
                    _ = Directory.EnumerateFileSystemEntries(directory).Take(1).ToList();
                    ok = true;
                }
                catch
                {
                    ok = false;
                }
            });
            return ok;
        }
        catch
        {
            return false;
        }
    }
}
