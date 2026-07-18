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
    private WatchEngine _watch;

    public StepWindHost(StepWindSettings settings, IBlobCodec codec, Action<string>? log = null)
    {
        _settings = settings;
        _log = log;

        _store = new VersionStore(
            new BlobStore(settings.StoreRoot, codec),
            new VersionLog(System.IO.Path.Combine(settings.StoreRoot, "versions.jsonl")));
        _store.Blobs.CleanTemp(); // drop any half-written blobs from a previous crash

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
                IpcCommand.ReverseOperation => ReverseOperation(request.Arg1 ?? ""),
                IpcCommand.RestoreVersion => RestoreVersion(request.Arg1 ?? "", request.Arg2),
                IpcCommand.RunRetention => RunRetentionCommand(),
                IpcCommand.GetSettings => Ok(BuildSettings()),
                IpcCommand.SetSettings => ApplySettings(request.Arg1 ?? ""),
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
            _settings.WatchedFolders,
        };
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
    };

    /// <summary>Editable slice of settings the GUI can push (WatchedFolders is the main one).</summary>
    private sealed class SettingsPatch
    {
        public List<string>? WatchedFolders { get; set; }
        public List<string>? ExcludedPrefixes { get; set; }
        public bool? AutoUpdateEnabled { get; set; }
    }

    private IpcResponse ApplySettings(string json)
    {
        SettingsPatch? patch = JsonSerializer.Deserialize<SettingsPatch>(json);
        if (patch is null)
        {
            return IpcResponse.Fail("bad settings payload");
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

        _settings.Save();

        if (foldersChanged)
        {
            lock (_watchLock)
            {
                WatchEngine old = _watch;
                _watch = BuildWatch(); // start watching the new set…
                old.Dispose();          // …then tear down the old watchers
            }

            _log?.Invoke($"watched folders updated ({_settings.WatchedFolders.Count})");
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

        // Fallback: a restored files folder under the store.
        return System.IO.Path.Combine(_settings.StoreRoot, "restored", relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
    }

    private IpcResponse RunRetentionCommand()
    {
        RetentionResult r = RunRetention();
        return IpcResponse.Success(JsonSerializer.Serialize(r));
    }

    private RetentionResult RunRetention()
    {
        RetentionResult r = Retention.Apply(_store.Log, _store.Blobs, _settings.Retention, DateTime.UtcNow);
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
        _retentionTimer.Dispose();
        lock (_watchLock)
        {
            _watch.Dispose();
        }

        _flightRecorder?.Dispose();
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
