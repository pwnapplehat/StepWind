using StepWind.Core.Attribution;
using StepWind.Core.Journal;
using Xunit;

namespace StepWind.Core.Tests;

/// <summary>
/// The timeline's "which app did this" label. The contract: a wrong name is worse than no
/// name. These tests pin the exact failure observed live — deletes made by one process were
/// attributed to an unrelated process that had merely been SEEN near the file — plus the
/// kind/time-window rules that prevent every variant of it.
/// </summary>
public class AttributionTests
{
    private static readonly DateTime T0 = new(2026, 07, 21, 12, 00, 00, DateTimeKind.Utc);

    private static Sighting S(string process, int pid, double secondsFromT0, FileActionKind kind)
        => new(process, pid, T0.AddSeconds(secondsFromT0), kind);

    // ------------------------- the live bug, pinned as a regression -------------------------

    [Fact]
    public void A_reaction_after_the_operation_does_not_steal_attribution_from_the_actor()
    {
        // Cursor deletes its db-journal at T0; a watcher-style process writes/acts on the
        // same path moments LATER (reacting). The old last-writer-wins cache blamed the
        // late-comer; the actor must win now.
        var sightings = new[]
        {
            S("Cursor", 100, -0.05, FileActionKind.Delete),   // the true actor
            S("StepWind.Mcp", 14264, +0.9, FileActionKind.Modify), // late reaction, wrong kind
        };

        Sighting? pick = FileAttributionTracker.Pick(sightings, T0, OperationKind.Delete);

        Assert.NotNull(pick);
        Assert.Equal("Cursor", pick.Process);
    }

    [Fact]
    public void No_kind_compatible_sighting_returns_null_not_a_guess()
    {
        // Only observation-adjacent activity (a write by some process seconds before) exists
        // for a MOVE — a write cannot explain a move, so the honest answer is "unknown".
        var sightings = new[]
        {
            S("OneDrive", 500, -3, FileActionKind.Modify),
            S("MsMpEng", 900, -1, FileActionKind.Create),
        };

        Assert.Null(FileAttributionTracker.Pick(sightings, T0, OperationKind.Move));
    }

    [Fact]
    public void Stale_sightings_outside_the_lookback_window_never_match()
    {
        // A delete-shaped event from 40s ago (an unrelated earlier action, or a recycled
        // PID's ghost) must not explain an operation happening now.
        var sightings = new[] { S("cmd", 777, -40, FileActionKind.Delete) };

        Assert.Null(FileAttributionTracker.Pick(sightings, T0, OperationKind.Delete));
    }

    [Fact]
    public void Far_future_sightings_never_match()
    {
        var sightings = new[] { S("Explorer", 123, +30, FileActionKind.Delete) };

        Assert.Null(FileAttributionTracker.Pick(sightings, T0, OperationKind.Delete));
    }

    // ----------------------------------- kind matching -----------------------------------

    [Fact]
    public void Delete_prefers_the_deleter_over_a_renamer_but_accepts_rename_as_posix_fallback()
    {
        // Full POSIX-unlink shape: Explorer renames into $Extend then sets disposition.
        var both = new[]
        {
            S("explorer", 42, -0.2, FileActionKind.Rename),
            S("explorer", 42, -0.1, FileActionKind.Delete),
        };
        Assert.Equal(FileActionKind.Delete, FileAttributionTracker.Pick(both, T0, OperationKind.Delete)!.Kind);

        // Marker-rename-only shape (the disposition event was missed): rename still explains it.
        var renameOnly = new[] { S("explorer", 42, -0.2, FileActionKind.Rename) };
        Assert.Equal("explorer", FileAttributionTracker.Pick(renameOnly, T0, OperationKind.Delete)!.Process);
    }

    [Fact]
    public void Move_matches_only_rename_shaped_events()
    {
        var sightings = new[]
        {
            S("robocopy", 10, -0.4, FileActionKind.Modify),
            S("explorer", 42, -0.2, FileActionKind.Rename),
            S("MsMpEng", 900, +0.3, FileActionKind.Delete),
        };

        Sighting? pick = FileAttributionTracker.Pick(sightings, T0, OperationKind.Move);

        Assert.Equal("explorer", pick!.Process);
    }

    [Fact]
    public void Create_falls_back_to_the_writer_when_no_namespace_create_was_seen()
    {
        var sightings = new[] { S("code", 61, -0.3, FileActionKind.Modify) };

        Assert.Equal("code", FileAttributionTracker.Pick(sightings, T0, OperationKind.Create)!.Process);
    }

