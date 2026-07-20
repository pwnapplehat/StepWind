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

    private long _versionsCaptured; // Interlocked — touched by the drain timer, reconcile, and error-recovery threads
    private DateTime? _lastCaptureUtc;
    private volatile bool _disposed;

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
            // A generous buffer reduces overflow under bursts (bulk copy, unzip). On overflow
            // the OS drops events, so Error recovery below re-syncs via a reconcile pass.
            InternalBufferSize = 256 * 1024,
        };
        w.Changed += OnChanged;
        w.Created += OnChanged;
        w.Renamed += (_, e) => OnPath(e.FullPath);
        w.Error += (_, e) => OnWatcherError(root, w, e.GetException());
        w.EnableRaisingEvents = true;
        _watchers.Add(w);
    }

    /// <summary>
    /// A watcher can die (buffer overflow under a burst, the root going briefly offline). We
    /// dispose it, rebuild it, and run a reconcile pass so anything missed during the gap is
    /// still captured — a dropped OS event must never mean a silently lost version.
    /// </summary>
    private void OnWatcherError(string root, FileSystemWatcher dead, Exception ex)
    {
        _log?.Invoke($"watcher on {root} errored ({ex.Message}); rebuilding + reconciling");
        try
        {
            dead.EnableRaisingEvents = false;
            _watchers.Remove(dead);
            dead.Dispose();
        }
        catch
        {
            // already gone
        }

        if (_disposed)
        {
            return;
        }

        try
        {
            if (Directory.Exists(root))
            {
                StartWatching(root);
            }
        }
        catch (Exception rebuildEx)
        {
            _log?.Invoke($"watcher rebuild failed for {root}: {rebuildEx.Message}");
        }

        _ = Task.Run(() =>
        {
            try { Reconcile(); } catch { /* best effort */ }
        });
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
        if (_disposed)
        {
            return; // a timer tick already in flight when Dispose ran must not capture
        }

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
            if (_disposed || !File.Exists(fullPath))
            {
                return false; // engine torn down, or deleted before it settled
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
            Interlocked.Increment(ref _versionsCaptured);
            _lastCaptureUtc = DateTime.UtcNow;
            return true;
        }
        catch (IOException)
        {
            // The file is mid-write or exclusively locked right now. We open with full share
            // (read/write/delete), so the common case — apps that keep a shared read handle
            // (Office, most editors) — already succeeds; this path is the minority that holds
            // an exclusive lock (e.g. an open Outlook PST). Requeue: it captures on the next
            // quiet cycle, and at the latest when the app releases the file (and on the startup
            // reconcile pass). Snapshot-based reads of exclusively-locked files are not done.
            _debouncer.Touch(fullPath, DateTime.UtcNow);
            return false;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"capture failed for {fullPath}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Startup catch-up: walks every watched root and captures files that changed (or appeared)
    /// while StepWind wasn't running — comparing each file's last-write time against the newest
    /// version already stored. This is what makes "protection resumes where it left off" true
    /// rather than a claim: without it, anything edited while the service was down would have no
    /// version. Runs on a background thread; content-level dedup in the store means unchanged
    /// files (same bytes, touched mtime) never create a redundant version. Best-effort per file.
    ///
    /// ABORTS THE MOMENT THE ENGINE IS DISPOSED. This pass can grind through a huge folder for
    /// minutes in the background; when the user removes folders, the host disposes this engine
    /// and builds a new one — without the abort, an in-flight baseline kept capturing files of
    /// folders the user had just un-protected ("I removed Desktop but versions keep appearing").
    /// </summary>
    public int Reconcile(CancellationToken ct = default)
    {
        int captured = 0;
        foreach (string root in _roots)
        {
            if (_disposed)
            {
                return captured;
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, "*", new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    RecurseSubdirectories = true,
                    AttributesToSkip = FileAttributes.System,
                });
            }
            catch
            {
                continue;
            }

            foreach (string path in files)
            {
                if (_disposed || ct.IsCancellationRequested)
                {
                    _log?.Invoke($"catch-up aborted (folders changed) after {captured} file(s)");
                    return captured;
                }

                try
                {
                    if (_exclusions.IsExcludedByDirectory(path) || _exclusions.IsExcludedByExtension(path)
                        || _exclusions.IsUnderExcludedPrefix(path))
                    {
                        continue;
                    }

                    var info = new FileInfo(path);
                    if (!_exclusions.ShouldVersion(path, info.Attributes, info.Length))
                    {
                        continue;
                    }

                    string? rel = RelativeToRoot(path);
                    if (rel is null)
                    {
                        continue;
                    }

                    // Skip files whose newest stored version already reflects the current
                    // on-disk write time — nothing changed while we were away.
                    FileVersion? latest = _store.Log.LatestFor(NormalizeRel(rel));
                    if (latest is not null && info.LastWriteTimeUtc <= latest.ModifiedUtc)
                    {
                        continue;
                    }

                    _store.Capture(path, rel, latest is null ? "baseline" : "catch-up");
                    captured++;
                }
                catch
                {
                    // one unreadable file never stops the sweep
                }
            }
        }

        if (captured > 0)
        {
            Interlocked.Add(ref _versionsCaptured, captured);
            _lastCaptureUtc = DateTime.UtcNow;
            _log?.Invoke($"catch-up: captured {captured} changed/new file(s) since last run");
        }

        return captured;
    }

    private static string NormalizeRel(string rel) => rel.Replace('\\', '/').TrimStart('/');

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
        _disposed = true;
        _drainTimer.Dispose();
        foreach (FileSystemWatcher w in _watchers.ToArray())
        {
            try
            {
                w.EnableRaisingEvents = false;
                w.Dispose();
            }
            catch
            {
                // already disposed by error recovery
            }
        }
    }
}
