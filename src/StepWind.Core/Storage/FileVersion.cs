namespace StepWind.Core.Storage;

/// <summary>
/// One captured version of one file: the ordered chunk ids that reconstruct its content,
/// plus the metadata needed to restore it faithfully and show it on a timeline. Immutable;
/// serialized as a single line in the append-only version log.
/// </summary>
public sealed record FileVersion
{
    /// <summary>Path relative to the watched root, using '/' separators (portable/stable).</summary>
    public required string RelativePath { get; init; }

    /// <summary>When StepWind captured this version.</summary>
    public required DateTime CapturedUtc { get; init; }

    /// <summary>The file's own last-write time when captured (restored onto recovered files).</summary>
    public DateTime ModifiedUtc { get; init; }

    /// <summary>Total plaintext size in bytes (sum of chunk lengths).</summary>
    public required long Size { get; init; }

    /// <summary>Ordered chunk content-ids; concatenating their plaintext rebuilds the file.</summary>
    public required IReadOnlyList<string> Chunks { get; init; }

    /// <summary>Why this version was captured (save/create/pre-delete/pre-overwrite…).</summary>
    public string Reason { get; init; } = "change";

    /// <summary>
    /// Git context when captured, e.g. "main @ 3a1b2c1" — set only for files inside a git repo,
    /// so a developer can tell versions apart by branch/commit. Null otherwise. Purely
    /// informational; never used to key or restore a version.
    /// </summary>
    public string? GitRef { get; init; }
}
