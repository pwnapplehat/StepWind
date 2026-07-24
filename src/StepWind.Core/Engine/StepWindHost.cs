using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using StepWind.Core.Diffing;
using StepWind.Core.Ipc;
using StepWind.Core.Journal;
using StepWind.Core.Storage;

namespace StepWind.Core.Engine;

/// <summary>
/// The service's brain: owns the version store, the folder watch engine, the whole-machine
/// flight recorder, and the retention schedule, and answers IPC commands from the GUI. Keeps
/// the two halves of the product (op timeline + folder history) behind one small API so the
/// unelevated GUI never needs privileges of its own.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class StepWindHost : IDisposable
{
    private readonly StepWindSettings _settings;
    private readonly IBlobCodec _codec;
    private VersionStore _store; // swappable: store relocation rebuilds it at the new root
    private volatile bool _relocating;
    private FlightRecorder? _flightRecorder; // swapped live by the settings toggle
    private readonly System.Threading.Timer _retentionTimer;
    private readonly System.Threading.Timer _storageTimer;
    private readonly Action<string>? _log;
    private readonly RealFileSystemActions _fs = new();
    private readonly object _watchLock = new();
    // Guards the RootOwners map so the concurrent pipe server's parallel authorization reads
    // can't race a SetSettings write (Dictionary is not safe for concurrent read+write).
    private readonly object _ownersLock = new();
    private readonly MigratingBlobCodec? _migCodec;
    private readonly CancellationTokenSource _lifetime = new();
    private StorageGuard _storage;
    private volatile bool _reEncoding;
    private volatile StorageState _storageState = new(false, null, -1, 0, 0, 0);
    private volatile Updates.PendingUpdate? _pendingUpdate;
    private WatchEngine _watch;

    public StepWindHost(StepWindSettings settings, IBlobCodec codec, Action<string>? log = null)
    {
        _settings = settings;
        _log = log;
        _codec = codec;
        _migCodec = codec as MigratingBlobCodec; // live encryption toggling needs this codec

        // The store holds copies of the user's documents under %ProgramData% — lock it to
        // SYSTEM + Administrators before writing anything (no-op for dev/console runs).
        StoreAcl.Harden(settings.StoreRoot, log);

        _store = new VersionStore(
            new BlobStore(settings.StoreRoot, codec),
            new VersionLog(System.IO.Path.Combine(settings.StoreRoot, "versions.jsonl"),
                BuildIndexCipher(settings.StoreRoot), encryptOnWrite: settings.EncryptionEnabled && settings.EncryptIndex));
        _store.Blobs.CleanTemp(); // drop any half-written blobs from a previous crash

        _storage = new StorageGuard(settings.StoreRoot, settings.MinFreeDiskBytes, settings.MaxStoreBytes);
        RefreshStorageState();

        // A crash mid-re-encode leaves the marker dirty; the mixed store is fully readable
        // regardless, and this resumes the pass so it converges to the target format.
        if (_migCodec is not null && ReadCodecState().EndsWith(":dirty", StringComparison.Ordinal))
        {
            _log?.Invoke("resuming interrupted store re-encode");
            StartReEncode();
        }

        EnsureRootIds(); // stable namespaces BEFORE the watch engine maps any path into the store
        _watch = BuildWatch();
        BackfillRootOwners();

        if (settings.FlightRecorderEnabled)
        {
            string? error = TryStartFlightRecorder();
            if (error is not null)
            {
                _log?.Invoke("flight recorder unavailable: " + error);
            }
        }

        // Catch up on anything that changed while the service was stopped (background so
        // startup isn't blocked by a large first-run scan). A quick (shallow) integrity check
        // runs first so a damaged store is noticed and reported at startup, not at restore time.
        WatchEngine watchForReconcile = _watch;
        _ = Task.Run(() =>
        {
            try
            {
                VerifyReport report = _store.RunExclusive(() => StoreMaintenance.Verify(_store.Log, _store.Blobs, deep: false));
                if (report.UnrestorableVersions > 0 || report.OrphanBlobs > 0)
                {
                    _log?.Invoke($"store check: {report.UnrestorableVersions} damaged version(s), {report.OrphanBlobs} orphan blob(s) " +
                                 "— run a repair from Settings to clean up.");
                }
            }
            catch (Exception ex) { _log?.Invoke("startup store check failed: " + ex.Message); }

            try { watchForReconcile.Reconcile(); }
            catch (Exception ex) { _log?.Invoke("catch-up failed: " + ex.Message); }
        });

        // Retention + GC daily (and once shortly after start).
        _retentionTimer = new System.Threading.Timer(_ => RunRetention(), null,
            TimeSpan.FromMinutes(5), TimeSpan.FromHours(24));

        // Watch storage headroom every 30s so a pause (or its release) is noticed quickly, and
        // an emergency prune runs the moment the disk gets tight — not only on the daily pass.
        _storageTimer = new System.Threading.Timer(_ => MonitorStorage(), null,
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// Records a verified-but-unsigned update the service has staged, so the GUI can offer a
    /// one-click install (with normal UAC). Called by the service's update loop.
    /// </summary>
    public void SetPendingUpdate(Updates.PendingUpdate? update) => _pendingUpdate = update;

    /// <summary>Recomputes the storage pause state from current free space + store size.</summary>
    private void RefreshStorageState()
    {
        long storeBytes = _store.Blobs.TotalBytes + SafeFileLength(System.IO.Path.Combine(_settings.StoreRoot, "versions.jsonl"));
        _storageState = _storage.Evaluate(storeBytes);
    }

    /// <summary>
    /// Periodic storage watchdog. On a low-space/over-quota transition it logs loudly and runs an
    /// emergency retention prune to try to win space back; when space returns, capturing resumes
    /// automatically (a reconcile catches up anything missed while paused).
    /// </summary>
    private void MonitorStorage()
    {
        bool wasPaused = _storageState.Paused;
        RefreshStorageState();

        if (_storageState.Paused && !wasPaused)
        {
            _log?.Invoke("STORAGE PAUSE: " + _storageState.Reason);
            try
            {
                RunRetention(); // emergency prune — free space by thinning old versions now
                RefreshStorageState();
            }
            catch (Exception ex)
            {
                _log?.Invoke("emergency prune failed: " + ex.Message);
            }
        }
        else if (!_storageState.Paused && wasPaused)
        {
            _log?.Invoke("storage recovered — capturing resumed");
            // Catch up anything that changed while paused.
            WatchEngine watch = _watch;
            _ = Task.Run(() =>
            {
                try { watch.Reconcile(_lifetime.Token); }
                catch (Exception ex) { _log?.Invoke("post-pause catch-up failed: " + ex.Message); }
            });
        }
    }

    /// <summary>
    /// Builds the index cipher for READING encrypted index lines whenever a store key exists — even
    /// if index encryption is currently off — so turning it off (or a mixed file) never orphans
    /// history. Returns null when there's no key (encryption never enabled) or on any error.
    /// </summary>
    private IIndexCipher? BuildIndexCipher(string storeRoot)
    {
        try
        {
            if (!File.Exists(System.IO.Path.Combine(storeRoot, "store.key")))
            {
                return null;
            }

            return new BlobCodecIndexCipher(new AesGcmBlobCodec(KeyProtector.LoadOrCreate(storeRoot)));
        }
        catch (Exception ex)
        {
            _log?.Invoke("index cipher unavailable: " + ex.Message);
            return null;
        }
    }

    /// <summary>Starts the flight recorder (USN + ETW). Returns null on success, else the reason.</summary>
    private string? TryStartFlightRecorder()
    {
        if (_flightRecorder is not null)
        {
            return null;
        }

        try
        {
            _flightRecorder = new FlightRecorder(_settings.StoreRoot, FixedJournalCandidates(), log: _log,
                ignorePrefixes: new[] { StepWindSettings.DefaultRoot, _settings.StoreRoot });
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private void StopFlightRecorder()
    {
        FlightRecorder? fr = _flightRecorder;
        _flightRecorder = null;
        try
        {
            fr?.Dispose();
        }
        catch (Exception ex)
        {
            _log?.Invoke("flight recorder stop: " + ex.Message);
        }
    }

    /// <summary>
    /// Gives existing (pre-authorization) installs a reasonable owner for each already-protected
    /// folder, so upgrading doesn't lock the current user out of their own history. Owner is the
    /// folder's on-disk NTFS owner SID when readable; otherwise the root is left unscoped (empty
    /// owners) and the live "can the caller read this folder" check governs access. Only fills
    /// gaps — never overwrites an owner set already recorded when a folder was added.
    /// </summary>
    private void BackfillRootOwners()
    {
        foreach (string root in _settings.WatchedFolders)
        {
            string ns = NamespaceOf(root);
            if (_settings.RootOwners.ContainsKey(ns))
            {
                continue;
            }

            var owners = new List<string>();
            try
            {
                var sid = new DirectoryInfo(root).GetAccessControl()
                    .GetOwner(typeof(System.Security.Principal.SecurityIdentifier)) as System.Security.Principal.SecurityIdentifier;
                if (sid is not null)
                {
                    owners.Add(sid.Value);
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"root-owner backfill for '{root}': {ex.Message} (left unscoped)");
            }

            lock (_ownersLock)
            {
                _settings.RootOwners[ns] = owners;
            }
        }
    }

    /// <summary>Creates a watch engine from the current settings (exclusions applied).</summary>
    private WatchEngine BuildWatch()
    {
        var exclusions = new PathExclusions { MaxFileBytes = _settings.MaxFileBytes };
        foreach (string prefix in _settings.ExcludedPrefixes)
        {
            exclusions.ExcludePrefix(prefix);
        }

        // Build the root → namespace map through NamespaceOf so every folder (including one just
        // added via settings) has a stable id by the time the engine maps its first path.
        var namespaces = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in _settings.WatchedFolders)
        {
            namespaces[root] = NamespaceOf(root);
        }

        return new WatchEngine(_store, exclusions, _settings.WatchedFolders, _log,
            canCapture: () => !_storageState.Paused && !_relocating,
            respectGitIgnore: () => _settings.RespectGitIgnore,
            rootNamespaces: namespaces);
    }

    /// <summary>
    /// In-process / CLI entry point: full trust inside the engine's own boundary. Real pipe
    /// connections use the <see cref="CallerContext"/> overload so every privileged or private
    /// action is authorized against the connected user's identity.
    /// </summary>
    public IpcResponse Handle(IpcRequest request) => Handle(request, CallerContext.LocalTrusted);

    public IpcResponse Handle(IpcRequest request, CallerContext caller)
    {
        try
        {
            return request.Command switch
            {
                IpcCommand.Ping => IpcResponse.Success("\"pong\""),
                IpcCommand.GetStatus => Ok(BuildStatus(caller)),
                IpcCommand.GetTimeline => Ok(BuildTimeline(request.Limit, caller)),
                IpcCommand.GetHistory => GetHistory(request.Arg1 ?? "", caller),
                IpcCommand.GetRecentFiles => Ok(BuildRecentFiles(request.Limit, caller)),
                IpcCommand.ReverseOperation => ReverseOperation(request.Arg1 ?? "", caller),
                IpcCommand.ReverseBatch => ReverseBatch(request.Arg1 ?? "", caller),
                IpcCommand.RelocateStore => RequirePrivilege(caller) ?? RelocateStore(request.Arg1 ?? ""),
                IpcCommand.RestoreVersion => RestoreVersion(request.Arg1 ?? "", request.Arg2, caller),
                IpcCommand.RunRetention => RequirePrivilege(caller) ?? RunRetentionCommand(),
                IpcCommand.GetSettings => Ok(BuildSettings()),
                IpcCommand.SetSettings => ApplySettings(request.Arg1 ?? "", caller),
                IpcCommand.PurgeHistory => PurgeHistory(request.Arg1 ?? "", caller),
                IpcCommand.BrowseVersions => Ok(BrowseVersions(request.Arg1 ?? "", request.Arg2, request.Limit, caller)),
                IpcCommand.GetVersionContent => GetVersionContent(request.Arg1 ?? "", caller),
                IpcCommand.DiffVersions => DiffVersionsCommand(request.Arg1 ?? "", request.Arg2 ?? "", caller),
                IpcCommand.CaptureNow => CaptureNowCommand(request.Arg1 ?? "", caller),
                IpcCommand.VerifyStore => VerifyStoreCommand(request.Arg1), // read-only health check
                IpcCommand.RepairStore => RequirePrivilege(caller) ?? RepairStoreCommand(request.Arg1),
                _ => IpcResponse.Fail("unsupported command"),
            };
        }
        catch (UnauthorizedAccessException ex)
        {
            return IpcResponse.Fail(ex.Message);
        }
        catch (Exception ex)
        {
            return IpcResponse.Fail(ex.Message);
        }
    }

    private static IpcResponse? RequirePrivilege(CallerContext caller) =>
        caller.IsPrivileged ? null : IpcResponse.Fail("This action requires an administrator.");

    // ── root namespaces: stable per-root store ids ───────────────────────────────────────────
    // A stored relative path's FIRST SEGMENT is its root namespace. Historically that was the
    // folder leaf ("Documents"), which made two same-named folders impossible to protect at
    // once. Namespaces are now assigned per root and remembered in settings: existing roots keep
    // their leaf (zero data migration), and a new root whose leaf is taken — by a live root, a
    // removed root's mapping, or dead history in the store — gets a deterministic "leaf~hash8".
    // Ownership is recorded per namespace; a namespace maps back to its live folder path so the
    // "can the caller actually read this folder" safety net can run.

    /// <summary>The store namespace for a protected root (assigning one if it's new).</summary>
    private string NamespaceOf(string root)
    {
        if (_settings.RootIds.TryGetValue(root, out string? ns))
        {
            return ns;
        }

        return EnsureRootId(root);
    }

    /// <summary>
    /// Assigns namespaces to every watched folder that lacks one. This is the STARTUP/UPGRADE
    /// pass, so it lets a folder CLAIM a store segment matching its own leaf: pre-RootIds
    /// installs stored that folder's history under exactly that segment, and refusing it here
    /// would orphan every existing user's history on upgrade. (Two watched folders can't have
    /// shared a leaf before RootIds existed — the old code refused the second one — so the claim
    /// is unambiguous; the first mapped folder wins and any later one is suffixed via Values.)
    /// </summary>
    private void EnsureRootIds()
    {
        bool changed = false;
        foreach (string root in _settings.WatchedFolders)
        {
            if (!_settings.RootIds.ContainsKey(root))
            {
                EnsureRootId(root, saveSettings: false, claimStoreSegments: true);
                changed = true;
            }
        }

        if (changed)
        {
            _settings.Save();
        }
    }

    private string EnsureRootId(string root, bool saveSettings = true, bool claimStoreSegments = false)
    {
        lock (_ownersLock)
        {
            if (_settings.RootIds.TryGetValue(root, out string? existing))
            {
                return existing;
            }

            // Taken namespaces: every mapped id (live or kept-after-removal). For a folder added
            // ONLINE (post-upgrade), first segments already present in the store are also taken —
            // dead history must never be silently adopted by an unrelated folder that happens to
            // share its name. The startup pass claims instead (see EnsureRootIds).
            var taken = new HashSet<string>(_settings.RootIds.Values, StringComparer.OrdinalIgnoreCase);
            if (!claimStoreSegments)
            {
                taken.UnionWith(_store.Log.DistinctRootSegments());
            }

            string leaf = System.IO.Path.GetFileName(root.TrimEnd(System.IO.Path.DirectorySeparatorChar)) is { Length: > 0 } l ? l : "root";
            string ns = leaf;
            if (taken.Contains(ns))
            {
                // Deterministic, path-derived suffix — stable across restarts and reinstalls.
                string hex = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(root.ToLowerInvariant()))).ToLowerInvariant();
                for (int len = 8; taken.Contains(ns) && len <= hex.Length; len += 4)
                {
                    ns = $"{leaf}~{hex[..len]}";
                }
            }

            _settings.RootIds[root] = ns;
            if (saveSettings)
            {
                _settings.Save();
            }

            return ns;
        }
    }

    private string? RootPathForSegment(string firstSegment)
    {
        foreach (string root in _settings.WatchedFolders)
        {
            if (string.Equals(NamespaceOf(root), firstSegment, StringComparison.OrdinalIgnoreCase))
            {
                return root;
            }
        }

        return null;
    }

    /// <summary>A thread-safe snapshot of the recorded owner SIDs for a root segment (or null if none).</summary>
    private IReadOnlyList<string>? OwnersSnapshot(string firstSegment)
    {
        lock (_ownersLock)
        {
            return _settings.RootOwners.TryGetValue(firstSegment, out List<string>? owners) ? [.. owners] : null;
        }
    }

    private bool CallerCanAccessSegment(CallerContext caller, string firstSegment)
        => RootAccess.CanAccess(caller, OwnersSnapshot(firstSegment), RootPathForSegment(firstSegment));

    private bool CallerCanAccessRelative(CallerContext caller, string relativePath)
        => CallerCanAccessSegment(caller, FirstSegment(NormalizeRel(relativePath)));

    private void EnsureCanAccessRelative(CallerContext caller, string relativePath)
    {
        if (!CallerCanAccessRelative(caller, relativePath))
        {
            throw new UnauthorizedAccessException("You don't have access to this file's history.");
        }
    }

    private object BuildStatus(CallerContext caller)
    {
        WatchEngine watch = _watch;
        EngineStatus s = watch.Status;
        List<string> folders = AccessibleFolders(caller);
        StorageState storage = _storageState;
        Updates.PendingUpdate? update = _pendingUpdate;
        return new
        {
            FlightRecorder = _flightRecorder is not null,
            WatchedRoots = folders.Count,
            s.PendingChanges,
            s.VersionsCaptured,
            s.LastCaptureUtc,
            s.LockedFiles,
            LockedSample = watch.LockedSample(5),
            TotalVersions = _store.Log.All.Count,
            StoreBytes = _store.Blobs.TotalBytes + SafeFileLength(System.IO.Path.Combine(_settings.StoreRoot, "versions.jsonl")),
            ReEncoding = _reEncoding,
            CapturePaused = storage.Paused,
            PauseReason = storage.Reason,
            FreeDiskBytes = storage.FreeBytes,
            UpdateReadyVersion = update?.Version,
            UpdateReadyPath = update?.SetupPath,
            Volumes = BuildVolumeCoverage(),
            WatchedFolders = folders,
        };
    }

    /// <summary>
    /// Which drives the whole-machine timeline actually covers. The flight recorder monitors
    /// fixed NTFS volumes only; removable, network, ReFS and exFAT drives are NOT recorded — this
    /// makes that honest in the UI instead of a silent gap the user discovers the hard way.
    /// </summary>
    private object[] BuildVolumeCoverage()
    {
        FlightRecorder? recorder = _flightRecorder;
        var active = new HashSet<string>(recorder?.ActiveVolumes ?? [], StringComparer.OrdinalIgnoreCase);
        var list = new List<object>();
        foreach (DriveInfo d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady)
                {
                    continue;
                }

                string name = d.Name.TrimEnd('\\');
                bool monitored = active.Contains(name); // the honest truth: is the journal actually being read
                bool couldJournal = d.DriveType == DriveType.Fixed && d.DriveFormat is "NTFS" or "ReFS";
                string note = monitored
                    ? "Covered by the timeline"
                    : couldJournal
                        ? (recorder is null ? "Flight recorder is off" : "No change journal on this drive — protect a folder here for version history")
                        : $"{d.DriveType} {d.DriveFormat}: not on the timeline — protect a folder here for version history";
                list.Add(new
                {
                    Name = name,
                    FileSystem = d.DriveFormat,
                    Type = d.DriveType.ToString(),
                    Monitored = monitored,
                    Note = note,
                });
            }
            catch
            {
                // a drive that vanished mid-enumeration — skip it
            }
        }

        return [.. list];
    }

    /// <summary>The watched folders this caller may see (all of them for a privileged caller).</summary>
    private List<string> AccessibleFolders(CallerContext caller)
    {
        if (caller.IsPrivileged)
        {
            return _settings.WatchedFolders;
        }

        return [.. _settings.WatchedFolders.Where(root =>
            RootAccess.CanAccess(caller, OwnersSnapshot(NamespaceOf(root)), root))];
    }

    private static long SafeFileLength(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; } catch { return 0; }
    }

    private List<TimelineEntry> BuildTimeline(int limit, CallerContext caller)
    {
        FlightRecorder? recorder = _flightRecorder; // local copy — the toggle can swap it
        if (recorder is null)
        {
            return [];
        }

        WatchEngine watch = _watch; // local — ApplySettings can swap it under _watchLock
        var access = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase); // per-call dir cache
        var entries = new List<TimelineEntry>();
        foreach (FileOperation op in recorder.Recent(limit))
        {
            // Privacy + integrity: a non-privileged caller only sees operations whose file lives
            // somewhere they can access, so they never learn about — nor receive a reverse handle
            // for — another user's file activity. This closes the cross-user timeline-read AND the
            // cross-user "undo someone else's move as SYSTEM" window in one place: no handle out,
            // no reverse in. It deliberately still shows the user their OWN moves anywhere on disk
            // (the whole point of the flight recorder), not just inside protected folders.
            if (!CallerCanSeeOperation(caller, op, watch, access))
            {
                continue;
            }

            entries.Add(new TimelineEntry
            {
                Kind = op.Kind.ToString(),
                TimestampUtc = op.TimestampUtc,
                Name = op.Name,
                OldPath = op.OldPath,
                NewPath = op.NewPath,
                ByProcess = op.ByProcess,
                Reversible = op.IsReversible,
                OperationId = FlightRecorder.OpToken(op),
                RecoverableVersionId = RecoverableVersionFor(op, watch),
            });
        }

        return entries;
    }

    /// <summary>
    /// Whether a caller may see a whole-machine operation. Privileged callers see everything;
    /// others see an operation when its file is one they can reach — either inside a protected
    /// folder they own, or a path their own token can read (so a user still sees their OWN moves
    /// anywhere on disk, but never another user's activity). <paramref name="dirAccess"/> caches
    /// the per-directory impersonated read check for the duration of one timeline build.
    /// </summary>
    private bool CallerCanSeeOperation(CallerContext caller, FileOperation op, WatchEngine watch,
        Dictionary<string, bool> dirAccess)
    {
        if (caller.IsPrivileged)
        {
            return true;
        }

        foreach (string? path in new[] { op.NewPath, op.OldPath })
        {
            if (string.IsNullOrEmpty(path))
            {
                continue;
            }

            // Owned protected folder — cheap, and works even for a deleted file (path-only).
            string? rel = watch.RelativeToRoot(path);
            if (rel is not null && CallerCanAccessRelative(caller, rel))
            {
                return true;
            }

            // Otherwise: can the caller's own token read the file's folder? (Cached per dir.)
            string? dir = TryGetDirectory(path);
            if (dir is null)
            {
                continue;
            }

            if (!dirAccess.TryGetValue(dir, out bool canRead))
            {
                canRead = RootAccess.CallerCanReadDirectory(caller, dir);
                dirAccess[dir] = canRead;
            }

            if (canRead)
            {
                return true;
            }
        }

        return false;
    }

    private static string? TryGetDirectory(string path)
    {
        try { return System.IO.Path.GetDirectoryName(path); }
        catch { return null; }
    }

    /// <summary>
    /// For a Delete whose file lived in a protected folder and has stored history, returns the
    /// VersionId of its latest saved version so the timeline can offer one-click delete-undo. A
    /// move/rename is undone by moving the item back (no stored content needed) — this is only
    /// for deletes, where the bytes are gone and recovery must come from the version store.
    /// Returns null when there's nothing to recover, so the GUI can be honest instead of showing
    /// a dead button.
    /// </summary>
    private string? RecoverableVersionFor(FileOperation op, WatchEngine watch)
    {
        if (op.Kind != OperationKind.Delete || op.IsDirectory || string.IsNullOrEmpty(op.OldPath))
        {
            return null;
        }

        return RecoverableVersionIdFor(op.OldPath, watch);
    }

    /// <summary>
    /// The VersionId of the latest saved version of the file at <paramref name="absolutePath"/>,
    /// or null if it wasn't inside a protected folder or has no history. Factored out so the
    /// delete-undo mapping is unit-testable without a live flight recorder (which needs admin/ETW).
    /// </summary>
    internal string? RecoverableVersionIdFor(string absolutePath) => RecoverableVersionIdFor(absolutePath, _watch);

    private string? RecoverableVersionIdFor(string absolutePath, WatchEngine watch)
    {
        string? rel = watch.RelativeToRoot(absolutePath);
        if (rel is null)
        {
            return null; // not inside any currently-protected folder
        }

        FileVersion? latest = _store.Log.LatestFor(NormalizeRel(rel));
        return latest is null ? null : $"{latest.RelativePath}|{latest.CapturedUtc.Ticks}";
    }

    private object BuildSettings() => new
    {
        _settings.WatchedFolders,
        _settings.ExcludedPrefixes,
        _settings.FlightRecorderEnabled,
        _settings.AutoUpdateEnabled,
        _settings.EncryptionEnabled,
        _settings.EncryptIndex,
        _settings.FirstRunCompleted,
        _settings.TimelineProtectedOnly,
        _settings.RespectGitIgnore,
        _settings.MinFreeDiskBytes,
        _settings.MaxStoreBytes,
        RetentionKeepAllHours = _settings.Retention.KeepAllHours,
        RetentionHourlyDays = _settings.Retention.HourlyDays,
        RetentionDailyDays = _settings.Retention.DailyDays,
        RetentionMaxAgeDays = _settings.Retention.MaxAgeDays,
        RetentionMaxVersionsPerFile = _settings.Retention.MaxVersionsPerFile,
    };

    /// <summary>Editable slice of settings the GUI can push (WatchedFolders is the main one).</summary>
    private sealed class SettingsPatch
    {
        public List<string>? WatchedFolders { get; set; }
        public List<string>? ExcludedPrefixes { get; set; }
        public bool? AutoUpdateEnabled { get; set; }
        public bool? EncryptionEnabled { get; set; }
        public bool? EncryptIndex { get; set; }
        public bool? FlightRecorderEnabled { get; set; }
        public bool? TimelineProtectedOnly { get; set; }
        public bool? RespectGitIgnore { get; set; }
        public long? MinFreeDiskBytes { get; set; }
        public long? MaxStoreBytes { get; set; }
        public int? RetentionKeepAllHours { get; set; }
        public int? RetentionHourlyDays { get; set; }
        public int? RetentionDailyDays { get; set; }
        public int? RetentionMaxAgeDays { get; set; }
        public int? RetentionMaxVersionsPerFile { get; set; }
    }

    private IpcResponse ApplySettings(string json, CallerContext caller)
    {
        SettingsPatch? patch = JsonSerializer.Deserialize<SettingsPatch>(json);
        if (patch is null)
        {
            return IpcResponse.Fail("bad settings payload");
        }

        if (patch.EncryptionEnabled is bool enc && enc != _settings.EncryptionEnabled)
        {
            if (_migCodec is null)
            {
                return IpcResponse.Fail("encryption can't be changed in this configuration");
            }

            try
            {
                _migCodec.SetEncryptNew(enc); // creates/loads the DPAPI-sealed key on enable
            }
            catch (Exception ex)
            {
                return IpcResponse.Fail("could not " + (enc ? "enable" : "disable") + " encryption: " + ex.Message);
            }

            _settings.EncryptionEnabled = enc;
            _log?.Invoke($"encryption {(enc ? "enabled" : "disabled")} — re-encoding existing history in the background");
            StartReEncode();
        }

        bool foldersChanged = false;
        if (patch.WatchedFolders is not null)
        {
            // Keep only real, existing, de-duplicated directories.
            var cleaned = patch.WatchedFolders
                .Where(p => !string.IsNullOrWhiteSpace(p) && System.IO.Directory.Exists(p))
                .Select(p => p.TrimEnd('\\', '/'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            IpcResponse? denied = AuthorizeFolderChange(caller, _settings.WatchedFolders, cleaned);
            if (denied is not null)
            {
                return denied;
            }

            foldersChanged = !cleaned.SequenceEqual(_settings.WatchedFolders, StringComparer.OrdinalIgnoreCase);
            RegisterOwnership(caller, _settings.WatchedFolders, cleaned);
            _settings.WatchedFolders = cleaned;

            // A human decided the folder set (add, remove, or remove-all). From here on the
            // GUI must respect it and never auto-seed defaults back in.
            _settings.FirstRunCompleted = true;
        }

        if (patch.ExcludedPrefixes is not null)
        {
            _settings.ExcludedPrefixes = [.. patch.ExcludedPrefixes];
            foldersChanged = true;
        }

        if (patch.AutoUpdateEnabled is bool au)
        {
            _settings.AutoUpdateEnabled = au;
        }

        if (patch.FlightRecorderEnabled is bool fre && fre != _settings.FlightRecorderEnabled)
        {
            if (fre)
            {
                // Honest failure: if ETW/USN can't start (no privileges in a dev/console run),
                // say so and leave the setting off rather than pretending.
                string? error = TryStartFlightRecorder();
                if (error is not null)
                {
                    return IpcResponse.Fail("could not start the flight recorder: " + error);
                }
            }
            else
            {
                StopFlightRecorder();
            }

            _settings.FlightRecorderEnabled = fre;
            _log?.Invoke($"flight recorder {(fre ? "started" : "stopped")}");
        }

        if (patch.TimelineProtectedOnly is bool tpo)
        {
            _settings.TimelineProtectedOnly = tpo;
        }

        if (patch.RespectGitIgnore is bool rgi)
        {
            _settings.RespectGitIgnore = rgi; // read live by the watch engine — no rebuild needed
        }

        if (patch.EncryptIndex is bool ei && ei != _settings.EncryptIndex)
        {
            // Persisted now; the index cipher's write mode is fixed at store construction, so this
            // takes effect at the next service start (existing encrypted lines stay readable either
            // way, and a later retention rewrite converges the file to the new mode).
            _settings.EncryptIndex = ei;
            _log?.Invoke($"index encryption {(ei ? "enabled" : "disabled")} — applies on next service restart");
        }

        bool storageChanged = false;
        if (patch.MinFreeDiskBytes is long minFree && minFree != _settings.MinFreeDiskBytes)
        {
            _settings.MinFreeDiskBytes = Math.Max(0, minFree);
            storageChanged = true;
        }

        if (patch.MaxStoreBytes is long maxStore && maxStore != _settings.MaxStoreBytes)
        {
            _settings.MaxStoreBytes = Math.Max(0, maxStore);
            storageChanged = true;
        }

        if (storageChanged)
        {
            _storage = new StorageGuard(_settings.StoreRoot, _settings.MinFreeDiskBytes, _settings.MaxStoreBytes);
            RefreshStorageState();
        }

        // Retention is user-configurable; clamp to sane floors so a typo can't nuke history
        // (0 everywhere would garbage-collect everything on the next pass).
        if (patch.RetentionKeepAllHours is int kah)
        {
            _settings.Retention.KeepAllHours = Math.Clamp(kah, 1, 24 * 365);
        }

        if (patch.RetentionHourlyDays is int hd)
        {
            _settings.Retention.HourlyDays = Math.Clamp(hd, 0, 365);
        }

        if (patch.RetentionDailyDays is int dd)
        {
            _settings.Retention.DailyDays = Math.Clamp(dd, 0, 3650);
        }

        if (patch.RetentionMaxAgeDays is int mad)
        {
            _settings.Retention.MaxAgeDays = Math.Clamp(mad, 1, 36500);
        }

        if (patch.RetentionMaxVersionsPerFile is int mv)
        {
            _settings.Retention.MaxVersionsPerFile = Math.Clamp(mv, 1, 100_000);
        }

        _settings.Save();

        if (foldersChanged)
        {
            WatchEngine rebuilt;
            lock (_watchLock)
            {
                WatchEngine old = _watch;
                _watch = BuildWatch(); // start watching the new set…
                old.Dispose();          // …then tear down the old watchers
                rebuilt = _watch;
            }

            _log?.Invoke($"watched folders updated ({_settings.WatchedFolders.Count})");

            // A newly added folder needs a baseline immediately (its files existed before we
            // watched it), so reconcile in the background rather than waiting for edits.
            _ = Task.Run(() =>
            {
                try { rebuilt.Reconcile(); }
                catch (Exception ex) { _log?.Invoke("folder catch-up failed: " + ex.Message); }
            });
        }

        return Ok(BuildSettings());
    }

    private IpcResponse GetHistory(string pathOrRelative, CallerContext caller)
    {
        // Accept either a store-relative path ("Documents/report.txt") or an absolute file
        // path (from the GUI's Browse button) — resolve the latter against the watched roots.
        string relativePath = pathOrRelative;
        if (pathOrRelative.Contains(':') || pathOrRelative.StartsWith('\\'))
        {
            relativePath = _watch.RelativeToRoot(pathOrRelative) ?? pathOrRelative;
        }

        EnsureCanAccessRelative(caller, relativePath);
        return Ok(BuildHistory(relativePath));
    }

    /// <summary>
    /// Guards a watched-folder change against privilege escalation:
    ///  • a non-privileged caller may only ADD a folder its own token can read (else it could
    ///    use the SYSTEM service to capture — then read — files it has no rights to);
    ///  • a non-privileged caller may only REMOVE a folder it owns (no un-protecting someone
    ///    else's folder).
    /// Same-name folders are NOT refused anymore: stable per-root namespaces (see
    /// <see cref="EnsureRootId"/>) give a colliding leaf its own store segment, so two
    /// "Documents" from different drives keep fully separate histories.
    /// Returns a failure response to reject the change, or null to allow it.
    /// </summary>
    private IpcResponse? AuthorizeFolderChange(CallerContext caller, List<string> oldFolders, List<string> newFolders)
    {
        var oldSet = new HashSet<string>(oldFolders, StringComparer.OrdinalIgnoreCase);
        var newSet = new HashSet<string>(newFolders, StringComparer.OrdinalIgnoreCase);

        foreach (string added in newFolders.Where(f => !oldSet.Contains(f)))
        {
            if (!caller.IsPrivileged && !RootAccess.CallerCanReadDirectory(caller, added))
            {
                return IpcResponse.Fail($"Can't protect '{added}': you don't have permission to read that folder.");
            }
        }

        if (!caller.IsPrivileged)
        {
            foreach (string removed in oldFolders.Where(f => !newSet.Contains(f)))
            {
                if (!RootAccess.CanAccess(caller, OwnersSnapshot(NamespaceOf(removed)), removed))
                {
                    return IpcResponse.Fail($"Can't stop protecting '{removed}': it belongs to another user.");
                }
            }
        }

        return null;
    }

    /// <summary>Records the caller as owner of newly added roots (so only they, or an admin, can read/purge that history).</summary>
    private void RegisterOwnership(CallerContext caller, List<string> oldFolders, List<string> newFolders)
    {
        if (caller.UserSid is not { Length: > 0 } sid)
        {
            return; // in-process/unknown caller — nothing to attribute
        }

        var oldSet = new HashSet<string>(oldFolders, StringComparer.OrdinalIgnoreCase);
        foreach (string added in newFolders.Where(f => !oldSet.Contains(f)))
        {
            string ns = NamespaceOf(added); // assigns a stable namespace on first sight (takes _ownersLock itself)
            lock (_ownersLock)
            {
                if (!_settings.RootOwners.TryGetValue(ns, out List<string>? owners))
                {
                    owners = [];
                    _settings.RootOwners[ns] = owners;
                }

                if (!owners.Contains(sid, StringComparer.OrdinalIgnoreCase))
                {
                    owners.Add(sid);
                }
            }
        }
    }

    private List<VersionEntry> BuildHistory(string relativePath)
    {
        var list = new List<VersionEntry>();
        foreach (FileVersion v in _store.Log.History(relativePath).OrderByDescending(v => v.CapturedUtc))
        {
            list.Add(new VersionEntry
            {
                RelativePath = v.RelativePath,
                CapturedUtc = v.CapturedUtc,
                Size = v.Size,
                Reason = v.Reason,
                VersionId = $"{v.RelativePath}|{v.CapturedUtc.Ticks}",
                GitRef = v.GitRef,
            });
        }

        return list;
    }

    /// <summary>
    /// Folder-navigable browse of the version store. With a search query, returns matching
    /// FILES anywhere beneath <paramref name="prefix"/> (flat, most-recent first). Without one,
    /// returns the IMMEDIATE children of the prefix: subfolders (with counts aggregated over
    /// everything beneath) then files. Root ("") lists the top-level protected folders that
    /// actually have history. Purely a read over the version log — cheap and lock-snapshotted.
    /// </summary>
    private List<BrowseEntry> BrowseVersions(string prefix, string? query, int limit, CallerContext caller)
    {
        string basePrefix = prefix.Replace('\\', '/').Trim('/');
        int cap = limit <= 0 ? 500 : Math.Min(limit, 2000);

        // Latest version per distinct file, restricted to what's under the prefix AND to the
        // roots this caller may access — so browse/search never reveals another user's files.
        var files = _store.Log.All
            .GroupBy(v => v.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Path: g.Key, Count: g.Count(), Last: g.Max(v => v.CapturedUtc)))
            .Where(f => basePrefix.Length == 0
                || f.Path.Equals(basePrefix, StringComparison.OrdinalIgnoreCase)
                || f.Path.StartsWith(basePrefix + "/", StringComparison.OrdinalIgnoreCase))
            .Where(f => CallerCanAccessRelative(caller, f.Path))
            .ToList();

        if (!string.IsNullOrWhiteSpace(query))
        {
            string q = query.Trim();
            return [.. files
                .Where(f => f.Path.Contains(q, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.Last)
                .Take(cap)
                .Select(f => new BrowseEntry
                {
                    Name = LeafName(f.Path),
                    RelativePath = f.Path,
                    IsFolder = false,
                    VersionCount = f.Count,
                    LastCapturedUtc = f.Last,
                })];
        }

        int depth = basePrefix.Length == 0 ? 0 : basePrefix.Split('/').Length;
        var folders = new Dictionary<string, (int Versions, int Files, DateTime Last)>(StringComparer.OrdinalIgnoreCase);
        var directFiles = new List<BrowseEntry>();

        foreach ((string path, int count, DateTime last) in files)
        {
            string[] segs = path.Split('/');
            if (segs.Length == depth + 1)
            {
                directFiles.Add(new BrowseEntry
                {
                    Name = segs[^1],
                    RelativePath = path,
                    IsFolder = false,
                    VersionCount = count,
                    LastCapturedUtc = last,
                });
            }
            else if (segs.Length > depth + 1)
            {
                string childName = segs[depth];
                string childPath = string.Join('/', segs.Take(depth + 1));
                (int v, int fc, DateTime l) = folders.GetValueOrDefault(childPath, (0, 0, DateTime.MinValue));
                folders[childPath] = (v + count, fc + 1, last > l ? last : l);
            }
        }

        var result = new List<BrowseEntry>();
        result.AddRange(folders
            .OrderByDescending(kv => kv.Value.Last)
            .Select(kv => new BrowseEntry
            {
                Name = depth == 0 ? DisplayNameForSegment(kv.Key) : LeafName(kv.Key),
                RelativePath = kv.Key,
                IsFolder = true,
                VersionCount = kv.Value.Versions,
                FileCount = kv.Value.Files,
                LastCapturedUtc = kv.Value.Last,
            }));
        result.AddRange(directFiles.OrderByDescending(f => f.LastCapturedUtc));
        return [.. result.Take(cap)];
    }

    /// <summary>
    /// Human name for a TOP-LEVEL store segment. For a suffixed namespace ("Documents~a1b2c3d4")
    /// the real folder's leaf is shown with its parent for disambiguation — "Documents (D:\Backup)"
    /// — so two same-named roots stay tellable-apart in every browse UI. Dead segments (folder no
    /// longer protected) fall back to the raw segment.
    /// </summary>
    private string DisplayNameForSegment(string segment)
    {
        if (RootPathForSegment(segment) is { } root)
        {
            string leaf = System.IO.Path.GetFileName(root.TrimEnd(System.IO.Path.DirectorySeparatorChar)) is { Length: > 0 } l ? l : root;
            if (!string.Equals(segment, leaf, StringComparison.OrdinalIgnoreCase))
            {
                string? parent = System.IO.Path.GetDirectoryName(root.TrimEnd(System.IO.Path.DirectorySeparatorChar));
                return string.IsNullOrEmpty(parent) ? leaf : $"{leaf} ({parent})";
            }

            return leaf;
        }

        return segment;
    }

    private static string LeafName(string relativePath)
    {
        int slash = relativePath.LastIndexOf('/');
        return slash < 0 ? relativePath : relativePath[(slash + 1)..];
    }

    // ── AI/MCP read + diff + checkpoint surface ─────────────────────────────────────────
    // Deliberately read-only + additive: nothing here can delete history or change settings.
    // An AI agent editing files in a protected folder gets the time machine (see what
    // changed, restore, checkpoint before a risky edit) but never the shredder.

    /// <summary>
    /// Cap on the bytes read back for a single GetVersionContent/DiffVersions side. Generous
    /// for source/config/doc files (the realistic AI-agent use case); large or binary files
    /// report their size honestly instead of paying to read/return megabytes of content the
    /// caller almost certainly can't use anyway. Matches UnifiedDiff's own line-count cap in
    /// spirit — keep any single MCP call fast, since the pipe serves one connection at a time.
    /// </summary>
    private const long MaxContentBytes = 4 * 1024 * 1024;

    private sealed class ResolvedContent
    {
        public string RelativePath = "";
        public string Label = "";
        public DateTime WhenUtc;
        public long Size;
        public bool TooLarge;
        public bool IsBinary;
        public byte[]? Bytes;
    }

    /// <summary>
    /// Resolves a content/diff selector. Three forms:
    ///   "relpath|ticks"    an exact captured version (the VersionId shape used elsewhere)
    ///   "latest:relpath"   the most recently captured version of that file
    ///   "current:relpath"  the live bytes on disk right now — lets a caller ask "what have I
    ///                       changed since the last checkpoint" without needing a VersionId
    /// Throws (caught by Handle's outer try/catch → a clean IpcResponse.Fail) on anything that
    /// doesn't resolve, so every failure mode reaches the caller as one readable error string.
    /// </summary>
    private ResolvedContent ResolveSelector(string selector, CallerContext caller)
    {
        if (string.IsNullOrWhiteSpace(selector))
        {
            throw new ArgumentException("empty selector");
        }

        if (selector.StartsWith("current:", StringComparison.OrdinalIgnoreCase))
        {
            string rel = NormalizeRel(selector[8..]);
            EnsureCanAccessRelative(caller, rel);
            string absolute = ResolveOriginalPath(rel, caller);
            if (!File.Exists(absolute))
            {
                throw new FileNotFoundException($"no current file at '{rel}' (looked for {absolute})");
            }

            var info = new FileInfo(absolute);
            var current = new ResolvedContent
            {
                RelativePath = rel,
                Label = $"{rel} (current on disk, {info.LastWriteTimeUtc:yyyy-MM-dd HH:mm:ss} UTC)",
                WhenUtc = info.LastWriteTimeUtc,
                Size = info.Length,
            };

            if (info.Length > MaxContentBytes)
            {
                current.TooLarge = true;
                return current;
            }

            // Shared read: never blocks whatever app currently has the file open.
            byte[] liveBytes = ReadAllBytesShared(absolute);
            current.Bytes = liveBytes;
            current.IsBinary = UnifiedDiff.LooksBinary(liveBytes);
            return current;
        }

        FileVersion version;
        if (selector.StartsWith("latest:", StringComparison.OrdinalIgnoreCase))
        {
            string rel = NormalizeRel(selector[7..]);
            EnsureCanAccessRelative(caller, rel);
            version = _store.Log.LatestFor(rel) ?? throw new FileNotFoundException($"no saved history for '{rel}'");
        }
        else
        {
            int sep = selector.LastIndexOf('|');
            if (sep <= 0 || !long.TryParse(selector[(sep + 1)..], out long ticks))
            {
                throw new ArgumentException(
                    $"unrecognized selector '{selector}' — expected 'relpath|ticks', 'latest:relpath', or 'current:relpath'");
            }

            string rel = selector[..sep];
            EnsureCanAccessRelative(caller, rel);
            version = _store.Log.History(rel).FirstOrDefault(v => v.CapturedUtc.Ticks == ticks)
                ?? throw new FileNotFoundException($"version not found: {selector}");
        }

        var resolved = new ResolvedContent
        {
            RelativePath = version.RelativePath,
            Label = $"{version.RelativePath} @ {version.CapturedUtc:yyyy-MM-dd HH:mm:ss} UTC ({version.Reason})",
            WhenUtc = version.CapturedUtc,
            Size = version.Size,
        };

        if (version.Size > MaxContentBytes)
        {
            resolved.TooLarge = true;
            return resolved;
        }

        using var ms = new MemoryStream();
        _store.WriteContent(version, ms); // re-hashes + verifies every chunk as it reads
        byte[] contentBytes = ms.ToArray();
        resolved.Bytes = contentBytes;
        resolved.IsBinary = UnifiedDiff.LooksBinary(contentBytes);
        return resolved;
    }

    private static byte[] ReadAllBytesShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var ms = new MemoryStream();
        fs.CopyTo(ms);
        return ms.ToArray();
    }

    private static string NormalizeRel(string rel) => rel.Replace('\\', '/').TrimStart('/');

    private IpcResponse GetVersionContent(string selector, CallerContext caller)
    {
        ResolvedContent r = ResolveSelector(selector, caller);
        var result = new ContentResult
        {
            RelativePath = r.RelativePath,
            Label = r.Label,
            WhenUtc = r.WhenUtc,
            Size = r.Size,
            IsBinary = r.IsBinary,
            Truncated = r.TooLarge,
            Content = r is { TooLarge: false, IsBinary: false, Bytes: not null } ? DecodeText(r.Bytes) : null,
        };
        return Ok(result);
    }

    private IpcResponse DiffVersionsCommand(string oldSelector, string newSelector, CallerContext caller)
    {
        ResolvedContent oldSide = ResolveSelector(oldSelector, caller);
        ResolvedContent newSide = ResolveSelector(newSelector, caller);

        string diffText;
        bool binary = oldSide.IsBinary || newSide.IsBinary;
        if (binary)
        {
            diffText = oldSide.Size == newSide.Size
                ? $"(binary file, size unchanged: {oldSide.Size:N0} bytes — content may still differ)"
                : $"(binary file: {oldSide.Size:N0} -> {newSide.Size:N0} bytes)";
        }
        else if (oldSide.TooLarge || newSide.TooLarge)
        {
            diffText = $"(file too large to diff: {oldSide.Size:N0} vs {newSide.Size:N0} bytes, cap is {MaxContentBytes:N0})";
        }
        else
        {
            string oldText = DecodeText(oldSide.Bytes!);
            string newText = DecodeText(newSide.Bytes!);
            diffText = UnifiedDiff.Diff(oldText, newText, oldSide.Label, newSide.Label);
        }

        return Ok(new DiffResult
        {
            OldLabel = oldSide.Label,
            NewLabel = newSide.Label,
            OldSize = oldSide.Size,
            NewSize = newSide.Size,
            Binary = binary,
            Diff = diffText,
        });
    }

    private static string DecodeText(byte[] bytes) => new UTF8Encoding(false).GetString(bytes);

    /// <summary>
    /// Forces an immediate capture of one file — a "checkpoint" — instead of waiting for the
    /// watcher's debounce window. Meant for an AI agent about to make a risky edit: checkpoint
    /// first, edit, then DiffVersions("latest:path","current:path") to see exactly what changed
    /// and RestoreVersion to undo if it went wrong. Accepts an absolute path (must resolve
    /// under a watched root) or an already-relative "Folder/name.ext" path.
    /// </summary>
    private IpcResponse CaptureNowCommand(string pathOrRelative, CallerContext caller)
    {
        string relative;
        string absolute;
        if (pathOrRelative.Contains(':') || pathOrRelative.StartsWith('\\'))
        {
            absolute = pathOrRelative;
            relative = _watch.RelativeToRoot(absolute)
                ?? throw new ArgumentException($"'{absolute}' is not inside any protected folder");
        }
        else
        {
            relative = NormalizeRel(pathOrRelative);
            absolute = ResolveOriginalPath(relative, caller);
        }

        EnsureCanAccessRelative(caller, relative);

        if (!File.Exists(absolute))
        {
            return IpcResponse.Fail($"file not found: {absolute}");
        }

        FileVersion version = _store.Capture(absolute, relative, reason: "checkpoint");
        return Ok(new VersionEntry
        {
            RelativePath = version.RelativePath,
            CapturedUtc = version.CapturedUtc,
            Size = version.Size,
            Reason = version.Reason,
            VersionId = $"{version.RelativePath}|{version.CapturedUtc.Ticks}",
        });
    }

    /// <summary>Distinct protected files with history, most-recently-changed first (quick list).</summary>
    private List<RecentFileEntry> BuildRecentFiles(int limit, CallerContext caller)
    {
        return [.. _store.Log.All
            .GroupBy(v => v.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Where(g => CallerCanAccessRelative(caller, g.Key))
            .Select(g => new RecentFileEntry
            {
                RelativePath = g.Key,
                LastCapturedUtc = g.Max(v => v.CapturedUtc),
                VersionCount = g.Count(),
            })
            .OrderByDescending(f => f.LastCapturedUtc)
            .Take(limit <= 0 ? 100 : limit)];
    }

    private IpcResponse ReverseOperation(string opId, CallerContext caller)
    {
        // Unforgeable: opId is an opaque handle into the flight recorder's in-memory ring, NOT
        // the operation's data. The server re-derives the real paths from its OWN entry, so a
        // client can never craft "move THIS to THERE as SYSTEM". An unknown/expired handle just
        // matches nothing.
        FileRecorderLookup lookup = FindOperation(opId);
        if (lookup.Op is null)
        {
            return IpcResponse.Fail("operation not found (it may have aged out of the timeline)");
        }

        // A non-privileged caller may only reverse operations touching files they can reach —
        // this is what stops one local user undoing another user's move via the SYSTEM service.
        if (!CallerCanSeeOperation(caller, lookup.Op, _watch, new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)))
        {
            return IpcResponse.Fail("You don't have access to reverse this operation.");
        }

        ReverseResult r = OperationReverser.Reverse(lookup.Op, _fs);
        return r.Success ? IpcResponse.Success(JsonSerializer.Serialize(new { r.Message, r.RestoredPath }))
                         : IpcResponse.Fail(r.Message);
    }

    /// <summary>
    /// Reverses many operations in one call — undoing a bulk move/rename (e.g. Explorer moved 200
    /// files) without 200 clicks. Each item is attempted independently and reported per-item, so a
    /// partial failure never silently stops the rest: the caller learns exactly what was and wasn't
    /// undone. Same authorization as single undo — a caller can only reverse operations it can see.
    /// </summary>
    private IpcResponse ReverseBatch(string json, CallerContext caller)
    {
        string[]? tokens;
        try
        {
            tokens = JsonSerializer.Deserialize<string[]>(json);
        }
        catch
        {
            return IpcResponse.Fail("bad batch payload");
        }

        if (tokens is null || tokens.Length == 0)
        {
            return IpcResponse.Fail("no operations to undo");
        }

        var access = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var results = new List<object>(tokens.Length);
        int succeeded = 0;

        foreach (string token in tokens)
        {
            FileOperation? op = _flightRecorder?.Find(token);
            if (op is null)
            {
                results.Add(new { Token = token, Success = false, Message = "operation not found (it may have aged out of the timeline)" });
                continue;
            }

            if (!CallerCanSeeOperation(caller, op, _watch, access))
            {
                results.Add(new { Token = token, Success = false, Message = "You don't have access to reverse this operation." });
                continue;
            }

            ReverseResult r = OperationReverser.Reverse(op, _fs);
            if (r.Success)
            {
                succeeded++;
            }

            results.Add(new { Token = token, Success = r.Success, Message = r.Message });
        }

        return Ok(new { Total = tokens.Length, Succeeded = succeeded, Failed = tokens.Length - succeeded, Results = results });
    }

    private readonly record struct FileRecorderLookup(FileOperation? Op);

    private FileRecorderLookup FindOperation(string opId)
    {
        FlightRecorder? recorder = _flightRecorder;
        return new FileRecorderLookup(recorder?.Find(opId));
    }

    private IpcResponse RestoreVersion(string versionId, string? destinationOverride, CallerContext caller)
    {
        int sep = versionId.LastIndexOf('|');
        if (sep <= 0 || !long.TryParse(versionId[(sep + 1)..], out long ticks))
        {
            return IpcResponse.Fail("bad version id");
        }

        string rel = versionId[..sep];
        EnsureCanAccessRelative(caller, rel);

        FileVersion? version = _store.Log.History(rel).FirstOrDefault(v => v.CapturedUtc.Ticks == ticks);
        if (version is null)
        {
            return IpcResponse.Fail("version not found");
        }

        // destinationOverride is honored ONLY for a privileged/in-process caller. A pipe client
        // must not be able to steer a SYSTEM write to an arbitrary path — the restore always
        // lands back under the file's own root (or, if that's gone, a location the caller owns).
        string dest = caller.IsPrivileged && !string.IsNullOrEmpty(destinationOverride)
            ? destinationOverride
            : ResolveOriginalPath(rel, caller);
        string written = _store.RestoreToSafePath(version, dest);
        return IpcResponse.Success(JsonSerializer.Serialize(new { RestoredPath = written }));
    }

    private string ResolveOriginalPath(string relativePath, CallerContext caller)
    {
        // relativePath is "<rootNamespace>/rest…"; map back to the actual root.
        int slash = relativePath.IndexOf('/');
        string firstSeg = slash < 0 ? relativePath : relativePath[..slash];
        string rest = slash < 0 ? "" : relativePath[(slash + 1)..];
        if (RootPathForSegment(firstSeg) is { } rootPath)
        {
            return System.IO.Path.Combine(rootPath, rest.Replace('/', System.IO.Path.DirectorySeparatorChar));
        }

        // The folder is no longer protected (or was renamed), so its original root is unknown.
        // Land the recovered file somewhere the RIGHT person can open — never the ACL-locked
        // store, and never world-readable Public Documents for a real (non-privileged) user's
        // private data. A privileged/in-process caller keeps the Public Documents fallback
        // (admins can read everything anyway, and it keeps the CLI/tests behavior stable).
        string baseDir;
        if (caller.IsPrivileged)
        {
            baseDir = Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments);
            if (string.IsNullOrEmpty(baseDir))
            {
                baseDir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "StepWind");
            }
        }
        else
        {
            // Impersonate the caller to land the file in THEIR profile, readable only by them.
            baseDir = CallerProfileDocuments(caller)
                ?? System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "StepWind", "Restored-Pending");
        }

        return System.IO.Path.Combine(baseDir, "StepWind Restored",
            relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
    }

    /// <summary>The connected caller's own Documents folder (resolved under impersonation), or null.</summary>
    private static string? CallerProfileDocuments(CallerContext caller)
    {
        if (caller.PipeStream is null)
        {
            return null;
        }

        try
        {
            string? docs = null;
            caller.PipeStream.RunAsClient(() =>
                docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
            return string.IsNullOrEmpty(docs) ? null : docs;
        }
        catch
        {
            return null;
        }
    }

    private IpcResponse RunRetentionCommand()
    {
        RetentionResult r = RunRetention();
        return IpcResponse.Success(JsonSerializer.Serialize(r));
    }

    /// <summary>
    /// Moves the history store to a new drive/folder. Safety-first: COPY the whole store to the
    /// new location, VERIFY the copy is fully restorable, only THEN switch the live store to it —
    /// and NEVER delete the old store (it's left intact so a relocation can never lose history;
    /// the caller is told where it is to remove later). Captures are paused for the duration.
    /// Admin-only (gated by the dispatcher).
    /// </summary>
    private IpcResponse RelocateStore(string newRootArg)
    {
        if (string.IsNullOrWhiteSpace(newRootArg))
        {
            return IpcResponse.Fail("no destination given");
        }

        string oldRoot = System.IO.Path.GetFullPath(_settings.StoreRoot);
        string newRoot;
        try
        {
            newRoot = System.IO.Path.GetFullPath(newRootArg.Trim());
        }
        catch (Exception ex)
        {
            return IpcResponse.Fail("invalid destination: " + ex.Message);
        }

        if (string.Equals(newRoot, oldRoot, StringComparison.OrdinalIgnoreCase))
        {
            return IpcResponse.Fail("that's already where the history store is.");
        }

        if (IsSubPath(newRoot, oldRoot) || IsSubPath(oldRoot, newRoot))
        {
            return IpcResponse.Fail("the new location can't be inside the current store (or vice versa).");
        }

        foreach (string watched in _settings.WatchedFolders)
        {
            if (IsSubPath(newRoot, watched) || string.Equals(newRoot, System.IO.Path.GetFullPath(watched), StringComparison.OrdinalIgnoreCase))
            {
                return IpcResponse.Fail($"the new location can't be inside a protected folder ('{watched}') — that would version the store itself.");
            }
        }

        if (Directory.Exists(newRoot) && Directory.EnumerateFileSystemEntries(newRoot).Any())
        {
            return IpcResponse.Fail("the new location must be an empty (or new) folder.");
        }

        // Enough room on the target?
        long storeBytes = _store.Blobs.TotalBytes + SafeFileLength(System.IO.Path.Combine(oldRoot, "versions.jsonl"));
        try
        {
            string? drive = System.IO.Path.GetPathRoot(newRoot);
            if (drive is not null)
            {
                long free = new DriveInfo(drive).AvailableFreeSpace;
                if (free < storeBytes + (64L * 1024 * 1024))
                {
                    return IpcResponse.Fail($"not enough free space at the destination ({free / 1024 / 1024} MB free, need ~{storeBytes / 1024 / 1024} MB).");
                }
            }
        }
        catch { /* if we can't tell, let the copy try and fail loudly */ }

        _relocating = true; // stop captures while we copy
        try
        {
            // Copy the whole store with captures + GC held off, so the copy is a consistent snapshot.
            _store.RunExclusive<object?>(() =>
            {
                CopyDirectory(oldRoot, newRoot);
                return null;
            });

            // Verify the COPY before trusting it — every version must reconstruct from the new root.
            var verifyStore = new VersionStore(
                new BlobStore(newRoot, _codec),
                new VersionLog(System.IO.Path.Combine(newRoot, "versions.jsonl"),
                    BuildIndexCipher(newRoot), encryptOnWrite: _settings.EncryptionEnabled && _settings.EncryptIndex));
            VerifyReport report = StoreMaintenance.Verify(verifyStore.Log, verifyStore.Blobs);
            if (report.UnrestorableVersions > 0)
            {
                return IpcResponse.Fail($"the copied store failed verification ({report.UnrestorableVersions} damaged of {report.TotalVersions}); staying on the current store and leaving the copy in place for inspection.");
            }

            StoreAcl.Harden(newRoot, _log);

            // Switch the live store + watch engine to the new root.
            lock (_watchLock)
            {
                _store = verifyStore;
                _settings.StoreRoot = newRoot;
                WatchEngine old = _watch;
                _watch = BuildWatch();
                old.Dispose();
            }

            _settings.Save();
            _storage = new StorageGuard(newRoot, _settings.MinFreeDiskBytes, _settings.MaxStoreBytes);
            RefreshStorageState();

            // The flight recorder ignores the store path so its own churn isn't on the timeline;
            // restart it so it ignores the NEW location (if it was running).
            if (_flightRecorder is not null)
            {
                StopFlightRecorder();
                TryStartFlightRecorder();
            }

            _log?.Invoke($"store relocated to {newRoot}; previous store left intact at {oldRoot}");
            return Ok(new
            {
                NewRoot = newRoot,
                OldRoot = oldRoot,
                report.TotalVersions,
                Message = $"History moved to {newRoot}. The old copy at {oldRoot} was left untouched — delete it yourself once you've confirmed everything's fine.",
            });
        }
        catch (Exception ex)
        {
            return IpcResponse.Fail("relocation failed (staying on the current store): " + ex.Message);
        }
        finally
        {
            _relocating = false;
        }
    }

    private static bool IsSubPath(string candidate, string ancestor)
    {
        string a = System.IO.Path.GetFullPath(ancestor).TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar;
        string c = System.IO.Path.GetFullPath(candidate).TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar;
        return c.StartsWith(a, StringComparison.OrdinalIgnoreCase);
    }

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (string dir in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(dir.Replace(src, dst, StringComparison.OrdinalIgnoreCase));
        }

        foreach (string file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            string target = file.Replace(src, dst, StringComparison.OrdinalIgnoreCase);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private IpcResponse VerifyStoreCommand(string? arg)
    {
        bool deep = string.Equals(arg, "deep", StringComparison.OrdinalIgnoreCase);
        VerifyReport report = _store.RunExclusive(() => StoreMaintenance.Verify(_store.Log, _store.Blobs, deep));
        _log?.Invoke($"store verify ({(deep ? "deep" : "quick")}): {report.OkVersions}/{report.TotalVersions} restorable, " +
                     $"{report.UnrestorableVersions} damaged, {report.OrphanBlobs} orphan blob(s)");
        return Ok(report);
    }

    private IpcResponse RepairStoreCommand(string? arg)
    {
        bool deep = string.Equals(arg, "deep", StringComparison.OrdinalIgnoreCase);
        VerifyReport report = _store.RunExclusive(() => StoreMaintenance.Repair(_store.Log, _store.Blobs, deep));
        RefreshStorageState(); // repair frees space
        _log?.Invoke($"store repair ({(deep ? "deep" : "quick")}): removed {report.RemovedVersions} unrestorable version(s), swept {report.SweptBlobs} blob(s)");
        return Ok(report);
    }

    /// <summary>
    /// Deletes stored versions NOW — the user's data, the user's call. Selector:
    ///   "*"            everything (the whole history store);
    ///   "unprotected"  every version whose folder is no longer in the protected list;
    ///   anything else  a store path prefix — "Desk" purges that whole folder's history,
    ///                  "Desk/note.txt" purges one file's history.
    /// Runs exclusively with captures/GC, then sweeps unreferenced blobs so the disk space
    /// actually comes back. Purged versions are unrecoverable — the GUI confirms first.
    /// </summary>
    private IpcResponse PurgeHistory(string selector, CallerContext caller)
    {
        if (string.IsNullOrWhiteSpace(selector))
        {
            return IpcResponse.Fail("nothing to purge — empty selector");
        }

        // Machine-wide destruction is privileged-only. "*" wipes everyone's history; "unprotected"
        // sweeps every user's orphaned history — neither is a non-admin's to trigger.
        if (selector == "*" || selector.Equals("unprotected", StringComparison.OrdinalIgnoreCase))
        {
            if (!caller.IsPrivileged)
            {
                return IpcResponse.Fail("Purging all history requires an administrator.");
            }
        }
        else
        {
            // Targeted prefix purge: only for a root the caller can access (their own history).
            EnsureCanAccessRelative(caller, selector.Replace('\\', '/').TrimStart('/'));
        }

        (int removedVersions, int sweptBlobs) = _store.RunExclusive(() =>
        {
            IReadOnlyList<FileVersion> all = _store.Log.All;
            List<FileVersion> keep;

            if (selector == "*")
            {
                keep = [];
            }
            else if (selector.Equals("unprotected", StringComparison.OrdinalIgnoreCase))
            {
                var protectedNames = new HashSet<string>(
                    _settings.WatchedFolders.Select(System.IO.Path.GetFileName).OfType<string>(),
                    StringComparer.OrdinalIgnoreCase);
                keep = [.. all.Where(v => protectedNames.Contains(FirstSegment(v.RelativePath)))];
            }
            else
            {
                string prefix = selector.Replace('\\', '/').TrimStart('/').TrimEnd('/');
                keep = [.. all.Where(v =>
                    !v.RelativePath.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                    && !v.RelativePath.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))];
            }

            int removed = all.Count - keep.Count;
            if (removed > 0)
            {
                _store.Log.Rewrite(keep);
            }

            int swept = Retention.Sweep(_store.Log, _store.Blobs);
            return (removed, swept);
        });

        _log?.Invoke($"purge '{selector}': removed {removedVersions} version(s), swept {sweptBlobs} blob(s)");
        return IpcResponse.Success(JsonSerializer.Serialize(new { RemovedVersions = removedVersions, SweptBlobs = sweptBlobs }));
    }

    private static string FirstSegment(string relativePath)
    {
        int slash = relativePath.IndexOf('/');
        return slash < 0 ? relativePath : relativePath[..slash];
    }

    // ── Store re-encode (encryption toggle) ────────────────────────────────────────────
    // The dirty/clean marker survives crashes: dirty means "some blobs may still be in the
    // old format", which is harmless for reads (Get accepts both) but must eventually
    // converge — so a dirty marker at startup resumes the pass.

    private string CodecStatePath => System.IO.Path.Combine(_settings.StoreRoot, "codec.state");

    private string ReadCodecState()
    {
        try { return File.Exists(CodecStatePath) ? File.ReadAllText(CodecStatePath) : ""; }
        catch { return ""; }
    }

    private void WriteCodecState(bool dirty)
    {
        try
        {
            File.WriteAllText(CodecStatePath,
                (_settings.EncryptionEnabled ? "cipher" : "plain") + (dirty ? ":dirty" : ":clean"));
        }
        catch (Exception ex)
        {
            _log?.Invoke("codec state write failed: " + ex.Message);
        }
    }

    private void StartReEncode()
    {
        WriteCodecState(dirty: true);
        _reEncoding = true;
        CancellationToken ct = _lifetime.Token;
        _ = Task.Run(() =>
        {
            try
            {
                int converted = _store.Blobs.ReEncodeAll(ct);
                WriteCodecState(dirty: false);
                _log?.Invoke($"store re-encode complete — {converted} blob(s) converted to {(_settings.EncryptionEnabled ? "encrypted" : "plain")} format");
            }
            catch (OperationCanceledException)
            {
                // service stopping mid-pass — the dirty marker resumes it next start
            }
            catch (Exception ex)
            {
                _log?.Invoke("store re-encode failed (will retry next start): " + ex.Message);
            }
            finally
            {
                _reEncoding = false;
            }
        }, CancellationToken.None);
    }

    private RetentionResult RunRetention()
    {
        // Exclusive with captures: GC's mark-and-sweep must not race a capture's
        // write-blob→append-version window (see VersionStore._maintenanceGate).
        RetentionResult r = _store.RunExclusive(() =>
        {
            _store.Log.Backup(); // refresh the known-good index snapshot alongside the daily pass
            return Retention.Apply(_store.Log, _store.Blobs, _settings.Retention, DateTime.UtcNow);
        });
        _log?.Invoke($"retention: kept {r.VersionsKept}/{r.VersionsBefore} versions, swept {r.BlobsSwept} blobs");
        return r;
    }

    private static IpcResponse Ok(object payload) => IpcResponse.Success(JsonSerializer.Serialize(payload));

    /// <summary>
    /// Fixed volumes whose filesystem MIGHT expose a USN change journal — NTFS always, and ReFS
    /// (Dev Drive) which supports one on recent Windows. The flight recorder tries each and skips
    /// any that don't actually have a journal (errors are per-volume, so a ReFS volume without one
    /// is harmless), and only reports the ones it's truly reading via ActiveVolumes.
    /// </summary>
    private static IEnumerable<string> FixedJournalCandidates()
    {
        foreach (DriveInfo d in DriveInfo.GetDrives())
        {
            bool candidate = false;
            try { candidate = d.DriveType == DriveType.Fixed && d.IsReady && d.DriveFormat is "NTFS" or "ReFS"; } catch { }
            if (candidate)
            {
                yield return d.Name.TrimEnd('\\');
            }
        }
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _retentionTimer.Dispose();
        _storageTimer.Dispose();
        lock (_watchLock)
        {
            _watch.Dispose();
        }

        StopFlightRecorder();
        _lifetime.Dispose();
    }

    private sealed class RealFileSystemActions : IFileSystemActions
    {
        public bool Exists(string path) => System.IO.File.Exists(path);
        public bool DirectoryExists(string path) => System.IO.Directory.Exists(path);
        public void Move(string from, string to)
        {
            if (System.IO.Directory.Exists(from))
            {
                System.IO.Directory.Move(from, to);
            }
            else
            {
                System.IO.File.Move(from, to);
            }
        }
    }
}
