using System.Text.Json;
using StepWind.Core.Engine;
using StepWind.Core.Enterprise;
using StepWind.Core.Ipc;
using StepWind.Core.Storage;
using Xunit;

namespace StepWind.Core.Tests;

/// <summary>
/// The security audit trail: destructive and privileged actions produce an audit record naming the
/// actor and the outcome. These assert the record shape (pure) and that the host emits the right
/// events through a capturing sink — no Windows Event Log needed.
/// </summary>
public class AuditLogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sw-audit", Guid.NewGuid().ToString("N"));

    public AuditLogTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void AuditEvent_formats_a_single_parseable_line()
    {
        var e = new AuditEvent(AuditAction.HistoryPurged, "DOMAIN\\alice", "selector='*', removed 12", Success: true);
        string line = e.Format();
        Assert.Contains("HistoryPurged", line);
        Assert.Contains("actor=DOMAIN\\alice", line);
        Assert.Contains("result=OK", line);
        Assert.Contains("removed 12", line);

        Assert.Contains("result=DENIED/FAILED",
            new AuditEvent(AuditAction.SettingsChangeDeniedByPolicy, "x", "y", Success: false).Format());
    }

    [Fact]
    public void EventId_is_the_stable_action_number()
    {
        Assert.Equal(3000, (int)AuditAction.HistoryPurged);
        Assert.Equal(2001, (int)AuditAction.SettingsChangeDeniedByPolicy);
        Assert.Equal(1000, (int)AuditAction.ServiceStarted);
    }

    [Fact]
    public void Purge_and_restore_and_settings_changes_are_audited_with_the_actor()
    {
        string watch = Path.Combine(_root, "Docs");
        Directory.CreateDirectory(watch);
        var audit = new CapturingAudit();
        using var host = new StepWindHost(new StepWindSettings
        {
            StoreRoot = Path.Combine(_root, "store"),
            WatchedFolders = [watch],
            FlightRecorderEnabled = false,
        }, new GzipBlobCodec(), audit: audit);

        // A capture + restore round-trip, then a settings change and a purge — all via the trusted
        // in-process caller (Actor = SYSTEM/in-process).
        string file = Path.Combine(watch, "a.txt");
        File.WriteAllText(file, "v1");
        IpcResponse cap = host.Handle(new IpcRequest { Command = IpcCommand.CaptureNow, Arg1 = file });
        string versionId = JsonSerializer.Deserialize<VersionEntry>(cap.Json!)!.VersionId;

        Assert.True(host.Handle(new IpcRequest { Command = IpcCommand.RestoreVersion, Arg1 = versionId }).Ok);
        Assert.True(host.Handle(new IpcRequest
        {
            Command = IpcCommand.SetSettings,
            Arg1 = JsonSerializer.Serialize(new { AutoUpdateEnabled = false }),
        }).Ok);
        Assert.True(host.Handle(new IpcRequest { Command = IpcCommand.PurgeHistory, Arg1 = "*" }).Ok);

        Assert.Contains(audit.Events, e => e.Action == AuditAction.VersionRestored);
        Assert.Contains(audit.Events, e => e.Action == AuditAction.SettingsChanged && e.Detail.Contains("AutoUpdateEnabled=False"));
        Assert.Contains(audit.Events, e => e.Action == AuditAction.HistoryPurged);
        Assert.All(audit.Events, e => Assert.False(string.IsNullOrEmpty(e.Actor)));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
