using ModelContextProtocol;
using StepWind.Core.Ipc;

namespace StepWind.Mcp;

/// <summary>
/// Thin bridge between MCP tool methods and the StepWind service's named pipe. Registered as a
/// singleton and resolved into tool methods via dependency injection (the C# MCP SDK special-
/// cases any DI-registered service as an automatically-bound tool parameter — see the "Special
/// Parameter Types" section of the SDK's tools documentation).
///
/// Turns a failed <see cref="IpcResponse"/> into an <see cref="McpException"/> (the base type,
/// not <see cref="McpProtocolException"/>) so the calling AI model sees the tool call fail with
/// the EXACT reason as ordinary result text — e.g. "no saved history for 'X'", or "StepWind
/// service is not running." — rather than a generic message or an unhandled crash. A plain
/// <see cref="McpException"/> becomes a normal <c>IsError</c> tool result the model can read and
/// react to; only <see cref="McpProtocolException"/> escalates to a protocol-level error.
/// </summary>
public sealed class StepWindGateway
{
    private readonly PipeClient _pipe = new();

    public async Task<string> CallAsync(
        IpcCommand command, string? arg1 = null, string? arg2 = null, int limit = 200, CancellationToken ct = default)
    {
        IpcResponse resp = await _pipe.SendAsync(
            new IpcRequest { Command = command, Arg1 = arg1, Arg2 = arg2, Limit = limit }, ct: ct);
        if (!resp.Ok)
        {
            throw new McpException(resp.Error ?? $"StepWind command '{command}' failed with no further detail.");
        }

        return resp.Json ?? "null";
    }
}
