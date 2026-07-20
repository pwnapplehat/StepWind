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
///     same file reference number — LATER can be *minutes* (NTFS purges $Deleted lazily;
///     measured on 26200). The user saw the file vanish at the rename-to-marker instant, so
///     we emit the Delete right there, attributed to the FRN's last *real* name and original
///     parent, and suppress the eventual FileDelete record as a duplicate. Classic deletes
///     (no marker, e.g. directories) still come from FileDelete directly.
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
        var deleteEmitted = new HashSet<ulong>(); // POSIX deletes emitted at marker-rename time

        foreach (UsnRecord r in records)
        {
            bool marker = IsMarkerRecord(r);
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

            if (r.Has(UsnReason.RenameNewName) && marker)
            {
                // POSIX unlink: the rename into \$Extend\$Deleted IS the moment the user saw
                // the file vanish. The FileDelete flag arrives only when the last open handle
                // closes (Defender scans make that seconds-to-minutes later), so emit the
                // Delete here, under the file's last real name and original parent.
                if (deleteEmitted.Add(r.FileReferenceNumber))
                {
                    string name = lastRealName.GetValueOrDefault(r.FileReferenceNumber, StripMarker(r.FileName));
                    ulong parent = lastParent.GetValueOrDefault(r.FileReferenceNumber, 0UL);
                    ops.Add(new FileOperation
                    {
                        Kind = OperationKind.Delete,
                        FileReferenceNumber = r.FileReferenceNumber,
                        TimestampUtc = r.TimestampUtc,
                        Name = name,
                        OldPath = Combine(parent == 0 ? null : _resolveDirectory(parent), name),
                        IsDirectory = isDir.GetValueOrDefault(r.FileReferenceNumber),
                    });
                }

                pendingOldName.Remove(r.FileReferenceNumber); // never pair a marker as a move
            }
            else if (r.Has(UsnReason.RenameNewName) && !marker
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

            if (r.Has(UsnReason.FileDelete) && !deleteEmitted.Contains(r.FileReferenceNumber))
            {
                // Classic (non-POSIX) delete — the common case for directories and for files
                // nothing else had open. POSIX deletes were already emitted at marker time.
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

    /// <summary>
    /// Transient POSIX-unlink name inside \$Extend\$Deleted. Two shapes observed on real
    /// hardware: "16hex:name" (older builds) and bare 24-hex (26200: FRN + disambiguator,
    /// no original name — which is why we track each FRN's last real name ourselves).
    /// </summary>
    public static bool IsDeletedMarker(string name)
        => (name.Length >= 17 && name[16] == ':' && name[..16].All(Uri.IsHexDigit))
        || (name.Length is >= 16 and <= 32 && name.All(Uri.IsHexDigit));

    /// <summary>
    /// Precise per-record marker check: bare-hex markers embed the file's own FRN as their
    /// first 16 hex digits (observed on 26200: "007300000002BB9B175DB297" for FRN
    /// 0x007300000002BB9B). Requiring that match means a user's genuinely-all-hex filename
    /// (e.g. a git object) can never be misclassified; "hex:name" markers are unambiguous.
    /// </summary>
    private static bool IsMarkerRecord(UsnRecord r)
    {
        string name = r.FileName;
        if (name.Length >= 17 && name[16] == ':' && name[..16].All(Uri.IsHexDigit))
        {
            return true;
        }

        return name.Length is >= 16 and <= 32
            && name.All(Uri.IsHexDigit)
            && ulong.TryParse(name.AsSpan(0, 16), System.Globalization.NumberStyles.HexNumber, null, out ulong embedded)
            && embedded == r.FileReferenceNumber;
    }

    private static string StripMarker(string name)
        => IsDeletedMarker(name) && name.Contains(':') ? name[(name.IndexOf(':') + 1)..] : name;
}
