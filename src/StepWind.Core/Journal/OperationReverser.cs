namespace StepWind.Core.Journal;

/// <summary>Result of attempting to reverse an operation.</summary>
public sealed record ReverseResult(bool Success, string Message, string? RestoredPath = null);

/// <summary>Abstracts filesystem effects so reversal logic is unit-testable without real IO.</summary>
public interface IFileSystemActions
{
    bool Exists(string path);
    bool DirectoryExists(string path);
    void Move(string from, string to);
}

/// <summary>
/// Reverses a reconstructed operation — the "undo" behind the one-click timeline. A move or
/// rename is reversed by moving the item back, which needs NO stored content (this is why the
/// flight recorder can rescue a huge folder instantly). Every reversal is guarded:
///   • the item must still be where the operation left it (NewPath),
///   • the original location (OldPath) must be free — we never overwrite whatever lives there
///     now, so reversing can't itself cause the data loss it's meant to fix.
/// Deletes are not reversed here (there's nothing to move back); the app routes those to
/// Recycle-Bin restore or the version store instead.
/// </summary>
public static class OperationReverser
{
    public static ReverseResult Reverse(FileOperation op, IFileSystemActions fs)
    {
        if (op.Kind is not (OperationKind.Move or OperationKind.Rename))
        {
            return new ReverseResult(false, $"{op.Kind} can't be reversed by moving — use restore instead.");
        }

        if (string.IsNullOrEmpty(op.OldPath) || string.IsNullOrEmpty(op.NewPath))
        {
            return new ReverseResult(false, "Original or current path is unknown, so this can't be safely reversed.");
        }

        bool currentExists = op.IsDirectory ? fs.DirectoryExists(op.NewPath) : fs.Exists(op.NewPath);
        if (!currentExists)
        {
            return new ReverseResult(false, "The item is no longer where it was moved to — it may have moved again or been deleted.");
        }

        if (fs.Exists(op.OldPath) || fs.DirectoryExists(op.OldPath))
        {
            return new ReverseResult(false, "Something already exists at the original location; not overwriting it.");
        }

        try
        {
            fs.Move(op.NewPath, op.OldPath);
            return new ReverseResult(true, $"Moved back to {op.OldPath}", op.OldPath);
        }
        catch (Exception ex)
        {
            return new ReverseResult(false, "Reversal failed: " + ex.Message);
        }
    }
}
