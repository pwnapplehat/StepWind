using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace StepWind.Core.Ipc;

/// <summary>
/// Named-pipe server hosted by the elevated service. One line-delimited JSON request per
/// connection, one JSON response. The pipe is ACL'd so the elevated service and interactive
/// users can talk, but it never runs arbitrary code — every request is a typed command the
/// handler validates. Accepts connections until disposed.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PipeServer : IDisposable
{
    private readonly Func<IpcRequest, CallerContext, IpcResponse> _handler;
    private readonly Action<string>? _log;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public PipeServer(Func<IpcRequest, CallerContext, IpcResponse> handler, Action<string>? log = null)
    {
        _handler = handler;
        _log = log;
    }

    public void Start() => _loop = Task.Run(() => AcceptLoop(_cts.Token));

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using NamedPipeServerStream server = CreatePipe();
                await server.WaitForConnectionAsync(ct);
                await HandleConnection(server, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _log?.Invoke("pipe accept error: " + ex.Message);
                await Task.Delay(500, ct).ContinueWith(_ => { }, CancellationToken.None);
            }
        }
    }

    private async Task HandleConnection(NamedPipeServerStream server, CancellationToken ct)
    {
        using var reader = new StreamReader(server, Encoding.UTF8, false, 1 << 16, leaveOpen: true);
        using var writer = new StreamWriter(server, new UTF8Encoding(false), 1 << 16, leaveOpen: true) { AutoFlush = true };

        string? line = await reader.ReadLineAsync(ct);
        if (line is null)
        {
            return;
        }

        IpcResponse response;
        try
        {
            IpcRequest? request = JsonSerializer.Deserialize<IpcRequest>(line);
            response = request is null ? IpcResponse.Fail("bad request")
                : request.Version != IpcProtocol.Version ? IpcResponse.Fail("version mismatch")
                : _handler(request, ResolveCaller(server));
        }
        catch (Exception ex)
        {
            response = IpcResponse.Fail(ex.Message);
        }

        await writer.WriteLineAsync(JsonSerializer.Serialize(response));
    }

    /// <summary>
    /// Identifies the connected client by impersonating the pipe. The elevated service acts on
    /// this identity's behalf, so authorization (who may read/reverse/purge which data) is
    /// decided from it — not from anything the client puts in the request. If identity can't be
    /// resolved, the caller is treated as an UNPRIVILEGED unknown (fail-closed for privilege).
    /// </summary>
    private CallerContext ResolveCaller(NamedPipeServerStream server)
    {
        try
        {
            string? sid = null, name = null;
            bool isAdmin = false;
            server.RunAsClient(() =>
            {
                using WindowsIdentity id = WindowsIdentity.GetCurrent();
                sid = id.User?.Value;
                name = id.Name;
                isAdmin = id.IsSystem || new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            });

            return new CallerContext
            {
                UserSid = sid,
                UserName = name,
                IsAdministrator = isAdmin,
                PipeStream = server,
            };
        }
        catch (Exception ex)
        {
            _log?.Invoke("caller identity resolve failed (treating as unprivileged): " + ex.Message);
            return new CallerContext { UserName = "(unknown)" };
        }
    }

    private static NamedPipeServerStream CreatePipe()
    {
        // Allow the local interactive users to connect (the unelevated GUI); the server itself
        // runs as SYSTEM. Deny network access implicitly (named pipes are local by default here).
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            IpcProtocol.PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(2000); } catch { }
        _cts.Dispose();
    }
}
