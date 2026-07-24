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
    // Cap on connections handled at once. Enough that the GUI and several MCP/CLI clients never
    // block each other (the old serial loop meant one slow diff stalled everyone), but bounded so
    // a flood of connections can't spawn unlimited handler tasks.
    private const int MaxConcurrent = 8;

    // A connected client must send its one request line within this window, or it's dropped and
    // its slot freed. Without this, a client that connects and never writes pins a slot forever;
    // eight such clients silently take the whole service down (the GUI then reads "not running").
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);

    // Hard ceiling on a single request line. IPC requests are tiny (a selector, a path, or a
    // batch of operation ids); this only exists so a client streaming endless bytes with no
    // newline can't drive unbounded allocation inside the SYSTEM process. No real request is close.
    private const int MaxRequestChars = 16 * 1024 * 1024;

    private readonly Func<IpcRequest, CallerContext, IpcResponse> _handler;
    private readonly Action<string>? _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _slots = new(MaxConcurrent, MaxConcurrent);
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
            NamedPipeServerStream? server = null;
            try
            {
                // Wait for a free handler slot BEFORE creating the next pipe instance, so at most
                // MaxConcurrent instances exist at once.
                await _slots.WaitAsync(ct);
                server = CreatePipe();
                await server.WaitForConnectionAsync(ct);
            }
            catch (OperationCanceledException)
            {
                server?.Dispose();
                _slots.Release();
                return;
            }
            catch (Exception ex)
            {
                _log?.Invoke("pipe accept error: " + ex.Message);
                server?.Dispose();
                _slots.Release();
                try { await Task.Delay(500, ct); } catch { return; }
                continue;
            }

            // Handle this connection concurrently so a slow request (e.g. a big diff) never blocks
            // the next client. The handler owns disposing the stream and releasing its slot.
            NamedPipeServerStream accepted = server;
            _ = Task.Run(async () =>
            {
                try
                {
                    await HandleConnection(accepted, ct);
                }
                catch (Exception ex)
                {
                    _log?.Invoke("pipe handle error: " + ex.Message);
                }
                finally
                {
                    accepted.Dispose();
                    try { _slots.Release(); } catch (ObjectDisposedException) { /* server shutting down */ }
                }
            }, CancellationToken.None);
        }
    }

    private async Task HandleConnection(NamedPipeServerStream server, CancellationToken ct)
    {
        using var reader = new StreamReader(server, Encoding.UTF8, false, 1 << 16, leaveOpen: true);
        using var writer = new StreamWriter(server, new UTF8Encoding(false), 1 << 16, leaveOpen: true) { AutoFlush = true };

        // Bound the read: a client has ReadTimeout to deliver its request line, and the line can't
        // exceed MaxRequestChars. Either violation drops the connection (and frees the slot in the
        // caller's finally). This is what stops a stalled or abusive local client from pinning one
        // of the few handler slots and taking the service offline.
        string? line;
        try
        {
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readCts.CancelAfter(ReadTimeout);
            line = await ReadBoundedLineAsync(reader, readCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log?.Invoke("pipe client dropped: no request within the read timeout");
            return;
        }
        catch (InvalidDataException ex)
        {
            _log?.Invoke("pipe client dropped: " + ex.Message);
            return;
        }

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
    /// Reads one newline-terminated line, but refuses to buffer more than <see cref="MaxRequestChars"/>
    /// (throws <see cref="InvalidDataException"/>) so an endless line can't exhaust memory. Reads in
    /// blocks rather than char-by-char for throughput.
    /// </summary>
    private static async Task<string?> ReadBoundedLineAsync(StreamReader reader, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buf = new char[8192];
        while (true)
        {
            int n = await reader.ReadAsync(buf.AsMemory(), ct);
            if (n == 0)
            {
                return sb.Length == 0 ? null : sb.ToString(); // EOF (no trailing newline)
            }

            for (int i = 0; i < n; i++)
            {
                char c = buf[i];
                if (c == '\n')
                {
                    // Trim a single trailing CR to match ReadLine semantics; ignore anything the
                    // client sent after the newline (one request per connection).
                    if (sb.Length > 0 && sb[^1] == '\r')
                    {
                        sb.Length--;
                    }

                    return sb.ToString();
                }

                sb.Append(c);
                if (sb.Length > MaxRequestChars)
                {
                    throw new InvalidDataException("request exceeded the maximum size");
                }
            }
        }
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
        // Allow local authenticated users to connect (the unelevated GUI/CLI/MCP); the server
        // itself runs as SYSTEM. A named pipe is reachable over SMB (\\host\pipe\...), so we
        // explicitly DENY the NETWORK group: a remote logon token carries the NETWORK SID, a
        // local interactive/batch/service logon does not — so this rejects remote clients while
        // leaving every local client unaffected. Deny ACEs are evaluated before allows.
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));
        // ReadPermissions lets a client read this pipe's OWNER after connecting, which is how
        // PipeClient confirms it's really talking to the SYSTEM service and not a squatter.
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.ReadPermissions, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));

        // NB: NOT PipeOptions.CurrentUserOnly — that would reject the unelevated GUI/CLI/MCP
        // connecting to this SYSTEM-owned pipe, which is the whole point of the design. Remote
        // rejection is the NETWORK deny ACE above; cross-user LOCAL access is intended and
        // authorized per-command by impersonation.
        return NamedPipeServerStreamAcl.Create(
            IpcProtocol.PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(2000); } catch { }
        _cts.Dispose();
        _slots.Dispose();
    }
}
