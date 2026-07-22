using System.Collections.Concurrent;

namespace StepWind.Core.Engine;

/// <summary>
/// Coalesces rapid change notifications into one capture per file. Editors and apps write a
/// file several times per "save" (temp write, rename, attribute touch); without debouncing
/// we'd store a burst of near-identical versions. A path becomes "ready" only once it has
/// been quiet for <see cref="QuietPeriod"/>, so we capture the settled result. Thread-safe;
/// the engine polls <see cref="TakeReady"/> on a timer.
/// </summary>
public sealed class ChangeDebouncer
{
    private readonly ConcurrentDictionary<string, DateTime> _pending = new(StringComparer.OrdinalIgnoreCase);

    public TimeSpan QuietPeriod { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Records that a path changed "now" (resets its quiet timer).</summary>
    public void Touch(string path, DateTime nowUtc) => _pending[path] = nowUtc;

    /// <summary>Drops a pending path (e.g. it was deleted, so there's nothing left to capture).</summary>
    public void Forget(string path) => _pending.TryRemove(path, out _);

    public int PendingCount => _pending.Count;

    /// <summary>Removes and returns the paths that have been quiet long enough to capture.</summary>
    public IReadOnlyList<string> TakeReady(DateTime nowUtc)
    {
        var ready = new List<string>();
        foreach (KeyValuePair<string, DateTime> kv in _pending)
        {
            if (nowUtc - kv.Value >= QuietPeriod && _pending.TryRemove(kv.Key, out _))
            {
                ready.Add(kv.Key);
            }
        }

        return ready;
    }
}
