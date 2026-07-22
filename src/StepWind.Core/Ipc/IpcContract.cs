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

/// <summary>
/// Wire protocol command ids. These cross a process boundary (GUI ↔ service, possibly from
/// DIFFERENT builds mid-update), so values are explicit and append-only: never renumber,
/// never insert in the middle — a remapped id makes an old service silently execute the
/// wrong command.
/// </summary>
public enum IpcCommand
{
    Ping = 0,
    GetStatus = 1,
    GetTimeline = 2,      // recent operations (flight recorder)
    GetHistory = 3,       // versions of one file
    ReverseOperation = 4,
    RestoreVersion = 5,
    GetSettings = 6,
    SetSettings = 7,
    RunRetention = 8,
    GetRecentFiles = 9,   // distinct files with saved history, most-recently-changed first
    PurgeHistory = 10,    // delete stored versions now: "*" | "unprotected" | folder-or-file prefix
    BrowseVersions = 11,  // folder-tree browse (Arg1 = prefix) or recursive search (Arg2 = query)

    // ── AI/MCP surface: read-only + additive only. No command below this line may delete
    // history or change settings — an AI agent gets the time machine, not the shredder. ──
    GetVersionContent = 12, // read one version's (or the live file's) text — Arg1 = selector
    DiffVersions = 13,       // unified diff between two selectors — Arg1 = old, Arg2 = new
    CaptureNow = 14,         // force an immediate checkpoint of one file — Arg1 = path/selector
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

    /// <summary>
    /// For a Delete of a file that lived in a protected folder and has stored version history:
    /// the VersionId of its most recent saved version, which the GUI can restore in one click
    /// (moves/renames undo via <see cref="OperationId"/>; deletes recover from the version store).
    /// Null when the deleted file has no recoverable version — the GUI then says so honestly.
    /// </summary>
    public string? RecoverableVersionId { get; init; }
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

/// <summary>A protected file with saved history, for the GUI's recent-files quick list.</summary>
public sealed record RecentFileEntry
{
    public required string RelativePath { get; init; }
    public required DateTime LastCapturedUtc { get; init; }
    public required int VersionCount { get; init; }
}

/// <summary>
/// One entry in the folder-navigable version browser: either a subfolder (drill in) or a file
/// (show its history). Version/latest aggregate over everything beneath a folder.
/// </summary>
public sealed record BrowseEntry
{
    public required string Name { get; init; }
    public required string RelativePath { get; init; }
    public required bool IsFolder { get; init; }
    public required int VersionCount { get; init; }
    public required DateTime LastCapturedUtc { get; init; }

    /// <summary>For folders: how many distinct files with history live beneath it.</summary>
    public int FileCount { get; init; }
}

/// <summary>
/// The text content of one version — or the live on-disk file — for the AI/MCP read and diff
/// tools. <see cref="Content"/> is null when the file is binary or larger than the size cap;
/// <see cref="Size"/> and <see cref="IsBinary"/>/<see cref="Truncated"/> are still reported so
/// a caller always learns something honest even when the bytes aren't included.
/// </summary>
public sealed record ContentResult
{
    public required string RelativePath { get; init; }

    /// <summary>Human-readable provenance, e.g. "Docs/x.txt @ 2026-07-21 09:14 UTC (checkpoint)".</summary>
    public required string Label { get; init; }
    public required DateTime WhenUtc { get; init; }
    public required long Size { get; init; }
    public required bool IsBinary { get; init; }
    public required bool Truncated { get; init; }
    public string? Content { get; init; }
}

/// <summary>Unified-diff result between two <see cref="ContentResult"/>-style selectors.</summary>
public sealed record DiffResult
{
    public required string OldLabel { get; init; }
    public required string NewLabel { get; init; }
    public required long OldSize { get; init; }
    public required long NewSize { get; init; }
    public required bool Binary { get; init; }

    /// <summary>The unified diff text, or an explanation when a text diff isn't possible.</summary>
    public required string Diff { get; init; }
}
