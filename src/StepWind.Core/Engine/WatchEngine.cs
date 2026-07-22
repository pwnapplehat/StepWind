using System.Collections.Concurrent;
using System.IO;
using StepWind.Core.Storage;

namespace StepWind.Core.Engine;

/// <summary>Snapshot of engine activity for the UI/logs.</summary>
public sealed record EngineStatus(int WatchedRoots, int PendingChanges, long VersionsCaptured, DateTime? LastCaptureUtc, int LockedFiles);

/// <summary>
/// The folder time-machine orchestrator: watches configured roots with FileSystemWatcher,
/// debounces bursts, applies <see cref="PathExclusions"/>, and captures a new version through
/// the <see cref="VersionStore"/> whenever a real file settles. A dispatcher-free timer drains
/// the debouncer, so it runs headless inside the service.
///
/// Two day-one reliability choices live here:
///   • capture is best-effort per file — one unreadable/locked file never stops the others,
///     and locked files are retried on the next quiet cycle (and on the startup reconcile);
///     an exclusively-locked file is not force-snapshotted, so its version lands when the app
///     releases it (a per-file "locked" status is surfaced in the UI rather than pretended);
///   • the engine records the last change time so the service can persist a cursor and,
///     combined with the USN catch-up, never miss changes that happened while it was down.
///
/// A newly CREATED file is captured on a fast path (a short, dedicated drain) rather than only
/// after the full debounce quiet period — otherwise a file created and deleted within the quiet
/// window would leave NO stored version at all, so a delete could not be undone. Content is only
/// ever read while the file exists; a delete never triggers a (futile, already-gone) read.
/// </summary>
public sealed class WatchEngine : IDisposable
{
    private readonly VersionStore _store;
    private readonly PathExclusions _exclusions;
    private readonly ChangeDebouncer _debouncer;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly List<string> _roots;
    private readonly System.Threading.Timer _drainTimer;
    private readonly System.Threading.Timer _createTimer;
    // path → when it was first seen created. Bounds retry and de-dupes repeat Created events.
    private readonly ConcurrentDictionary<string, DateTime> _pendingCreates = new(StringComparer.OrdinalIgnoreCase);
    // path → when it was first seen exclusively locked (so we can report chronically-locked files).
    private readonly ConcurrentDictionary<string, DateTime> _lockedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string>? _log;

    /// <summary>A file must be locked at least this long before it's reported as "waiting" (not just a transient mid-save lock).</summary>
    private static readonly TimeSpan LockedReportAfter = TimeSpan.FromSeconds(20);

    private long _versionsCaptured; // Interlocked — touched by the drain timer, reconcile, and error-recovery threads
    private DateTime? _lastCaptureUtc;
    private volatile bool _disposed;

