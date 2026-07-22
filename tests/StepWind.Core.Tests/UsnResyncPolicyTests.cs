using StepWind.Core.Journal;
using Xunit;

namespace StepWind.Core.Tests;

/// <summary>
/// P1-4: a USN journal that wraps or is recreated must trigger a LOUD resync, never a silent
/// gap where operations vanish from the timeline. These pin the resume-point decision the flight
/// recorder makes every poll.
/// </summary>
public class UsnResyncPolicyTests
{
    [Fact]
    public void Continues_from_the_cursor_when_it_is_still_valid()
    {
        // Same journal, cursor well within the valid range → just keep reading.
        (long start, UsnResync kind) = UsnResyncPolicy.DecideStart(
            previousJournalId: 42, previousNextUsn: 5000,
            currentJournalId: 42, currentNextUsn: 6000, lowestValidUsn: 1000);

        Assert.Equal(5000, start);
        Assert.Equal(UsnResync.None, kind);
    }

    [Fact]
    public void Resyncs_to_end_when_the_journal_id_changed()
    {
        // Journal deleted + recreated: the old cursor means nothing; jump to the current end so
        // we don't replay the entire new journal as if it all just happened.
        (long start, UsnResync kind) = UsnResyncPolicy.DecideStart(
            previousJournalId: 42, previousNextUsn: 5000,
            currentJournalId: 99, currentNextUsn: 200, lowestValidUsn: 50);

        Assert.Equal(200, start);
        Assert.Equal(UsnResync.JournalChanged, kind);
    }

    [Fact]
    public void Detects_a_truncation_gap_and_resumes_from_lowest_valid()
    {
        // Same journal, but it wrapped past our cursor: records 5000..30000 are gone. Resume from
        // the earliest still-recorded change and flag the gap — never pretend nothing was missed.
        (long start, UsnResync kind) = UsnResyncPolicy.DecideStart(
            previousJournalId: 42, previousNextUsn: 5000,
            currentJournalId: 42, currentNextUsn: 90000, lowestValidUsn: 30000);

        Assert.Equal(30000, start);
        Assert.Equal(UsnResync.GapTruncated, kind);
    }

    [Fact]
    public void Cursor_exactly_at_lowest_valid_is_not_a_gap()
    {
        (long start, UsnResync kind) = UsnResyncPolicy.DecideStart(
            previousJournalId: 42, previousNextUsn: 30000,
            currentJournalId: 42, currentNextUsn: 90000, lowestValidUsn: 30000);

        Assert.Equal(30000, start);
        Assert.Equal(UsnResync.None, kind);
    }
}
