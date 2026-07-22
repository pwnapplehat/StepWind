using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Text.Json;
using StepWind.Core.Attribution;

namespace StepWind.Core.Journal;

/// <summary>
/// The whole-machine flight recorder: continuously tails the NTFS USN journal on each fixed
/// volume, reconstructs user-meaningful operations, attributes them to a process via ETW, and
/// keeps a bounded rolling window in memory for the timeline UI. Persists a per-volume cursor
/// (journal id + last USN) so a restart resumes exactly where it left off — and detects a
/// journal-id change or a cursor that predates the journal's first record (wrap/overflow),
/// resetting to the current end rather than silently missing or double-reading.
///
/// Runs in the elevated service. All public members are thread-safe.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FlightRecorder : IDisposable
{
    private sealed record Cursor(ulong JournalId, long NextUsn);

    private readonly string _cursorPath;
    private readonly int _capacity;
    private readonly Action<string>? _log;
    private readonly FileAttributionTracker _attribution = new();
    private readonly ConcurrentDictionary<string, Cursor> _cursors = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<FileOperation> _recent = new();
    private readonly object _recentLock = new();
    private readonly System.Threading.Timer _timer;
    private readonly string[] _volumes;

    private readonly string[] _ignorePrefixes;

    public FlightRecorder(string stateDir, IEnumerable<string> volumes, int capacity = 5000,
        Action<string>? log = null, IEnumerable<string>? ignorePrefixes = null)
    {
        _cursorPath = System.IO.Path.Combine(stateDir, "usn-cursors.json");
        _capacity = capacity;
        _log = log;
        _volumes = [.. volumes];
        // Never show StepWind's own bookkeeping (store + state dir) on the timeline, plus any
        // caller-supplied noise prefixes. Keeps "what just happened" about the USER's actions.
        _ignorePrefixes = [.. (ignorePrefixes ?? []).Append(stateDir)
            .Select(p => p.TrimEnd('\\', '/')).Where(p => p.Length > 0)];
        System.IO.Directory.CreateDirectory(stateDir);
        LoadCursors();
        _attribution.Start();

        // Prime cursors to "now" for volumes we've never seen, so the first poll doesn't
        // replay the entire existing journal as if it all just happened.
        foreach (string vol in _volumes)
        {
            if (!_cursors.ContainsKey(vol))
            {
                TryPrime(vol);
            }
        }

        _timer = new System.Threading.Timer(_ => Poll(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
    }

    /// <summary>Most recent operations, newest first, up to <paramref name="limit"/>.</summary>
    public IReadOnlyList<FileOperation> Recent(int limit)
    {
        lock (_recentLock)
        {
            return [.. _recent.Take(limit)];
        }
    }

    /// <summary>
    /// A stable, opaque handle for an operation in the ring. The GUI receives this on the
    /// timeline and passes it back to reverse the operation. Crucially it is NOT the operation's
    /// data: the server re-derives the real paths from its OWN ring entry, so a client can never
    /// forge a "move THIS to THERE" request — the worst a bogus handle does is match nothing.
    /// Identity = file reference number + timestamp + kind (unique within the ring window).
    /// </summary>
    public static string OpToken(FileOperation op)
        => $"{op.FileReferenceNumber}:{op.TimestampUtc.Ticks}:{(int)op.Kind}";

    /// <summary>
    /// Looks up the server-side operation for a handle from <see cref="OpToken"/>. Returns null
    /// if no matching operation is currently in the ring (unknown/forged/expired handle), so the
    /// caller rejects it rather than acting on attacker-chosen data.
    /// </summary>
    public FileOperation? Find(string token)
    {
        lock (_recentLock)
        {
            foreach (FileOperation op in _recent)
            {
                if (OpToken(op) == token)
                {
                    return op;
                }
            }
        }

        return null;
    }

    private void TryPrime(string volume)
    {
        try
        {
            using var reader = new UsnJournalReader(volume);
            (ulong id, long next) = reader.Query();
            _cursors[volume] = new Cursor(id, next);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"prime {volume}: {ex.Message}");
        }
    }

    private void Poll()
    {
        foreach (string volume in _volumes)
        {
            try
            {
                PollVolume(volume);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"poll {volume}: {ex.Message}");
            }
        }
    }

    private void PollVolume(string volume)
    {
        using var reader = new UsnJournalReader(volume);
        UsnJournalState state = reader.QueryState();
        ulong journalId = state.JournalId;

        Cursor cursor = _cursors.GetValueOrDefault(volume) ?? new Cursor(journalId, state.NextUsn);

        // Decide where to resume. A journal-id change (recreated) resyncs to the end; a cursor
        // that has fallen behind the journal's lowest-valid USN means records were purged past us
        // (overflow/wrap) — we resume from the lowest valid USN and say so LOUDLY, so a gap is
        // reported instead of silently dropped (before, only a journal-id change was handled).
        (long start, UsnResync kind) = UsnResyncPolicy.DecideStart(
            cursor.JournalId, cursor.NextUsn, journalId, state.NextUsn, state.LowestValidUsn);

        if (kind == UsnResync.JournalChanged)
        {
            _log?.Invoke($"{volume}: journal changed (wrap/reset) — resyncing to end");
        }
        else if (kind == UsnResync.GapTruncated)
        {
            _log?.Invoke($"{volume}: USN GAP — the change journal was truncated past our cursor " +
                         $"(cursor {cursor.NextUsn} < lowest valid {state.LowestValidUsn}); some operations were missed. " +
                         "Resyncing from the earliest still-recorded change.");
        }

        (List<UsnRecord> records, long nextUsn) = reader.Read(journalId, start);
        if (records.Count > 0)
        {
            var reconstructor = new OperationReconstructor(reader.ResolveDirectory);
            foreach (FileOperation op in reconstructor.Reconstruct(records))
            {
                if (IsNoise(op))
                {
                    continue;
                }

                Add(op with { ByProcess = AttributeOp(op) });
            }
        }

        _cursors[volume] = new Cursor(journalId, nextUsn);
        SaveCursors();
        ReattributeFresh();
    }

    /// <summary>
    /// Second (and third…) chance for blank attributions. ETW real-time delivery lags the USN
    /// journal by a buffer flush (~1–3 s), while ops are attributed at the first poll after
    /// they happen — so the authored event for a fresh operation often hasn't ARRIVED yet at
    /// attribution time (measured live: the same process deleting the same file was named on
    /// one occurrence and blank on the next, purely by poll timing). Each poll re-runs
    /// attribution for still-blank ops younger than <see cref="ReattributeWindow"/>; the
    /// per-op time window itself is unchanged, so this closes the delivery race without ever
    /// widening what may match.
    /// </summary>
    private void ReattributeFresh()
    {
        DateTime cutoff = DateTime.UtcNow - ReattributeWindow;
        lock (_recentLock)
        {
            for (LinkedListNode<FileOperation>? node = _recent.First; node is not null; node = node.Next)
            {
                if (node.Value.TimestampUtc < cutoff)
                {
                    break; // newest-first: everything past here is too old to retry
                }

                if (node.Value.ByProcess is null && AttributeOp(node.Value) is { } who)
                {
                    node.Value = node.Value with { ByProcess = who };
                }
            }
        }
    }

    private static readonly TimeSpan ReattributeWindow = TimeSpan.FromSeconds(12);

    /// <summary>
    /// Filters out operations the user doesn't care about: StepWind's own store/state churn,
    /// and the temp/journal/database sidecar files apps constantly rewrite (-wal, -shm, .tmp).
    /// Keeping the timeline about real user actions is what makes it usable.
    /// </summary>
    private bool IsNoise(FileOperation op)
    {
        string? path = op.NewPath ?? op.OldPath;
        if (path is not null)
        {
            foreach (string prefix in _ignorePrefixes)
            {
                if (path.StartsWith(prefix + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        // Program-internal churn locations. These are where apps constantly rewrite their own
        // state/caches — almost never a file-management action the user would want to "undo".
        // The timeline is about YOUR documents; showing AppData/Windows/temp noise would bury
        // the one operation you care about. (A future "show everything" toggle can lift this.)
        if (path is not null && IsProgramInternal(path))
        {
            return true;
        }

        string name = op.Name;
        if (name.EndsWith("-wal", StringComparison.OrdinalIgnoreCase) || name.EndsWith("-shm", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".log", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".etl", StringComparison.OrdinalIgnoreCase) || OperationReconstructor.IsDeletedMarker(name)
            || name.StartsWith("usn-cursors", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static bool IsProgramInternal(string path)
    {
        // Match as path segments (case-insensitive) so we don't false-positive on a user
        // folder literally named "Temp" elsewhere.
        ReadOnlySpan<char> p = path;
        string[] needles =
        [
            @"\AppData\Local\", @"\AppData\Roaming\", @"\AppData\LocalLow\",
            @"\Windows\", @"\ProgramData\", @"\$Recycle.Bin\", @"\Program Files\", @"\Program Files (x86)\",
            @"\Microsoft\", @"\Packages\",
        ];
        foreach (string n in needles)
        {
            if (path.Contains(n, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private string? AttributeOp(FileOperation op)
    {
        // Deletes key on OldPath (NewPath is null); everything else on the resulting path.
        // Both the delete-disposition and the POSIX marker-rename ETW events carry the
        // file's ORIGINAL path, so the lookup key lines up for every operation kind.
        string? path = op.NewPath ?? op.OldPath;
        return path is null ? null : _attribution.Attribute(path, op.TimestampUtc, op.Kind);
    }

    private void Add(FileOperation op)
    {
        lock (_recentLock)
        {
            _recent.AddFirst(op);
            while (_recent.Count > _capacity)
            {
                _recent.RemoveLast();
            }
        }
    }

    private void LoadCursors()
    {
        try
        {
            if (System.IO.File.Exists(_cursorPath))
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, Cursor>>(System.IO.File.ReadAllText(_cursorPath));
                if (loaded is not null)
                {
                    foreach (KeyValuePair<string, Cursor> kv in loaded)
                    {
                        _cursors[kv.Key] = kv.Value;
                    }
                }
            }
        }
        catch
        {
            // corrupt cursor file → re-prime from scratch (safe: we resync to end)
        }
    }

    private void SaveCursors()
    {
        try
        {
            string tmp = _cursorPath + ".tmp";
            System.IO.File.WriteAllText(tmp, JsonSerializer.Serialize(_cursors));
            System.IO.File.Move(tmp, _cursorPath, overwrite: true);
        }
        catch
        {
            // best effort — a lost cursor just means a resync-to-end next start
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        _attribution.Dispose();
        SaveCursors();
    }
}
