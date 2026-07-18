using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace StepWind.Core.Ipc;

/// <summary>
/// Client the unelevated GUI uses to talk to the service. Each call opens a short-lived
/// connection, sends one request, reads one response. Never throws for a down service —
/// returns a failed <see cref="IpcResponse"/> so the UI can show "service not running".
/// </summary>
public sealed class PipeClient
{
    public async Task<IpcResponse> SendAsync(IpcRequest request, int timeoutMs = 5000, CancellationToken ct = default)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", IpcProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(timeoutMs, ct);

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
}
