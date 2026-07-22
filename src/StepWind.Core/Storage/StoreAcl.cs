using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace StepWind.Core.Storage;

/// <summary>
/// Locks the version store down to SYSTEM + Administrators. The store lives under
/// %ProgramData%, whose default ACL lets any local user read files — but the store holds
/// copies of the user's documents, so standard users must not be able to read it directly.
/// (The GUI never touches the store; it goes through the service's pipe.)
///
/// Applied only when running as SYSTEM (the real service): a dev/console run as a normal
/// user would otherwise lock itself out of its own test store.
/// </summary>
[SupportedOSPlatform("windows")]
public static class StoreAcl
{
    /// <summary>Returns true if the ACL was applied (or intentionally skipped as non-SYSTEM).</summary>
    public static bool Harden(string storeRoot, Action<string>? log = null)
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            if (!identity.IsSystem)
            {
                return true; // dev/console run — leave the directory usable by its owner
            }

            var dir = new DirectoryInfo(storeRoot);
            dir.Create();

            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));

            dir.SetAccessControl(security);
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke("store ACL hardening failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Locks a directory so only SYSTEM + Administrators can WRITE, but every user can read and
    /// EXECUTE. Used for the update-staging dir: the SYSTEM service (the only thing that has
    /// verified the download) places the installer there, and the unelevated GUI can launch it —
    /// but a standard user can't plant a malicious file for the GUI to run. No-op unless SYSTEM.
    /// </summary>
    public static bool HardenReadExecute(string dir, Action<string>? log = null)
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            if (!identity.IsSystem)
            {
                return true; // dev/console run — leave it usable by its owner
            }

            var info = new DirectoryInfo(dir);
            info.Create();

            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            // Everyone else: read + execute only (can launch the staged installer, can't replace it).
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));

            info.SetAccessControl(security);
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke("update-dir ACL hardening failed: " + ex.Message);
            return false;
        }
    }
}
