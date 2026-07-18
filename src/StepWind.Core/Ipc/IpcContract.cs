namespace StepWind.Core.Ipc;

/// <summary>
/// Named-pipe protocol between the elevated service (server) and the unelevated GUI (client).
/// One JSON request line → one JSON response line. Kept deliberately small and versioned so
/// the two halves can be updated independently. The pipe is ACL'd to the interactive user.
/// </summary>
public static class IpcProtocol
{
    public const string PipeName = "StepWind.Service";
    public const int Version = 1;
}

public enum IpcCommand
{
    Ping,
    GetStatus,
    GetTimeline,     // recent operations (flight recorder)
    GetHistory,      // versions of one file
    ReverseOperation,
    RestoreVersion,
    GetSettings,
    SetSettings,
    RunRetention,
}

public sealed record IpcRequest
{
    public int Version { get; init; } = IpcProtocol.Version;
    public IpcCommand Command { get; init; }

    /// <summary>Command-specific string argument (path, op id, etc.).</summary>
    public string? Arg1 { get; init; }
    public string? Arg2 { get; init; }
    public int Limit { get; init; } = 200;
}

public sealed record IpcResponse
{
    public bool Ok { get; init; }
    public string? Error { get; init; }

    /// <summary>JSON payload (shape depends on the command).</summary>
    public string? Json { get; init; }

    public static IpcResponse Fail(string error) => new() { Ok = false, Error = error };
    public static IpcResponse Success(string? json = null) => new() { Ok = true, Json = json };
}

/// <summary>A flight-recorder operation as sent to the GUI (flattened, path-friendly).</summary>
public sealed record TimelineEntry
{
    public required string Kind { get; init; }
    public required DateTime TimestampUtc { get; init; }
    public required string Name { get; init; }
    public string? OldPath { get; init; }
    public string? NewPath { get; init; }
    public string? ByProcess { get; init; }
    public bool Reversible { get; init; }

    /// <summary>Opaque id the GUI passes back to ReverseOperation.</summary>
    public required string OperationId { get; init; }
}

/// <summary>A stored version as sent to the GUI.</summary>
public sealed record VersionEntry
{
    public required string RelativePath { get; init; }
    public required DateTime CapturedUtc { get; init; }
    public required long Size { get; init; }
    public required string Reason { get; init; }

    /// <summary>Opaque id the GUI passes back to RestoreVersion.</summary>
    public required string VersionId { get; init; }
}
