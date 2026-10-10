using System.Text.Json;

namespace StepWind.Core.Journal;

/// <summary>
/// The timeline the GUI shows. Two failures made creates and deletes disappear after a few
/// minutes: the list lived only in memory (a service restart forgot it, and the USN cursor
/// does not replay it), and a burst of ordinary modifies — a first scan of a big folder does
/// this — pushed the creates and deletes out of the newest-N window.
///
/// Structural events (create, delete, move, rename) are kept ahead of modifies, and the list
/// is written to disk so it is still there after the service starts again.
/// </summary>
public sealed class TimelineLog
{
    /// <summary>How long a remembered operation stays. Older lines are dropped on load.</summary>
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(7);

    private readonly string _path;
    private readonly int _capacity;
    private readonly object _lock = new();
    private readonly LinkedList<FileOperation> _recent = new(); // newest first
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private bool _dirty;

    public TimelineLog(string path, int capacity = 5000)
    {
        _path = path;
        _capacity = Math.Max(1, capacity);
        Load();
    }

    public static string Token(FileOperation op)
        => $"{op.FileReferenceNumber}:{op.TimestampUtc.Ticks}:{(int)op.Kind}";

    public void Add(FileOperation op)
    {
        lock (_lock)
        {
            string token = Token(op);
            if (!_seen.Add(token))
            {
                return;
            }

            _recent.AddFirst(op);
            Trim();
            _dirty = true;
        }
    }

    /// <summary>
    /// Newest first. Structural operations are included even when newer modifies would
    /// otherwise fill the whole window.
    /// </summary>
    public IReadOnlyList<FileOperation> Recent(int limit)
    {
        if (limit <= 0)
        {
            return [];
        }

        lock (_lock)
        {
            var structural = new List<FileOperation>();
            var modifies = new List<FileOperation>();
            foreach (FileOperation op in _recent)
            {
                if (op.Kind == OperationKind.Modify)
                {
                    modifies.Add(op);
                }
                else
                {
                    structural.Add(op);
                }
            }

            var picked = new List<FileOperation>(Math.Min(limit, _recent.Count));
            foreach (FileOperation op in structural)
            {
                if (picked.Count >= limit)
                {
                    break;
                }

                picked.Add(op);
            }

            foreach (FileOperation op in modifies)
            {
                if (picked.Count >= limit)
                {
                    break;
                }

                picked.Add(op);
            }

            picked.Sort(static (a, b) => b.TimestampUtc.CompareTo(a.TimestampUtc));
            return picked;
        }
    }

    public FileOperation? Find(string token)
    {
        lock (_lock)
        {
            foreach (FileOperation op in _recent)
            {
                if (Token(op) == token)
                {
                    return op;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Fills in a process name that arrived after the operation was recorded. Newest first;
    /// stops once events are older than <paramref name="cutoff"/>.
    /// </summary>
    public void AttributeYoung(DateTime cutoff, Func<FileOperation, string?> attribute)
    {
        lock (_lock)
        {
            for (LinkedListNode<FileOperation>? node = _recent.First; node is not null; node = node.Next)
            {
                if (node.Value.TimestampUtc < cutoff)
                {
                    break;
                }

                if (node.Value.ByProcess is null && attribute(node.Value) is { } who)
                {
                    node.Value = node.Value with { ByProcess = who };
                    _dirty = true;
                }
            }
        }
    }

    public void Flush()
    {
        List<FileOperation> snapshot;
        lock (_lock)
        {
            if (!_dirty)
            {
                return;
            }

            snapshot = [.. _recent];
            _dirty = false;
        }

        try
        {
            string? dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string tmp = _path + ".tmp";
            var lines = new List<string>(snapshot.Count);
            foreach (FileOperation op in snapshot)
            {
                lines.Add(JsonSerializer.Serialize(op));
            }

            File.WriteAllLines(tmp, lines);
            File.Move(tmp, _path, overwrite: true);
        }
        catch
        {
            lock (_lock)
            {
                _dirty = true; // try again next flush
            }
        }
    }

    private void Trim()
    {
        while (_recent.Count > _capacity)
        {
            LinkedListNode<FileOperation>? oldestModify = null;
            for (LinkedListNode<FileOperation>? node = _recent.Last; node is not null; node = node.Previous)
            {
                if (node.Value.Kind == OperationKind.Modify)
                {
                    oldestModify = node;
                    break;
                }
            }

            LinkedListNode<FileOperation> drop = oldestModify ?? _recent.Last!;
            _seen.Remove(Token(drop.Value));
            _recent.Remove(drop);
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            DateTime cutoff = DateTime.UtcNow - KeepFor;
            var loaded = new List<FileOperation>();
            foreach (string line in File.ReadLines(_path))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    FileOperation? op = JsonSerializer.Deserialize<FileOperation>(line);
                    if (op is not null && op.TimestampUtc >= cutoff)
                    {
                        loaded.Add(op);
                    }
                }
                catch (JsonException)
                {
                    // one bad line does not throw away the rest of the timeline
                }
            }

            loaded.Sort(static (a, b) => a.TimestampUtc.CompareTo(b.TimestampUtc)); // oldest first, so AddFirst ends newest-first
            foreach (FileOperation op in loaded)
            {
                string token = Token(op);
                if (!_seen.Add(token))
                {
                    continue;
                }

                _recent.AddFirst(op);
            }

            Trim();
        }
        catch
        {
            // a damaged timeline file starts empty; new events still record
        }
    }
}
