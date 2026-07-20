using System.Runtime.Versioning;
using System.Text.Json;
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
    private readonly VersionStore _store;
    private readonly FlightRecorder? _flightRecorder;
    private readonly System.Threading.Timer _retentionTimer;
    private readonly Action<string>? _log;
    private readonly RealFileSystemActions _fs = new();
    private readonly object _watchLock = new();
    private readonly MigratingBlobCodec? _migCodec;
    private readonly CancellationTokenSource _lifetime = new();
    private volatile bool _reEncoding;
    private WatchEngine _watch;

    public StepWindHost(StepWindSettings settings, IBlobCodec codec, Action<string>? log = null)
    {
        _settings = settings;
        _log = log;
        _migCodec = codec as MigratingBlobCodec; // live encryption toggling needs this codec

        // The store holds copies of the user's documents under %ProgramData% — lock it to
        // SYSTEM + Administrators before writing anything (no-op for dev/console runs).
        StoreAcl.Harden(settings.StoreRoot, log);

        _store = new VersionStore(
            new BlobStore(settings.StoreRoot, codec),
            new VersionLog(System.IO.Path.Combine(settings.StoreRoot, "versions.jsonl")));
        _store.Blobs.CleanTemp(); // drop any half-written blobs from a previous crash

        // A crash mid-re-encode leaves the marker dirty; the mixed store is fully readable
        // regardless, and this resumes the pass so it converges to the target format.
        if (_migCodec is not null && ReadCodecState().EndsWith(":dirty", StringComparison.Ordinal))
        {
            _log?.Invoke("resuming interrupted store re-encode");
            StartReEncode();
        }

        _watch = BuildWatch();

        if (settings.FlightRecorderEnabled)
        {
            try
            {
                _flightRecorder = new FlightRecorder(settings.StoreRoot, FixedNtfsVolumes(), log: log,
                    ignorePrefixes: new[] { StepWindSettings.DefaultRoot, settings.StoreRoot });
            }
            catch (Exception ex)
            {
                _log?.Invoke("flight recorder unavailable: " + ex.Message);
            }
        }

        // Catch up on anything that changed while the service was stopped (background so
        // startup isn't blocked by a large first-run scan).
        WatchEngine watchForReconcile = _watch;
        _ = Task.Run(() =>
        {
            try { watchForReconcile.Reconcile(); }
            catch (Exception ex) { _log?.Invoke("catch-up failed: " + ex.Message); }
        });

        // Retention + GC daily (and once shortly after start).
        _retentionTimer = new System.Threading.Timer(_ => RunRetention(), null,
            TimeSpan.FromMinutes(5), TimeSpan.FromHours(24));
    }

    /// <summary>Creates a watch engine from the current settings (exclusions applied).</summary>
    private WatchEngine BuildWatch()
    {
        var exclusions = new PathExclusions { MaxFileBytes = _settings.MaxFileBytes };
        foreach (string prefix in _settings.ExcludedPrefixes)
        {
            exclusions.ExcludePrefix(prefix);
        }

        return new WatchEngine(_store, exclusions, _settings.WatchedFolders, _log);
    }

    public IpcResponse Handle(IpcRequest request)
    {
        try
        {
            return request.Command switch
            {
                IpcCommand.Ping => IpcResponse.Success("\"pong\""),
                IpcCommand.GetStatus => Ok(BuildStatus()),
                IpcCommand.GetTimeline => Ok(BuildTimeline(request.Limit)),
                IpcCommand.GetHistory => Ok(BuildHistory(request.Arg1 ?? "")),
                IpcCommand.GetRecentFiles => Ok(BuildRecentFiles(request.Limit)),
                IpcCommand.ReverseOperation => ReverseOperation(request.Arg1 ?? ""),
                IpcCommand.RestoreVersion => RestoreVersion(request.Arg1 ?? "", request.Arg2),
                IpcCommand.RunRetention => RunRetentionCommand(),
                IpcCommand.GetSettings => Ok(BuildSettings()),
                IpcCommand.SetSettings => ApplySettings(request.Arg1 ?? ""),
                IpcCommand.PurgeHistory => PurgeHistory(request.Arg1 ?? ""),
                _ => IpcResponse.Fail("unsupported command"),
            };
        }
        catch (Exception ex)
        {
            return IpcResponse.Fail(ex.Message);
        }
    }

    private object BuildStatus()
    {
        EngineStatus s = _watch.Status;
        return new
        {
            FlightRecorder = _flightRecorder is not null,
            s.WatchedRoots,
            s.PendingChanges,
            s.VersionsCaptured,
            s.LastCaptureUtc,
            TotalVersions = _store.Log.All.Count,
            StoreBytes = _store.Blobs.TotalBytes + SafeFileLength(System.IO.Path.Combine(_settings.StoreRoot, "versions.jsonl")),
            ReEncoding = _reEncoding,
            _settings.WatchedFolders,
        };
    }

    private static long SafeFileLength(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; } catch { return 0; }
    }

    private List<TimelineEntry> BuildTimeline(int limit)
    {
        if (_flightRecorder is null)
        {
            return [];
        }

        var entries = new List<TimelineEntry>();
        foreach (FileOperation op in _flightRecorder.Recent(limit))
        {
            entries.Add(new TimelineEntry
            {
                Kind = op.Kind.ToString(),
                TimestampUtc = op.TimestampUtc,
                Name = op.Name,
                OldPath = op.OldPath,
                NewPath = op.NewPath,
                ByProcess = op.ByProcess,
                Reversible = op.IsReversible,
                OperationId = EncodeOp(op),
            });
        }

        return entries;
    }

    private object BuildSettings() => new
    {
        _settings.WatchedFolders,
        _settings.ExcludedPrefixes,
        _settings.FlightRecorderEnabled,
        _settings.AutoUpdateEnabled,
        _settings.EncryptionEnabled,
        _settings.FirstRunCompleted,
        _settings.TimelineProtectedOnly,
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
        public bool? TimelineProtectedOnly { get; set; }
        public int? RetentionKeepAllHours { get; set; }
        public int? RetentionHourlyDays { get; set; }
        public int? RetentionDailyDays { get; set; }
        public int? RetentionMaxAgeDays { get; set; }
        public int? RetentionMaxVersionsPerFile { get; set; }
    }

    private IpcResponse ApplySettings(string json)
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
            foldersChanged = !cleaned.SequenceEqual(_settings.WatchedFolders, StringComparer.OrdinalIgnoreCase);
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

        if (patch.TimelineProtectedOnly is bool tpo)
        {
            _settings.TimelineProtectedOnly = tpo;
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

    private List<VersionEntry> BuildHistory(string pathOrRelative)
    {
        // Accept either a store-relative path ("Documents/report.txt") or an absolute file
        // path (from the GUI's Browse button) — resolve the latter against the watched roots.
        string relativePath = pathOrRelative;
        if (pathOrRelative.Contains(':') || pathOrRelative.StartsWith('\\'))
        {
            relativePath = _watch.RelativeToRoot(pathOrRelative) ?? pathOrRelative;
        }

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
            });
        }

        return list;
    }

    /// <summary>Distinct protected files with history, most-recently-changed first (quick list).</summary>
    private List<RecentFileEntry> BuildRecentFiles(int limit)
    {
        return [.. _store.Log.All
            .GroupBy(v => v.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(g => new RecentFileEntry
            {
                RelativePath = g.Key,
                LastCapturedUtc = g.Max(v => v.CapturedUtc),
                VersionCount = g.Count(),
            })
            .OrderByDescending(f => f.LastCapturedUtc)
            .Take(limit <= 0 ? 100 : limit)];
    }

    private IpcResponse ReverseOperation(string opId)
    {
        FileOperation? op = DecodeOp(opId);
        if (op is null)
        {
            return IpcResponse.Fail("operation not found");
        }

        ReverseResult r = OperationReverser.Reverse(op, _fs);
        return r.Success ? IpcResponse.Success(JsonSerializer.Serialize(new { r.Message, r.RestoredPath }))
                         : IpcResponse.Fail(r.Message);
    }

    private IpcResponse RestoreVersion(string versionId, string? destinationOverride)
    {
        int sep = versionId.LastIndexOf('|');
        if (sep <= 0 || !long.TryParse(versionId[(sep + 1)..], out long ticks))
        {
            return IpcResponse.Fail("bad version id");
        }

        string rel = versionId[..sep];
        FileVersion? version = _store.Log.History(rel).FirstOrDefault(v => v.CapturedUtc.Ticks == ticks);
        if (version is null)
        {
            return IpcResponse.Fail("version not found");
        }

        // Default destination: back under the watched root it came from.
        string dest = destinationOverride ?? ResolveOriginalPath(rel);
        string written = _store.RestoreToSafePath(version, dest);
        return IpcResponse.Success(JsonSerializer.Serialize(new { RestoredPath = written }));
    }

    private string ResolveOriginalPath(string relativePath)
    {
        // relativePath is "<watchedFolderName>/rest…"; map back to the actual root.
        int slash = relativePath.IndexOf('/');
        string firstSeg = slash < 0 ? relativePath : relativePath[..slash];
        string rest = slash < 0 ? "" : relativePath[(slash + 1)..];
        foreach (string root in _settings.WatchedFolders)
        {
            if (string.Equals(System.IO.Path.GetFileName(root), firstSeg, StringComparison.OrdinalIgnoreCase))
            {
                return System.IO.Path.Combine(root, rest.Replace('/', System.IO.Path.DirectorySeparatorChar));
            }
        }

        // The folder is no longer protected (or was renamed), so its original root is
        // unknown. Land the restore somewhere every interactive user can actually open —
        // NOT inside the store, which is ACL'd to SYSTEM+Admins and would lock the user
        // out of their own recovered file.
        string publicDocs = Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments);
        if (string.IsNullOrEmpty(publicDocs))
        {
            publicDocs = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "StepWind");
        }

        return System.IO.Path.Combine(publicDocs, "StepWind Restored",
            relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
    }

    private IpcResponse RunRetentionCommand()
    {
        RetentionResult r = RunRetention();
        return IpcResponse.Success(JsonSerializer.Serialize(r));
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
    private IpcResponse PurgeHistory(string selector)
    {
        if (string.IsNullOrWhiteSpace(selector))
        {
            return IpcResponse.Fail("nothing to purge — empty selector");
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
        RetentionResult r = _store.RunExclusive(
            () => Retention.Apply(_store.Log, _store.Blobs, _settings.Retention, DateTime.UtcNow));
        _log?.Invoke($"retention: kept {r.VersionsKept}/{r.VersionsBefore} versions, swept {r.BlobsSwept} blobs");
        return r;
    }

    private static IpcResponse Ok(object payload) => IpcResponse.Success(JsonSerializer.Serialize(payload));

    // Operations are encoded self-contained so the GUI can round-trip them without the server
    // holding per-connection state.
    private static string EncodeOp(FileOperation op)
        => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(op));

    private static FileOperation? DecodeOp(string id)
    {
        try
        {
            return JsonSerializer.Deserialize<FileOperation>(Convert.FromBase64String(id));
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> FixedNtfsVolumes()
    {
        foreach (DriveInfo d in DriveInfo.GetDrives())
        {
            bool ntfs = false;
            try { ntfs = d.DriveType == DriveType.Fixed && d.IsReady && d.DriveFormat == "NTFS"; } catch { }
            if (ntfs)
            {
                yield return d.Name.TrimEnd('\\');
            }
        }
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _retentionTimer.Dispose();
        lock (_watchLock)
        {
            _watch.Dispose();
        }

        _flightRecorder?.Dispose();
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
