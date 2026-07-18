namespace StepWind.Core.Journal;

/// <summary>The kind of filesystem operation reconstructed from journal records.</summary>
public enum OperationKind
{
    Create,
    Modify,
    Rename,
    Move,
    Delete,
}

/// <summary>
/// A user-meaningful operation rebuilt from raw USN records — the entries on StepWind's
/// timeline ("Explorer moved ProjectX to Desktop\old at 2:31 PM"). A move/rename is a single
/// operation here even though NTFS emits it as two records (old-name + new-name). Deletes are
/// detected FRN-centrically because modern Windows unlinks POSIX-style (the file is renamed
/// into \$Extend\$Deleted first, and the delete flag lands on a later record for the same
/// file reference number).
/// </summary>
public sealed record FileOperation
{
    public required OperationKind Kind { get; init; }

    public required ulong FileReferenceNumber { get; init; }

    public required DateTime TimestampUtc { get; init; }

    /// <summary>The file/folder's user-visible name at the time of the operation.</summary>
    public required string Name { get; init; }

    /// <summary>Full path before the operation (rename/move/delete), if resolvable.</summary>
    public string? OldPath { get; init; }

    /// <summary>Full path after the operation (create/rename/move), if resolvable.</summary>
    public string? NewPath { get; init; }

    /// <summary>Process that performed it, filled in by ETW attribution when available.</summary>
    public string? ByProcess { get; init; }

    /// <summary>True for directories (their moves are reversible without stored content).</summary>
    public bool IsDirectory { get; init; }

    /// <summary>Whether this operation can be reversed directly (a move/rename back).</summary>
    public bool IsReversible => Kind is OperationKind.Move or OperationKind.Rename
        && !string.IsNullOrEmpty(OldPath) && !string.IsNullOrEmpty(NewPath);
}
