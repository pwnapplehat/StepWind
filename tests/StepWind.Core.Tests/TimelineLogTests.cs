using StepWind.Core.Journal;
using Xunit;

namespace StepWind.Core.Tests;

/// <summary>
/// HillsCloud, 1.0.6: creates and deletes showed up, then vanished after 10–15 minutes.
/// The timeline kept only the newest N events in memory, so a burst of ordinary saves
/// pushed the creates and deletes out, and a service restart forgot them entirely.
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

    [Fact]
    public void A_delete_survives_a_burst_of_newer_modifies()
    {
        string path = Path.Combine(Path.GetTempPath(), "stepwind-tl", Guid.NewGuid().ToString("N"), "timeline.jsonl");
        var log = new TimelineLog(path, capacity: 5000);
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
    public void Creates_and_deletes_are_still_there_after_a_restart()
    {
        string path = Path.Combine(Path.GetTempPath(), "stepwind-tl", Guid.NewGuid().ToString("N"), "timeline.jsonl");
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
        string path = Path.Combine(Path.GetTempPath(), "stepwind-tl", Guid.NewGuid().ToString("N"), "timeline.jsonl");
        var log = new TimelineLog(path, capacity: 4);
        log.Add(Op(OperationKind.Delete, 1, "keep-me.txt"));
        for (int i = 2; i <= 6; i++)
        {
            log.Add(Op(OperationKind.Modify, i));
        }

        IReadOnlyList<FileOperation> all = log.Recent(20);
        Assert.Equal(4, all.Count);
        Assert.Contains(all, op => op.Name == "keep-me.txt");
    }
}
