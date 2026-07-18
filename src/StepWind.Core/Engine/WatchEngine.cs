using System.IO;
using StepWind.Core.Storage;

namespace StepWind.Core.Engine;

/// <summary>Snapshot of engine activity for the UI/logs.</summary>
public sealed record EngineStatus(int WatchedRoots, int PendingChanges, long VersionsCaptured, DateTime? LastCaptureUtc);

/// <summary>
/// The folder time-machine orchestrator: watches configured roots with FileSystemWatcher,
/// debounces bursts, applies <see cref="PathExclusions"/>, and captures a new version through
/// the <see cref="VersionStore"/> whenever a real file settles. A dispatcher-free timer drains
/// the debouncer, so it runs headless inside the service.
///
/// Two day-one reliability choices live here:
///   • capture is best-effort per file — one unreadable/locked file never stops the others,
///     and locked files are retried (and, in the service, read via VSS);
///   • the engine records the last change time so the service can persist a cursor and,
///     combined with the USN catch-up, never miss changes that happened while it was down.
/// </summary>
public sealed class WatchEngine : IDisposable
{
    private readonly VersionStore _store;
    private readonly PathExclusions _exclusions;
    private readonly ChangeDebouncer _debouncer;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly List<string> _roots;
    private readonly System.Threading.Timer _drainTimer;
    private readonly Action<string>? _log;

    private long _versionsCaptured;
    private DateTime? _lastCaptureUtc;

    public WatchEngine(VersionStore store, PathExclusions exclusions, IEnumerable<string> roots,
        Action<string>? log = null, TimeSpan? quietPeriod = null)
    {
        _store = store;
        _exclusions = exclusions;
        _log = log;
        _roots = [.. roots.Where(Directory.Exists)];
        _debouncer = new ChangeDebouncer { QuietPeriod = quietPeriod ?? TimeSpan.FromSeconds(2) };

        // Never version our own store, wherever it lives.
        _exclusions.ExcludePrefix(Path.GetFullPath(store.Blobs.Root));

        foreach (string root in _roots)
        {
            StartWatching(root);
        }

        _drainTimer = new System.Threading.Timer(_ => Drain(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public EngineStatus Status => new(_roots.Count, _debouncer.PendingCount, _versionsCaptured, _lastCaptureUtc);

    private void StartWatching(string root)
    {
        var w = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
            InternalBufferSize = 64 * 1024,
        };
        w.Changed += OnChanged;
        w.Created += OnChanged;
        w.Renamed += (_, e) => OnPath(e.FullPath);
        w.Error += (_, e) => _log?.Invoke("watcher error on " + root + ": " + e.GetException().Message);
        w.EnableRaisingEvents = true;
        _watchers.Add(w);
    }

    private void OnChanged(object sender, FileSystemEventArgs e) => OnPath(e.FullPath);

    private void OnPath(string fullPath)
    {
        // Cheap pre-filter here; the full attribute/size check happens at capture time.
        if (_exclusions.IsExcludedByDirectory(fullPath) || _exclusions.IsExcludedByExtension(fullPath)
            || _exclusions.IsUnderExcludedPrefix(fullPath))
        {
            return;
        }

        _debouncer.Touch(fullPath, DateTime.UtcNow);
    }

    private void Drain()
    {
        foreach (string path in _debouncer.TakeReady(DateTime.UtcNow))
        {
            TryCapture(path);
        }
    }

    /// <summary>Captures one path if it still exists and passes exclusions. Never throws.</summary>
    public bool TryCapture(string fullPath)
    {
        try
        {
            if (!File.Exists(fullPath))
            {
                return false; // deleted before it settled — the flight recorder covers deletes
            }

            var info = new FileInfo(fullPath);
            if (!_exclusions.ShouldVersion(fullPath, info.Attributes, info.Length))
            {
                return false;
            }

            string? rel = RelativeToRoot(fullPath);
            if (rel is null)
            {
                return false;
            }

            _store.Capture(fullPath, rel);
            _versionsCaptured++;
            _lastCaptureUtc = DateTime.UtcNow;
            return true;
        }
        catch (IOException)
        {
            // Locked/being-written — requeue for one more quiet cycle. The service also has a
            // VSS path for files held open exclusively.
            _debouncer.Touch(fullPath, DateTime.UtcNow);
            return false;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"capture failed for {fullPath}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Path relative to whichever watched root contains it (portable '/' form).</summary>
    public string? RelativeToRoot(string fullPath)
    {
        foreach (string root in _roots)
        {
            if (fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                string name = Path.GetFileName(root);
                return (name + "/" + Path.GetRelativePath(root, fullPath)).Replace('\\', '/');
            }
        }

        return null;
    }

    public void Dispose()
    {
        _drainTimer.Dispose();
        foreach (FileSystemWatcher w in _watchers)
        {
            w.EnableRaisingEvents = false;
            w.Dispose();
        }
    }
}
