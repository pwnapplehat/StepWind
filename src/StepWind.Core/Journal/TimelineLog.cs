using System.Text.Json;

namespace StepWind.Core.Journal;

/// <summary>
/// The timeline the GUI shows. Three failures made creates and deletes disappear for a user:
/// the list lived only in memory (a service restart forgot it, and the USN cursor does not replay
/// it); a burst of ordinary modifies pushed structural events out of one shared newest-N window;
/// and the GUI only ever asked for the newest 1000 rows, so anything older than the first 1000
/// newer events was unreachable even while it was still retained.
///
/// Structural events (create, delete, move, rename) are kept ahead of modifies and have their own,
/// much larger retention, so a flood of saves can never evict them. The list is written to disk so
/// it is still there after the service starts again, and <see cref="Recent"/> can return the whole
/// retained window in one read (see <see cref="MaxRetained"/>).
/// </summary>
public sealed class TimelineLog
{
    /// <summary>How long a remembered operation stays. Older lines are dropped on load.</summary>
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(7);

    /// <summary>Default number of structural operations (create, delete, move, rename) retained.</summary>
    public const int DefaultStructuralCapacity = 20000;

    private readonly string _path;
    private readonly int _modifyCapacity;
    private readonly int _structuralCapacity;
    private readonly object _lock = new();
    private readonly LinkedList<FileOperation> _recent = new(); // newest first
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly HashSet<string> _outlasting = new(StringComparer.Ordinal); // structural tokens that go last
    private readonly Func<FileOperation, bool>? _outlastsOthers;
    private int _modifyCount;
    private bool _dirty;

    /// <summary>
    /// Creates a timeline backed by <paramref name="path"/> and loads whatever it already holds.
    /// </summary>
    /// <param name="path">JSON-lines file the timeline persists to.</param>
    /// <param name="capacity">Maximum modify operations retained. The oldest modifies go first.</param>
    /// <param name="structuralCapacity">Maximum create/delete/move/rename operations retained.</param>
    /// <param name="outlastsOthers">
    /// Marks structural operations that matter most (those inside a protected folder). When the
    /// structural cap is reached every other structural operation is dropped first, so a build that
    /// writes thousands of files somewhere else cannot push your protected-folder creates and
    /// deletes out. Evaluated once, when the operation is recorded.
    /// </param>
    public TimelineLog(string path, int capacity = 5000, int structuralCapacity = DefaultStructuralCapacity,
        Func<FileOperation, bool>? outlastsOthers = null)
    {
        _path = path;
        _modifyCapacity = Math.Max(1, capacity);
        _structuralCapacity = Math.Max(1, structuralCapacity);
        _outlastsOthers = outlastsOthers;
        Load();
    }

    /// <summary>
    /// The most operations this log can hold. A reader asking for this many gets the entire
    /// retained window in one read, so nothing that is still kept can be out of reach.
    /// </summary>
    public int MaxRetained => _modifyCapacity + _structuralCapacity;

    /// <summary>
    /// Stable identity for an operation: file reference number, timestamp, and kind. Two different
    /// operations never share all three, so the token can be persisted and used to find an entry.
    /// </summary>
    public static string Token(FileOperation op)
        => $"{op.FileReferenceNumber}:{op.TimestampUtc.Ticks}:{(int)op.Kind}";

    /// <summary>
    /// Records an operation unless one with the same token is already held. Enforces the retention
    /// caps immediately so memory stays bounded while the service runs.
    /// </summary>
    public void Add(FileOperation op)
    {
        lock (_lock)
        {
            if (!_seen.Add(Token(op)))
            {
                return;
            }

            Insert(op);
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

    /// <summary>Looks up a retained operation by its <see cref="Token"/>; null when it has aged out.</summary>
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

    /// <summary>
    /// Writes the timeline to disk when it changed since the last write. The write goes through a
    /// temp file and a move so a crash mid-write cannot leave a half-written timeline behind.
    /// </summary>
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

    /// <summary>
    /// Puts an operation at the newest end of the ring and keeps the modify count current.
    /// The caller must already hold <c>_lock</c> and must have claimed the token in <c>_seen</c>.
    /// </summary>
    private void Insert(FileOperation op)
    {
        _recent.AddFirst(op);
        if (op.Kind == OperationKind.Modify)
        {
            _modifyCount++;
        }
        else if (OutlastsOthers(op))
        {
            _outlasting.Add(Token(op));
        }
    }

    private bool OutlastsOthers(FileOperation op)
    {
        try
        {
            return _outlastsOthers?.Invoke(op) == true;
        }
        catch
        {
            return false; // a failing rule must never stop an operation from being recorded
        }
    }

    /// <summary>
    /// Enforces both retention caps. Modifies go first when the modify cap is exceeded; structural
    /// events are only dropped, oldest first, when they exceed their own (larger) cap. The caller
    /// must hold <c>_lock</c>.
    /// </summary>
    private void Trim()
    {
        while (_modifyCount > _modifyCapacity && DropOldest(modifies: true))
        {
        }

        while (_recent.Count - _modifyCount > _structuralCapacity && DropOldest(modifies: false))
        {
        }
    }

    /// <summary>
    /// Removes the oldest operation of one class (modify, or structural). Returns false when none
    /// of that class is held. Among structural operations the oldest one that does NOT outlast the
    /// others goes first; a protected-folder operation is only dropped once nothing else is left to
    /// drop. The caller must hold <c>_lock</c>.
    /// </summary>
    private bool DropOldest(bool modifies)
    {
        LinkedListNode<FileOperation>? fallback = null;
        for (LinkedListNode<FileOperation>? node = _recent.Last; node is not null; node = node.Previous)
        {
            if ((node.Value.Kind == OperationKind.Modify) != modifies)
            {
                continue;
            }

            if (!modifies && _outlasting.Contains(Token(node.Value)))
            {
                fallback ??= node; // the oldest protected one — dropped only if nothing else can be
                continue;
            }

            Remove(node, modifies);
            return true;
        }

        if (fallback is not null)
        {
            Remove(fallback, modifies);
            return true;
        }

        return false;
    }

    private void Remove(LinkedListNode<FileOperation> node, bool modifies)
    {
        string token = Token(node.Value);
        _seen.Remove(token);
        _outlasting.Remove(token);
        _recent.Remove(node);
        if (modifies)
        {
            _modifyCount--;
        }
    }

    /// <summary>
    /// Loads lines written by <see cref="Flush"/>, dropping anything older than <see cref="KeepFor"/>
    /// and tolerating individually damaged lines. A missing or unreadable file starts an empty timeline.
    /// </summary>
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

            loaded.Sort(static (a, b) => a.TimestampUtc.CompareTo(b.TimestampUtc)); // oldest first, so Insert ends newest-first
            foreach (FileOperation op in loaded)
            {
                if (_seen.Add(Token(op)))
                {
                    Insert(op);
                }
            }

            Trim();
        }
        catch
        {
            // a damaged timeline file starts empty; new events still record
        }
    }
}
