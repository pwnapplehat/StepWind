using StepWind.Core.Journal;
using Xunit;

namespace StepWind.Core.Tests;

/// <summary>
/// HillsCloud, 1.0.6 and 1.0.7: creates and deletes showed up, then vanished after 10–15 minutes.
/// Three causes: the timeline lived only in memory; a burst of ordinary saves pushed structural
/// events out of one shared window; and the GUI only ever read the newest 1000 rows, so anything
/// older than the first 1000 newer events was unreachable even though it was still retained.
/// </summary>
public class TimelineLogTests
{
    private static FileOperation Op(OperationKind kind, int n, string? name = null) => new()
    {
        Kind = kind,
        FileReferenceNumber = (ulong)(1000 + n),
        TimestampUtc = new DateTime(2026, 10, 10, 8, 0, 0, DateTimeKind.Utc).AddSeconds(n),
        Name = name ?? $"{kind}-{n}",
        OldPath = kind == OperationKind.Delete ? $@"D:\Work\{kind}-{n}.txt" : null,
        NewPath = kind == OperationKind.Delete ? null : $@"D:\Work\{kind}-{n}.txt",
    };

    private static string NewPath() => Path.Combine(Path.GetTempPath(), "stepwind-tl", Guid.NewGuid().ToString("N"), "timeline.jsonl");

    [Fact]
    public void A_delete_survives_a_burst_of_newer_modifies()
    {
        var log = new TimelineLog(NewPath(), capacity: 5000);
        log.Add(Op(OperationKind.Delete, 1, "chapter.docx"));
        log.Add(Op(OperationKind.Create, 2, "notes.txt"));
        for (int i = 3; i <= 302; i++)
        {
            log.Add(Op(OperationKind.Modify, i));
        }

        IReadOnlyList<FileOperation> window = log.Recent(250);
        Assert.Contains(window, op => op.Kind == OperationKind.Delete && op.Name == "chapter.docx");
        Assert.Contains(window, op => op.Kind == OperationKind.Create && op.Name == "notes.txt");
        Assert.Equal(250, window.Count);
    }

    [Fact]
    public void A_create_older_than_a_thousand_newer_events_is_reachable_with_a_full_page()
    {
        var log = new TimelineLog(NewPath(), capacity: 5000);
        log.Add(Op(OperationKind.Create, 1, "marker.txt"));
        for (int i = 2; i <= 1101; i++)
        {
            log.Add(Op(OperationKind.Create, i));
        }

        // A single 1000-row read cannot hold 1101 structural events, so the marker is past it...
        Assert.DoesNotContain(log.Recent(1000), op => op.Name == "marker.txt");

        // ...but the whole retained window (what the GUI pages through) still returns it.
        Assert.Contains(log.Recent(log.MaxRetained), op => op.Name == "marker.txt");
    }

    [Fact]
    public void A_delete_survives_a_modify_flood_larger_than_the_modify_cap()
    {
        var log = new TimelineLog(NewPath(), capacity: 5000);
        log.Add(Op(OperationKind.Delete, 1, "chapter.docx"));
        for (int i = 2; i <= 6001; i++)
        {
            log.Add(Op(OperationKind.Modify, i));
        }

        IReadOnlyList<FileOperation> all = log.Recent(log.MaxRetained);
        Assert.Contains(all, op => op.Kind == OperationKind.Delete && op.Name == "chapter.docx");
        Assert.Equal(5000, all.Count(op => op.Kind == OperationKind.Modify));
    }

    [Fact]
    public void Creates_and_deletes_are_still_there_after_a_restart()
    {
        string path = NewPath();
        var first = new TimelineLog(path, capacity: 100);
        first.Add(Op(OperationKind.Delete, 1, "chapter.docx"));
        first.Add(Op(OperationKind.Create, 2, "notes.txt"));
        first.Flush();

        var restarted = new TimelineLog(path, capacity: 100);
        IReadOnlyList<FileOperation> window = restarted.Recent(50);
        Assert.Contains(window, op => op.Kind == OperationKind.Delete && op.Name == "chapter.docx");
        Assert.Contains(window, op => op.Kind == OperationKind.Create && op.Name == "notes.txt");
        Assert.NotNull(restarted.Find(TimelineLog.Token(window.First(op => op.Name == "chapter.docx"))));
    }

    [Fact]
    public void Capacity_drops_old_modifies_before_a_delete()
    {
        var log = new TimelineLog(NewPath(), capacity: 4);
        log.Add(Op(OperationKind.Delete, 1, "keep-me.txt"));
        for (int i = 2; i <= 6; i++)
        {
            log.Add(Op(OperationKind.Modify, i));
        }

        IReadOnlyList<FileOperation> all = log.Recent(log.MaxRetained);
        Assert.Equal(4, all.Count(op => op.Kind == OperationKind.Modify));
        Assert.DoesNotContain(all, op => op.Name == "Modify-2");
        Assert.Contains(all, op => op.Name == "keep-me.txt");
    }

    [Fact]
    public void Structural_retention_is_capped_separately_from_modifies()
    {
        var log = new TimelineLog(NewPath(), capacity: 2, structuralCapacity: 3);
        for (int i = 1; i <= 5; i++)
        {
            log.Add(Op(OperationKind.Create, i));
        }

        for (int i = 10; i <= 12; i++)
        {
            log.Add(Op(OperationKind.Modify, i));
        }

        Assert.Equal(5, log.MaxRetained);
        IReadOnlyList<FileOperation> all = log.Recent(log.MaxRetained);
        Assert.Equal(3, all.Count(op => op.Kind == OperationKind.Create));
        Assert.DoesNotContain(all, op => op.Name == "Create-1");
        Assert.Contains(all, op => op.Name == "Create-5");
        Assert.Equal(2, all.Count(op => op.Kind == OperationKind.Modify));
        Assert.DoesNotContain(all, op => op.Name == "Modify-10");
    }
}
