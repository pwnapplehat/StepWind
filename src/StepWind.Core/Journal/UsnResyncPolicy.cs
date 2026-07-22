namespace StepWind.Core.Journal;

/// <summary>Why the flight recorder had to jump its read cursor rather than continue in sequence.</summary>
public enum UsnResync
{
    /// <summary>Cursor is valid; continue reading from where we left off.</summary>
    None,

    /// <summary>The journal was deleted/recreated (new id) — the old cursor is meaningless.</summary>
    JournalChanged,

    /// <summary>The journal wrapped/was truncated past our cursor, so some records are gone forever.</summary>
    GapTruncated,
}

/// <summary>
/// Decides where to resume reading the USN journal, so a gap is handled LOUDLY instead of
/// silently dropping operations. Pure/primitive-typed so it's unit-tested without a volume:
///
///  • different journal id  → the journal was recreated; resync to the current end (don't replay
///    the whole new journal as if it all just happened);
///  • our cursor is older than the journal's lowest still-valid USN → the records between them
///    have been purged (overflow/wrap), so we've missed some — resume from the lowest valid USN
///    to recover what's still there, and report the gap;
///  • otherwise continue from our cursor.
/// </summary>
public static class UsnResyncPolicy
{
    public static (long Start, UsnResync Kind) DecideStart(
        ulong previousJournalId, long previousNextUsn,
        ulong currentJournalId, long currentNextUsn, long lowestValidUsn)
    {
        if (previousJournalId != currentJournalId)
        {
            return (currentNextUsn, UsnResync.JournalChanged);
        }

        if (previousNextUsn < lowestValidUsn)
        {
            return (lowestValidUsn, UsnResync.GapTruncated);
        }

        return (previousNextUsn, UsnResync.None);
    }
}
