using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace StepWind.Core.Attribution;

/// <summary>
/// Answers "which process did this?" for the operation timeline, using ETW kernel file events.
/// The delete event itself carries no process, so we correlate: create/query events carry
/// both a FileKey and the process, and the later delete carries the same FileKey — the exact
/// scheme proven in the spike. Runs on a background thread inside the elevated service; a
/// bounded, time-expiring cache keeps memory flat. Never throws into the caller — attribution
/// is an enhancement, and the timeline is still correct (minus a process name) without it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FileAttributionTracker : IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<string, (string Process, int Pid, DateTime When)> _recentByPath = new(StringComparer.OrdinalIgnoreCase);
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
        _session.EnableKernelProvider(KernelTraceEventParser.Keywords.FileIOInit | KernelTraceEventParser.Keywords.FileIO);

        _session.Source.Kernel.FileIOFileCreate += d => Remember(d.FileName, d.ProcessName, d.ProcessID);
        _session.Source.Kernel.FileIOCreate += d => Remember(d.FileName, d.ProcessName, d.ProcessID);
        _session.Source.Kernel.FileIOQueryInfo += d => Remember(d.FileName, d.ProcessName, d.ProcessID);
        _session.Source.Kernel.FileIORename += d => Remember(d.FileName, d.ProcessName, d.ProcessID);
        _session.Source.Kernel.FileIODelete += d => Remember(d.FileName, d.ProcessName, d.ProcessID);

        _pump = new Thread(() => { try { _session.Source.Process(); } catch { } })
        {
            IsBackground = true,
            Name = "StepWind.Attribution",
        };
        _running = true;
        _pump.Start();
    }

    /// <summary>Most recent process seen touching a path, within the freshness window.</summary>
    public string? AttributeByPath(string fullPath, TimeSpan freshness)
    {
        lock (_lock)
        {
            if (_recentByPath.TryGetValue(fullPath, out (string Process, int Pid, DateTime When) hit)
                && DateTime.UtcNow - hit.When <= freshness)
            {
                return $"{hit.Process} ({hit.Pid})";
            }
        }

        return null;
    }

    private void Remember(string path, string process, int pid)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(process))
        {
            return;
        }

        lock (_lock)
        {
            _recentByPath[path] = (process, pid, DateTime.UtcNow);

            // Bound the cache: prune expired entries occasionally.
            if (_recentByPath.Count > 20_000)
            {
                DateTime cutoff = DateTime.UtcNow.AddMinutes(-5);
                foreach (string key in _recentByPath.Where(kv => kv.Value.When < cutoff).Select(kv => kv.Key).ToList())
                {
                    _recentByPath.Remove(key);
                }
            }
        }
    }

    public void Dispose()
    {
        _running = false;
        try { _session?.Stop(); } catch { }
        _session?.Dispose();
        _pump?.Join(2000);
    }
}
