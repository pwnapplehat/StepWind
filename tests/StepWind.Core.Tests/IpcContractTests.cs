using StepWind.Core.Engine;
using StepWind.Core.Ipc;
using StepWind.Core.Storage;
using Xunit;

namespace StepWind.Core.Tests;

/// <summary>
/// The IPC command ids cross a process boundary between the GUI and the service — and during a
/// silent update the two can briefly be DIFFERENT builds. So the enum is append-only: values must
/// never be renumbered or reordered (a remapped id makes an old peer execute the wrong command).
/// These tests freeze the wire values and prove every command is actually routed by the host, so a
/// newly-added command can't be forgotten in the dispatcher.
/// </summary>
public class IpcContractTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sw-ipc", Guid.NewGuid().ToString("N"));
    private readonly StepWindHost _host;

    public IpcContractTests()
    {
        string watch = Path.Combine(_root, "Docs");
        Directory.CreateDirectory(watch);
        _host = new StepWindHost(new StepWindSettings
        {
            StoreRoot = Path.Combine(_root, "store"),
            WatchedFolders = [watch],
            FlightRecorderEnabled = false,
        }, new GzipBlobCodec());
    }

    [Fact]
    public void Wire_values_are_frozen_append_only()
    {
        // If any of these change, a mixed-version GUI/service pair mid-update runs the wrong
        // command. Add new commands with NEW numbers; never renumber an existing one.
        Assert.Equal(0, (int)IpcCommand.Ping);
        Assert.Equal(1, (int)IpcCommand.GetStatus);
        Assert.Equal(2, (int)IpcCommand.GetTimeline);
        Assert.Equal(3, (int)IpcCommand.GetHistory);
        Assert.Equal(4, (int)IpcCommand.ReverseOperation);
        Assert.Equal(5, (int)IpcCommand.RestoreVersion);
        Assert.Equal(6, (int)IpcCommand.GetSettings);
        Assert.Equal(7, (int)IpcCommand.SetSettings);
        Assert.Equal(8, (int)IpcCommand.RunRetention);
        Assert.Equal(9, (int)IpcCommand.GetRecentFiles);
        Assert.Equal(10, (int)IpcCommand.PurgeHistory);
        Assert.Equal(11, (int)IpcCommand.BrowseVersions);
        Assert.Equal(12, (int)IpcCommand.GetVersionContent);
        Assert.Equal(13, (int)IpcCommand.DiffVersions);
        Assert.Equal(14, (int)IpcCommand.CaptureNow);
        Assert.Equal(15, (int)IpcCommand.VerifyStore);
        Assert.Equal(16, (int)IpcCommand.RepairStore);
        Assert.Equal(17, (int)IpcCommand.ReverseBatch);
        Assert.Equal(18, (int)IpcCommand.RelocateStore);
    }

    [Fact]
    public void Every_command_is_routed_by_the_host()
    {
        // Send each command (in-process trusted). It may fail for a missing arg, but it must never
        // fall through to "unsupported command" — that would mean the dispatcher forgot to wire it.
        foreach (IpcCommand cmd in Enum.GetValues<IpcCommand>())
        {
            IpcResponse r = _host.Handle(new IpcRequest { Command = cmd });
            Assert.False(
                string.Equals(r.Error, "unsupported command", StringComparison.Ordinal),
                $"IpcCommand.{cmd} is not wired in StepWindHost.Handle");
        }
    }

    public void Dispose()
    {
        _host.Dispose();
        try { Directory.Delete(_root, true); } catch { }
    }
}