    [Fact]
    public void Modify_prefers_the_writer_and_falls_back_to_an_atomic_replace_renamer()
    {
        // Editors that save via temp-file + rename author a Modify with a Rename event.
        var renamer = new[] { S("notepad", 7, -0.1, FileActionKind.Rename) };
        Assert.Equal("notepad", FileAttributionTracker.Pick(renamer, T0, OperationKind.Modify)!.Process);

        var both = new[]
        {
            S("notepad", 7, -0.5, FileActionKind.Rename),
            S("word", 8, -0.2, FileActionKind.Modify),
        };
        Assert.Equal("word", FileAttributionTracker.Pick(both, T0, OperationKind.Modify)!.Process);
    }

    [Fact]
    public void Latest_matching_sighting_wins_among_candidates()
    {
        var sightings = new[]
        {
            S("git", 11, -6, FileActionKind.Modify),
            S("code", 22, -0.5, FileActionKind.Modify),
        };

        Assert.Equal("code", FileAttributionTracker.Pick(sightings, T0, OperationKind.Modify)!.Process);
    }

    // -------------------------- history retention inside the tracker --------------------------

    [Fact]
    public void Write_bursts_collapse_but_do_not_evict_other_actors()
    {
        using var tracker = new FileAttributionTracker(); // never Start()ed — no ETW session
        const string path = @"C:\p\doc.txt";

        // The true deleter is seen first…
        tracker.RememberForTest(path, "explorer", 42, T0.AddSeconds(-1), FileActionKind.Delete);
        // …then a chatty writer floods 50 write events in a burst (would evict an 8-slot
        // ring without collapsing; must collapse to ~1 slot instead).
        for (int i = 0; i < 50; i++)
        {
            tracker.RememberForTest(path, "logger", 77, T0.AddMilliseconds(-900 + i * 10), FileActionKind.Modify);
        }

        string? who = tracker.Attribute(path, T0, OperationKind.Delete);

        Assert.NotNull(who);
        Assert.StartsWith("explorer", who);
    }

    [Fact]
    public void Attribute_formats_process_and_pid_and_returns_null_for_unknown_paths()
    {
        using var tracker = new FileAttributionTracker();
        tracker.RememberForTest(@"C:\p\a.txt", "explorer", 42, T0, FileActionKind.Rename);

        Assert.Equal("explorer (42)", tracker.Attribute(@"C:\p\a.txt", T0, OperationKind.Move));
        Assert.Null(tracker.Attribute(@"C:\p\never-seen.txt", T0, OperationKind.Move));
    }

    [Fact]
    public void The_kernels_lazy_writer_is_never_recorded_as_an_author()
    {
        // Measured live: System (pid 4) flushes another process's write ~1ms later. If it
        // were remembered it would win "latest Modify" and every save would say "System".
        using var tracker = new FileAttributionTracker();
        const string path = @"C:\p\doc.txt";
        tracker.RememberForTest(path, "cmd", 100, T0.AddMilliseconds(-20), FileActionKind.Modify);
        tracker.RememberForTest(path, "System", 4, T0.AddMilliseconds(-1), FileActionKind.Modify);
        tracker.RememberForTest(path, "Idle", 0, T0, FileActionKind.Modify);

        Assert.Equal("cmd (100)", tracker.Attribute(path, T0, OperationKind.Modify));
    }

    [Fact]
    public void Delete_on_close_sightings_attribute_del_style_deletes()
    {
        // cmd's `del` never issues a delete IRP — the FILE_DELETE_ON_CLOSE open is the only
        // authored event, recorded as a Delete-kind sighting by the ETW callback.
        using var tracker = new FileAttributionTracker();
        const string path = @"C:\p\doomed.txt";
        tracker.RememberForTest(path, "cmd", 6832, T0.AddMilliseconds(-30), FileActionKind.Delete);

        Assert.Equal("cmd (6832)", tracker.Attribute(path, T0, OperationKind.Delete));
    }
}

/// <summary>
/// Settings persistence binding. The bug this pins: Save() used to write unconditionally to
/// the one hardcoded %ProgramData% path, so every unit test that drove SetSettings through a
/// host OVERWROTE THE REAL MACHINE'S production settings (discovered when the real service
/// restarted into a test fixture's temp folders with the flight recorder off). Only Load()
/// binds an instance to a file; everything else must be inert.
/// </summary>
public class SettingsBindingTests
{
    [Fact]
    public void A_directly_constructed_instance_is_unbound_and_save_is_a_noop()
    {
        var s = new StepWind.Core.Engine.StepWindSettings();

        Assert.Null(s.BoundPath);
        s.Save(); // must not throw and must not write anywhere
        Assert.Null(s.BoundPath);
    }

    [Fact]
    public void A_bound_instance_saves_to_its_own_path_and_nowhere_else()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sw-settings-" + Guid.NewGuid().ToString("N"));
        string file = Path.Combine(dir, "settings.json");
        try
        {
            var s = new StepWind.Core.Engine.StepWindSettings { BoundPath = file, FlightRecorderEnabled = false };
            s.Save();

            Assert.True(File.Exists(file));
            Assert.Contains("\"FlightRecorderEnabled\": false", File.ReadAllText(file));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
