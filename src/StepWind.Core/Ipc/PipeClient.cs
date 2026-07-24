using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace StepWind.Core.Ipc;

/// <summary>
/// Client the unelevated GUI uses to talk to the service. Each call opens a short-lived
/// connection, sends one request, reads one response. Never throws for a down service —
/// returns a failed <see cref="IpcResponse"/> so the UI can show "service not running".
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PipeClient
{
    public async Task<IpcResponse> SendAsync(IpcRequest request, int timeoutMs = 5000, CancellationToken ct = default)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", IpcProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(timeoutMs, ct);

            // Anti-spoof: the real server runs as LocalSystem. A local attacker could create a
            // pipe instance of the same name first and impersonate the service to harvest the
            // paths/selectors we send or feed back a fake "you're fully protected" view. Refuse
            // to talk to any server whose pipe isn't owned by a privileged identity.
            if (!ServerIsPrivileged(client))
            {
                return IpcResponse.Fail("StepWind service identity could not be verified (refusing to use an untrusted pipe).");
            }

            using var writer = new StreamWriter(client, new UTF8Encoding(false), 1 << 16, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(client, Encoding.UTF8, false, 1 << 16, leaveOpen: true);

            await writer.WriteLineAsync(JsonSerializer.Serialize(request));
            string? line = await reader.ReadLineAsync(ct);
            return line is null ? IpcResponse.Fail("no response") : JsonSerializer.Deserialize<IpcResponse>(line) ?? IpcResponse.Fail("bad response");
        }
        catch (TimeoutException)
        {
            return IpcResponse.Fail("StepWind service is not running.");
        }
        catch (Exception ex)
        {
            return IpcResponse.Fail(ex.Message);
        }
    }

    /// <summary>
    /// True iff the connected pipe is owned by a PRIVILEGED identity — LocalSystem or the
    /// Administrators group. A kernel object created by the SYSTEM service is owned by
    /// BUILTIN\Administrators by default (verified on real hardware: the pipe owner is
    /// S-1-5-32-544), so both are accepted. What this rejects is the real threat: a standard-user
    /// process squatting the pipe name — its pipe is owned by that user's own SID, which a
    /// non-privileged account cannot change to SYSTEM or Administrators.
    /// </summary>
    private static bool ServerIsPrivileged(NamedPipeClientStream client)
    {
        try
        {
            PipeSecurity acl = client.GetAccessControl();
            return acl.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier sid
                && (sid.IsWellKnown(WellKnownSidType.LocalSystemSid)
                    || sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid));
        }
        catch (Exception)
        {
            // Can't read the owner (older OS quirk / permission) — fail closed: don't trust it.
            return false;
        }
    }
}
