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
/// Each protected root has a STABLE store namespace segment (see StepWindSettings.RootIds):
/// the folder leaf for the common case, or a deterministic "leaf~hash" when two roots share a
/// name. A namespace therefore maps to a single owner set with no ambiguity — even when two
/// protected folders are both called "Documents".
/// </summary>
[SupportedOSPlatform("windows")]
public static class RootAccess
{
    /// <summary>
    /// Can this caller read/restore/purge the history for a root? <paramref name="ownersForRoot"/>
    /// is a SNAPSHOT of that root's owner SIDs (the caller resolves it under a lock and passes a
    /// copy, so this stays safe under the concurrent pipe server).
    /// </summary>
    public static bool CanAccess(CallerContext caller, IReadOnlyList<string>? ownersForRoot, string? currentRootPath)
    {
        if (caller.IsPrivileged)
        {
            return true;
        }

        if (caller.UserSid is { Length: > 0 } sid
            && ownersForRoot is not null
            && ownersForRoot.Contains(sid, StringComparer.OrdinalIgnoreCase))
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
