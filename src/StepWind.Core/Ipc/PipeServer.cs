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
    private readonly Func<IpcRequest, IpcResponse> _handler;
    private readonly Action<string>? _log;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public PipeServer(Func<IpcRequest, IpcResponse> handler, Action<string>? log = null)
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
                : _handler(request);
        }
        catch (Exception ex)
        {
            response = IpcResponse.Fail(ex.Message);
        }

        await writer.WriteLineAsync(JsonSerializer.Serialize(response));
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