    /// <summary>How often the create fast-path checks whether a new file has settled.</summary>
    private static readonly TimeSpan CreateBaselineInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// A new file must have been quiet for at least this long (and be non-empty) before the fast
    /// path baselines it — so we never capture a half-written or zero-byte file as a "version".
    /// </summary>
    private static readonly TimeSpan CreateStability = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// Stop the create fast-path from chasing a file forever: after this long the normal debounce
    /// owns it. Bounds the window we close to roughly [CreateStability, this] before a delete.
    /// </summary>
    private static readonly TimeSpan CreateGiveUp = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Optional gate consulted before every capture. When it returns false (e.g. the disk is
    /// nearly full — see <see cref="Storage.StorageGuard"/>) captures are skipped, not stored,
    /// so StepWind never fills the drive. Skipped changes are re-captured by the reconcile pass
    /// once the gate reopens, so nothing is lost — only deferred.
    /// </summary>
    private readonly Func<bool>? _canCapture;

    /// <summary>Read live so toggling "respect .gitignore" in Settings takes effect without a rebuild.</summary>
    private readonly Func<bool>? _respectGitIgnore;
    private readonly ConcurrentDictionary<string, GitIgnoreMatcher?> _gitIgnore = new(StringComparer.OrdinalIgnoreCase);

    public WatchEngine(VersionStore store, PathExclusions exclusions, IEnumerable<string> roots,
        Action<string>? log = null, TimeSpan? quietPeriod = null, Func<bool>? canCapture = null,
        Func<bool>? respectGitIgnore = null)
    {
        _store = store;
        _exclusions = exclusions;
        _log = log;
        _canCapture = canCapture;
        _respectGitIgnore = respectGitIgnore;
        _roots = [.. roots.Where(Directory.Exists)];
        _debouncer = new ChangeDebouncer { QuietPeriod = quietPeriod ?? TimeSpan.FromSeconds(2) };

        // Never version our own store, wherever it lives.
        _exclusions.ExcludePrefix(Path.GetFullPath(store.Blobs.Root));

        foreach (string root in _roots)
        {
            StartWatching(root);
        }

        _drainTimer = new System.Threading.Timer(_ => Drain(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        _createTimer = new System.Threading.Timer(_ => DrainCreates(), null, CreateBaselineInterval, CreateBaselineInterval);
    }

    public EngineStatus Status => new(_roots.Count, _debouncer.PendingCount, _versionsCaptured, _lastCaptureUtc, LockedFileCount());

    /// <summary>How many files are currently held open by another program (locked past the transient window).</summary>
    private int LockedFileCount()
    {
        DateTime cutoff = DateTime.UtcNow - LockedReportAfter;
        int n = 0;
        foreach (KeyValuePair<string, DateTime> kv in _lockedPaths)
        {
            if (kv.Value <= cutoff)
            {
                n++;
            }
        }

        return n;
    }

    /// <summary>Up to <paramref name="max"/> names of files currently locked past the transient window (for the UI).</summary>
    public IReadOnlyList<string> LockedSample(int max)
    {
        DateTime cutoff = DateTime.UtcNow - LockedReportAfter;
        return [.. _lockedPaths.Where(kv => kv.Value <= cutoff).OrderBy(kv => kv.Value)
            .Select(kv => Path.GetFileName(kv.Key)).Take(max)];
    }

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
        w.Created += OnCreated;
        w.Renamed += (_, e) => OnPath(e.FullPath);
        w.Deleted += OnDeleted;
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

    /// <summary>
    /// A brand-new file. Queue it for a PROMPT baseline capture (see <see cref="DrainCreates"/>)
    /// AND debounce it like a change — the debounced pass captures the final content once the
    /// file settles (a create is often followed immediately by writes), while the prompt baseline
    /// guarantees at least one restorable version exists before a fast create→delete can erase it.
    /// </summary>
    private void OnCreated(object sender, FileSystemEventArgs e)
    {
        string fullPath = e.FullPath;
        if (_exclusions.IsExcludedByDirectory(fullPath) || _exclusions.IsExcludedByExtension(fullPath)
            || _exclusions.IsUnderExcludedPrefix(fullPath))
        {
            return;
        }

        _pendingCreates.TryAdd(fullPath, DateTime.UtcNow);
        _debouncer.Touch(fullPath, DateTime.UtcNow);
    }

    /// <summary>
    /// A delete. There is nothing to capture — the bytes are already gone — so we never attempt
    /// a (futile) read here; the file's previously stored versions remain fully restorable, which
    /// is what powers timeline delete-undo. The whole-machine flight recorder is what surfaces
    /// the delete itself on the timeline. We simply stop tracking the path as a pending change.
    /// </summary>
    private void OnDeleted(object sender, FileSystemEventArgs e)
    {
        _debouncer.Forget(e.FullPath);
        _pendingCreates.TryRemove(e.FullPath, out _);
        _lockedPaths.TryRemove(e.FullPath, out _); // gone — no longer waiting on a lock
    }

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

    /// <summary>
    /// Fast path for newly created files: once a new file has been briefly quiet (so we don't
    /// grab a half-written file), baseline it — best-effort — so a create→delete inside the
    /// normal debounce window can't leave a file with zero stored versions. A file is only
    /// baselined here ONCE (then removed); the normal debounce still captures the settled result.
    /// Deduplication collapses the two when content is unchanged, so it costs nothing normally.
    /// </summary>
    private void DrainCreates()
    {
        if (_disposed || _pendingCreates.IsEmpty)
        {
            return;
        }

        DateTime now = DateTime.UtcNow;
        foreach (KeyValuePair<string, DateTime> kv in _pendingCreates)
        {
            if (_disposed)
            {
                return;
            }

            TimeSpan age = now - kv.Value;
            if (age < CreateStability)
            {
                continue; // let it settle a moment first — avoid capturing a partial write
            }

            if (!_pendingCreates.TryRemove(kv.Key, out _))
            {
                continue; // another tick took it
            }

            // Past the give-up horizon the normal debounce owns it; don't double-capture.
            if (age <= CreateGiveUp)
            {
                TryCapture(kv.Key, reason: "create");
            }
        }
    }

    /// <summary>Captures one path if it still exists and passes exclusions. Never throws.</summary>
    public bool TryCapture(string fullPath) => TryCapture(fullPath, reason: "change");

    /// <summary>Captures one path with an explicit reason label. Never throws.</summary>
    public bool TryCapture(string fullPath, string reason)
    {
        try
        {
            if (_disposed || !File.Exists(fullPath))
            {
                return false; // engine torn down, or deleted before it settled
            }

            if (_canCapture is not null && !_canCapture())
            {
                return false; // storage paused (e.g. disk nearly full) — reconcile re-captures later
            }

            // Read attributes via the extended-length path so a deep (>260-char) file is versioned
            // rather than throwing PathTooLong; exclusions/relative-path use the plain path.
            var info = new FileInfo(Storage.LongPath.Of(fullPath));
            if (!_exclusions.ShouldVersion(fullPath, info.Attributes, info.Length))
            {
                return false;
            }

            if (IsGitIgnored(fullPath))
            {
                return false;
            }

            string? rel = RelativeToRoot(fullPath);
            if (rel is null)
            {
                return false;
            }

            _store.Capture(fullPath, rel, reason);
            Interlocked.Increment(ref _versionsCaptured);
            _lastCaptureUtc = DateTime.UtcNow;
            _lockedPaths.TryRemove(fullPath, out _); // it was capturable — no longer locked
            return true;
        }
        catch (IOException)
        {
            _lockedPaths.TryAdd(fullPath, DateTime.UtcNow); // remember since when it's been locked
            // The file is mid-write or exclusively locked right now. We open with full share
            // (read/write/delete), so the common case — apps that keep a shared read handle
            // (Office, most editors) — already succeeds; this path is the minority that holds
            // an exclusive lock (e.g. an open Outlook PST). Requeue: it captures on the next
            // quiet cycle, and at the latest when the app releases the file (and on the startup
            // reconcile pass). Exclusively-locked files are NOT force-snapshotted (no VSS here) —
            // the version lands when the lock clears; the UI shows the file's protection status.
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

                if (_canCapture is not null && !_canCapture())
                {
                    _log?.Invoke($"catch-up paused (storage) after {captured} file(s)");
                    return captured;
                }

                try
                {
                    if (_exclusions.IsExcludedByDirectory(path) || _exclusions.IsExcludedByExtension(path)
                        || _exclusions.IsUnderExcludedPrefix(path))
                    {
                        continue;
                    }

                    var info = new FileInfo(Storage.LongPath.Of(path));
                    if (!_exclusions.ShouldVersion(path, info.Attributes, info.Length))
                    {
                        continue;
                    }

                    if (IsGitIgnored(path))
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

    /// <summary>
    /// True if the user opted into honoring .gitignore AND the file sits in a git repo whose
    /// root .gitignore ignores it. Matchers are cached per repo root. Any hiccup = not ignored
    /// (fail-open: we'd rather over-protect than silently skip a file).
    /// </summary>
    private bool IsGitIgnored(string fullPath)
    {
        if (_respectGitIgnore is null || !_respectGitIgnore())
        {
            return false;
        }

        string? repoRoot = Storage.GitInfo.RepoRoot(fullPath);
        if (repoRoot is null)
        {
            return false;
        }

        GitIgnoreMatcher? matcher = _gitIgnore.GetOrAdd(repoRoot, static r => GitIgnoreMatcher.ForRepo(r));
        if (matcher is null)
        {
            return false;
        }

        string rel = Path.GetRelativePath(repoRoot, fullPath).Replace('\\', '/');
        return matcher.IsIgnored(rel);
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
        _createTimer.Dispose();
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
