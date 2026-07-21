using System.Runtime.Versioning;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using StepWind.Core.Journal;

namespace StepWind.Core.Attribution;

/// <summary>The shape of an ETW file event we treat as AUTHORSHIP (never observation).</summary>
public enum FileActionKind
{
    Create,
    Modify,
    Rename,
    Delete,
}

/// <summary>One authored file action seen via ETW: who, when, and what shape of action.</summary>
public sealed record Sighting(string Process, int Pid, DateTime WhenUtc, FileActionKind Kind);

/// <summary>
/// Answers "which process did this?" for the operation timeline, using ETW kernel file events.
/// A wrong name is worse than no name — the timeline accusing the wrong app of deleting a
/// file destroys the product's core promise. Three rules keep attribution honest:
///
///  1. AUTHORSHIP ONLY. We remember namespace creates, writes, renames, deletes, and
///     set-disposition/rename info calls. We deliberately do NOT remember handle opens
///     (FileIOCreate) or stats (FileIOQueryInfo): watchers, antivirus, indexers, and Explorer
///     constantly OBSERVE files right around the moment someone else changes them, and the
///     old last-writer-wins cache regularly blamed those observers for the change.
///
///  2. MATCH THE OPERATION, NOT "NOW". Each path keeps a small recent history, and the
///     timeline's USN operation is matched against it by the operation's OWN kind and
///     timestamp (a Delete only matches delete-shaped events at-or-just-before the delete).
///     Reactions that come AFTER the operation can no longer overwrite the true actor.
///
///  3. NAMES RESOLVED AT EVENT TIME. The session enables the kernel Process provider so
///     TraceEvent maps PID→name from process start/stop events — a recycled PID no longer
///     resolves to whoever owns that number when we happen to look.
///
/// Runs on a background thread inside the elevated service; bounded, time-expiring history
/// keeps memory flat. Never throws into the caller — attribution is an enhancement, and the
/// timeline is still correct (minus a process name) without it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FileAttributionTracker : IDisposable
{
    private const int MaxSightingsPerPath = 8;
    private const int MaxPaths = 20_000;

    /// <summary>Bursts (same process+kind in quick succession) collapse into one sighting.</summary>
    private static readonly TimeSpan BurstCollapse = TimeSpan.FromMilliseconds(300);

    /// <summary>How far BEFORE the operation an authored event may lie and still explain it.</summary>
    private static readonly TimeSpan LookBack = TimeSpan.FromSeconds(10);

    /// <summary>Forward slack for ETW-vs-USN timestamp jitter (they are separate pipelines).</summary>
    private static readonly TimeSpan LookAhead = TimeSpan.FromSeconds(1.5);

    private readonly object _lock = new();
    private readonly Dictionary<string, List<Sighting>> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private TraceEventSession? _session;
    private Thread? _pump;
    private volatile bool _running;

    public bool IsRunning => _running;

    public void Start()
    {
        if (_running || !(TraceEventSession.IsElevated() ?? false))
        {
            return;
        }

        _session = new TraceEventSession("StepWindAttribution");
        // Process keyword: TraceEvent builds a live PID→name map from process start/stop
        // events, so names are resolved as-of the event, not as-of our lookup (PID reuse).
        _session.EnableKernelProvider(
            KernelTraceEventParser.Keywords.Process |
            KernelTraceEventParser.Keywords.FileIOInit |
            KernelTraceEventParser.Keywords.FileIO);

        _session.Source.Kernel.FileIOFileCreate += d =>
            Remember(d.FileName, d.ProcessName, d.ProcessID, d.TimeStamp.ToUniversalTime(), FileActionKind.Create);
        _session.Source.Kernel.FileIOWrite += d =>
            Remember(d.FileName, d.ProcessName, d.ProcessID, d.TimeStamp.ToUniversalTime(), FileActionKind.Modify);
        _session.Source.Kernel.FileIORename += d =>
            Remember(d.FileName, d.ProcessName, d.ProcessID, d.TimeStamp.ToUniversalTime(), FileActionKind.Rename);
        _session.Source.Kernel.FileIODelete += d =>
            Remember(d.FileName, d.ProcessName, d.ProcessID, d.TimeStamp.ToUniversalTime(), FileActionKind.Delete);
        _session.Source.Kernel.FileIOCreate += d =>
        {
            // A handle open is OBSERVATION (watchers/AV/indexers do it constantly and must
            // never be blamed) — with ONE exception: an open carrying FILE_DELETE_ON_CLOSE
            // (0x1000) is how `del`, DeleteFile, and most programmatic deletes actually
            // delete. No delete IRP ever fires for those, so this open IS the authorship
            // event (measured on real hardware: cmd's `del` = open 0x1040 → cleanup → gone).
            if (((uint)d.CreateOptions & 0x1000) != 0)
            {
                Remember(d.FileName, d.ProcessName, d.ProcessID, d.TimeStamp.ToUniversalTime(), FileActionKind.Delete);
            }
        };
        _session.Source.Kernel.FileIOSetInfo += d =>
        {
            // Only two InfoClasses are authorship: disposition (delete) and rename.
            // FILE_INFORMATION_CLASS: 13 = FileDispositionInformation, 64 = ...InformationEx,
            // 10 = FileRenameInformation, 65 = FileRenameInformationEx.
            FileActionKind? kind = d.InfoClass switch
            {
                13 or 64 => FileActionKind.Delete,
                10 or 65 => FileActionKind.Rename,
                _ => null,
            };
            if (kind is { } k)
            {
                Remember(d.FileName, d.ProcessName, d.ProcessID, d.TimeStamp.ToUniversalTime(), k);
            }
        };

        _pump = new Thread(() => { try { _session.Source.Process(); } catch { } })
        {
            IsBackground = true,
            Name = "StepWind.Attribution",
        };
        _running = true;
        _pump.Start();
    }

    /// <summary>
    /// The process that authored the given operation, or null when no honest answer exists.
    /// Matches by the operation's own kind and timestamp — see <see cref="Pick"/>.
    /// </summary>
    public string? Attribute(string fullPath, DateTime opTimeUtc, OperationKind opKind)
    {
        Sighting? hit;
        lock (_lock)
        {
            if (!_byPath.TryGetValue(fullPath, out List<Sighting>? sightings))
            {
                return null;
            }
            hit = Pick(sightings, opTimeUtc, opKind);
        }

        return hit is null ? null : $"{hit.Process} ({hit.Pid})";
    }

    /// <summary>
    /// Pure selection logic (unit-tested): the latest kind-compatible sighting inside the
    /// operation's time window, preferring an exact kind match over a compatible fallback.
    /// Null when nothing fits — an honest blank beats a plausible-looking wrong answer.
    /// </summary>
    internal static Sighting? Pick(IReadOnlyList<Sighting> sightings, DateTime opTimeUtc, OperationKind opKind)
    {
        (FileActionKind preferred, FileActionKind? fallback) = opKind switch
        {
            // A "new file" is authored by its namespace-create; editors that write a temp
            // file and rename it into place author it with a rename; plain writes follow.
            OperationKind.Create => (FileActionKind.Create, (FileActionKind?)FileActionKind.Modify),
            OperationKind.Modify => (FileActionKind.Modify, (FileActionKind?)FileActionKind.Rename),
            OperationKind.Rename => (FileActionKind.Rename, (FileActionKind?)null),
            OperationKind.Move => (FileActionKind.Rename, (FileActionKind?)null),
            // POSIX-style deletes surface in ETW as a rename (into \$Extend\$Deleted) plus a
            // disposition set, so a rename sighting on the old path can honestly explain one.
            OperationKind.Delete => (FileActionKind.Delete, (FileActionKind?)FileActionKind.Rename),
            _ => (FileActionKind.Modify, (FileActionKind?)null),
        };

        DateTime earliest = opTimeUtc - LookBack;
        DateTime latest = opTimeUtc + LookAhead;

        Sighting? best = null;
        Sighting? bestFallback = null;
        foreach (Sighting s in sightings)
        {
            if (s.WhenUtc < earliest || s.WhenUtc > latest)
            {
                continue;
            }

            if (s.Kind == preferred && (best is null || s.WhenUtc > best.WhenUtc))
            {
                best = s;
            }
            else if (fallback is { } fb && s.Kind == fb && (bestFallback is null || s.WhenUtc > bestFallback.WhenUtc))
            {
                bestFallback = s;
            }
        }

        return best ?? bestFallback;
    }

    private void Remember(string path, string process, int pid, DateTime whenUtc, FileActionKind kind)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(process))
        {
            return;
        }

        // The kernel itself (System, pid 4) flushes other processes' writes via the lazy
        // writer moments after the real write — it is never the author the user means.
        // Idle (0) likewise. (Measured: System's cache flush lands ~1ms after cmd's write.)
        if (pid is 0 or 4)
        {
            return;
        }

        lock (_lock)
        {
            if (!_byPath.TryGetValue(path, out List<Sighting>? sightings))
            {
                sightings = new List<Sighting>(4);
                _byPath[path] = sightings;
            }

            // Collapse write/save bursts from the same process into one refreshed sighting so
            // a busy writer can't evict the other actors from the per-path history.
            if (sightings.Count > 0)
            {
                Sighting newest = sightings[^1];
                if (newest.Pid == pid && newest.Kind == kind && whenUtc - newest.WhenUtc < BurstCollapse)
                {
                    sightings[^1] = newest with { WhenUtc = whenUtc };
                    return;
                }
            }

            sightings.Add(new Sighting(process, pid, whenUtc, kind));
            if (sightings.Count > MaxSightingsPerPath)
            {
                sightings.RemoveAt(0);
            }

            // Bound the cache: prune stale paths occasionally.
            if (_byPath.Count > MaxPaths)
            {
                DateTime cutoff = DateTime.UtcNow.AddMinutes(-5);
                foreach (string key in _byPath
                    .Where(kv => kv.Value.Count == 0 || kv.Value[^1].WhenUtc < cutoff)
                    .Select(kv => kv.Key).ToList())
                {
                    _byPath.Remove(key);
                }
            }
        }
    }

    /// <summary>Test seam: feeds a sighting exactly as the ETW callbacks do.</summary>
    internal void RememberForTest(string path, string process, int pid, DateTime whenUtc, FileActionKind kind)
        => Remember(path, process, pid, whenUtc, kind);

    public void Dispose()
    {
        _running = false;
        try { _session?.Stop(); } catch { }
        _session?.Dispose();
        _pump?.Join(2000);
    }
}
