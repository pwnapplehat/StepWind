using System.Diagnostics;
using System.Runtime.Versioning;

namespace StepWind.Core.Enterprise;

/// <summary>Security-relevant actions worth an audit record. The numeric value is the stable Event ID (for SIEM filtering).</summary>
public enum AuditAction
{
    ServiceStarted = 1000,
    ServiceStopped = 1001,
    SettingsChanged = 2000,
    SettingsChangeDeniedByPolicy = 2001,
    SettingsChangeDeniedByAuthorization = 2002,
    HistoryPurged = 3000,
    VersionRestored = 3001,
    OperationReversed = 3002,
    FolderProtected = 3003,
    FolderUnprotected = 3004,
    EncryptionToggled = 3005,
    StoreRelocated = 3006,
    UpdateStaged = 4000,
    UpdateLaunched = 4001,
    PolicyEnforced = 5000,
}

/// <summary>One audit record. Actor is the acting user's identity; Detail is human-readable context.</summary>
public sealed record AuditEvent(AuditAction Action, string Actor, string Detail, bool Success)
{
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;

    /// <summary>The single-line message written to the log (SIEM-parseable, no PII beyond the actor + the detail the caller chose).</summary>
    public string Format() =>
        $"[{Action}] actor={Actor} result={(Success ? "OK" : "DENIED/FAILED")} :: {Detail}";
}

/// <summary>Where audit records go. Kept an interface so the host is testable without touching the real Event Log.</summary>
public interface IAuditSink
{
    void Write(AuditEvent e);
}

/// <summary>Discards records — the default when auditing is off or on a non-Windows/unprivileged run.</summary>
public sealed class NullAuditSink : IAuditSink
{
    public static NullAuditSink Instance { get; } = new();

    public void Write(AuditEvent e)
    {
        // intentionally nothing
    }
}

/// <summary>
/// Writes audit records to a dedicated <c>StepWind</c> Windows Event Log — the standard local,
/// tamper-evident, SIEM-forwardable sink (Windows Event Forwarding / any EDR agent can ship it;
/// nothing ever leaves the machine on its own). Creating the log source needs administrator
/// rights, which the SYSTEM service has; if creation fails (e.g. an unprivileged dev run) the sink
/// degrades to the optional fallback logger and never throws into the caller.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class EventLogAuditSink : IAuditSink, IDisposable
{
    public const string LogName = "StepWind";
    public const string SourceName = "StepWind";

    private readonly EventLog? _log;
    private readonly Action<string>? _fallback;

    public EventLogAuditSink(Action<string>? fallback = null)
    {
        _fallback = fallback;
        try
        {
            if (!EventLog.SourceExists(SourceName))
            {
                EventLog.CreateEventSource(new EventSourceCreationData(SourceName, LogName));
            }

            _log = new EventLog(LogName) { Source = SourceName };
        }
        catch (Exception ex)
        {
            _fallback?.Invoke($"audit: Event Log unavailable ({ex.Message}); auditing to the service log instead");
            _log = null;
        }
    }

    public void Write(AuditEvent e)
    {
        string message = e.Format();
        try
        {
            _log?.WriteEntry(message, e.Success ? EventLogEntryType.Information : EventLogEntryType.Warning, (int)e.Action);
        }
        catch (Exception ex)
        {
            _fallback?.Invoke($"audit write failed ({ex.Message}): {message}");
            return;
        }

        if (_log is null)
        {
            _fallback?.Invoke("audit: " + message);
        }
    }

    public void Dispose() => _log?.Dispose();
}
