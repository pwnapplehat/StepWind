namespace StepWind.Core.Journal;

/// <summary>
/// Turns the raw USN record stream into user-meaningful <see cref="FileOperation"/>s — the
/// heart of the "flight recorder" timeline. Pure and injectable (path resolution is a
/// delegate), so the whole reconstruction is unit-tested against synthetic record streams
/// without touching a real disk.
///
/// The tricky Windows realities it handles (all learned from the hardware spike):
///   • Moves/renames arrive as two records (old-name + new-name) sharing a file reference
///     number; a differing parent reference means MOVE, same parent means RENAME.
///   • Modern deletes are POSIX unlinks: the file is first renamed into \$Extend\$Deleted
///     with a "hexid:name" marker, and the FileDelete flag lands on a LATER record for the
///     same file reference number — so we track each FRN's last *real* name and attribute
///     the delete to that, never to the marker.
/// </summary>
public sealed class OperationReconstructor
{
    private readonly Func<ulong, string?> _resolveDirectory;

    /// <param name="resolveDirectory">Best-effort FRN → directory path (null if not resolvable).</param>
    public OperationReconstructor(Func<ulong, string?>? resolveDirectory = null)
        => _resolveDirectory = resolveDirectory ?? (_ => null);

    public IReadOnlyList<FileOperation> Reconstruct(IEnumerable<UsnRecord> records)
    {
        var ops = new List<FileOperation>();
        var lastRealName = new Dictionary<ulong, string>();
        var lastParent = new Dictionary<ulong, ulong>();
        var isDir = new Dictionary<ulong, bool>();
        var lastTimestamp = new Dictionary<ulong, DateTime>();
        var pendingOldName = new Dictionary<ulong, (string Name, ulong Parent)>();
        var createdAndClosed = new HashSet<ulong>();
        var modified = new HashSet<ulong>();

        foreach (UsnRecord r in records)
        {
            bool marker = IsDeletedMarker(r.FileName);
            lastTimestamp[r.FileReferenceNumber] = r.TimestampUtc;
            if (!marker)
            {
                lastRealName[r.FileReferenceNumber] = r.FileName;
                lastParent[r.FileReferenceNumber] = r.ParentFileReferenceNumber;
                isDir[r.FileReferenceNumber] = r.IsDirectory;
            }

            if (r.Has(UsnReason.FileCreate))
            {
                createdAndClosed.Add(r.FileReferenceNumber);
            }

            if (r.Has(UsnReason.DataOverwrite) || r.Has(UsnReason.DataExtend) || r.Has(UsnReason.DataTruncation))
            {
                modified.Add(r.FileReferenceNumber);
            }

            if (r.Has(UsnReason.RenameOldName) && !marker)
            {
                pendingOldName[r.FileReferenceNumber] = (r.FileName, r.ParentFileReferenceNumber);
            }

            if (r.Has(UsnReason.RenameNewName) && !marker
                && pendingOldName.TryGetValue(r.FileReferenceNumber, out (string Name, ulong Parent) old))
            {
                bool crossDir = old.Parent != r.ParentFileReferenceNumber;
                string? oldDir = _resolveDirectory(old.Parent);
                string? newDir = _resolveDirectory(r.ParentFileReferenceNumber);
                ops.Add(new FileOperation
                {
                    Kind = crossDir ? OperationKind.Move : OperationKind.Rename,
                    FileReferenceNumber = r.FileReferenceNumber,
                    TimestampUtc = r.TimestampUtc,
                    Name = r.FileName,
                    OldPath = Combine(oldDir, old.Name),
                    NewPath = Combine(newDir, r.FileName),
                    IsDirectory = isDir.GetValueOrDefault(r.FileReferenceNumber),
                });
                pendingOldName.Remove(r.FileReferenceNumber);
            }

            if (r.Has(UsnReason.FileDelete))
            {
                string name = lastRealName.GetValueOrDefault(r.FileReferenceNumber, marker ? StripMarker(r.FileName) : r.FileName);
                ulong parent = lastParent.GetValueOrDefault(r.FileReferenceNumber, r.ParentFileReferenceNumber);
                ops.Add(new FileOperation
                {
                    Kind = OperationKind.Delete,
                    FileReferenceNumber = r.FileReferenceNumber,
                    TimestampUtc = r.TimestampUtc,
                    Name = name,
                    OldPath = Combine(_resolveDirectory(parent), name),
                    IsDirectory = isDir.GetValueOrDefault(r.FileReferenceNumber),
                });
            }
        }

        // Emit create/modify (on Close) for FRNs that weren't renamed/deleted — keeps the
        // timeline complete without double-counting a file that was created then moved.
        var terminal = new HashSet<ulong>(ops.Select(o => o.FileReferenceNumber));
        foreach (ulong frn in createdAndClosed)
        {
            if (terminal.Add(frn))
            {
                ops.Add(Simple(OperationKind.Create, frn, lastRealName, lastParent, isDir, lastTimestamp));
            }
        }

        foreach (ulong frn in modified)
        {
            if (terminal.Add(frn))
            {
                ops.Add(Simple(OperationKind.Modify, frn, lastRealName, lastParent, isDir, lastTimestamp));
            }
        }

        return [.. ops.OrderBy(o => o.TimestampUtc)];
    }

    private FileOperation Simple(OperationKind kind, ulong frn,
        Dictionary<ulong, string> names, Dictionary<ulong, ulong> parents, Dictionary<ulong, bool> dirs,
        Dictionary<ulong, DateTime> timestamps)
    {
        string name = names.GetValueOrDefault(frn, "?");
        return new FileOperation
        {
            Kind = kind,
            FileReferenceNumber = frn,
            TimestampUtc = timestamps.GetValueOrDefault(frn, DateTime.MinValue),
            Name = name,
            NewPath = Combine(_resolveDirectory(parents.GetValueOrDefault(frn)), name),
            IsDirectory = dirs.GetValueOrDefault(frn),
        };
    }

    private static string? Combine(string? dir, string name)
        => string.IsNullOrEmpty(dir) ? null : System.IO.Path.Combine(dir, name);

    /// <summary>Transient POSIX-unlink name: 16 hex digits, ':', then the original name.</summary>
    public static bool IsDeletedMarker(string name)
        => name.Length >= 17 && name[16] == ':' && name[..16].All(Uri.IsHexDigit);

    private static string StripMarker(string name)
        => IsDeletedMarker(name) ? name[17..] : name;
}
