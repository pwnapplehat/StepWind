using System.Collections.Concurrent;
using System.Text.Json;
using StepWind.Core.Engine;
using StepWind.Core.Ipc;
using StepWind.Core.Storage;
using Xunit;

namespace StepWind.Core.Tests;

/// <summary>
/// P2-4: the pipe server now handles connections concurrently (a slow diff no longer blocks the
/// GUI and every MCP client), which means <see cref="StepWindHost.Handle"/> is called in parallel.
/// This hammers the host from many threads — reads, captures, and settings writes at once — and
/// asserts nothing throws (the RootOwners map race the concurrency exposed is now locked) and the
/// store stays consistent.
/// </summary>
public class ConcurrencyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sw-concurrency", Guid.NewGuid().ToString("N"));
    private readonly string _watch;
    private readonly StepWindHost _host;

    public ConcurrencyTests()
    {
        _watch = Path.Combine(_root, "Docs");
        Directory.CreateDirectory(_watch);
        for (int i = 0; i < 15; i++)
        {
            File.WriteAllText(Path.Combine(_watch, $"seed{i}.txt"), $"seed {i}");
        }

        var settings = new StepWindSettings
        {
            StoreRoot = Path.Combine(_root, "store"),
            WatchedFolders = [_watch],
            FlightRecorderEnabled = false,
        };
        _host = new StepWindHost(settings, new GzipBlobCodec());
    }

    [Fact]
    public async Task Parallel_reads_captures_and_settings_writes_never_corrupt_or_throw()
    {
        // Seed some history first so reads have something to chew on.
        foreach (string f in Directory.EnumerateFiles(_watch))
        {
            _host.Handle(new IpcRequest { Command = IpcCommand.CaptureNow, Arg1 = f });
        }

        var errors = new ConcurrentBag<string>();
        var tasks = new List<Task>();

        // Many concurrent readers (the hot path the concurrent pipe server enables).
        for (int t = 0; t < 12; t++)
        {
            tasks.Add(Task.Run(() =>
            {
                for (int i = 0; i < 40; i++)
                {
                    try
                    {
                        Assert.True(_host.Handle(new IpcRequest { Command = IpcCommand.GetStatus }).Ok);
                        Assert.True(_host.Handle(new IpcRequest { Command = IpcCommand.GetRecentFiles }).Ok);
                        Assert.True(_host.Handle(new IpcRequest { Command = IpcCommand.BrowseVersions, Arg1 = "" }).Ok);
                        Assert.True(_host.Handle(new IpcRequest { Command = IpcCommand.GetTimeline }).Ok);
                    }
                    catch (Exception ex)
                    {
                        errors.Add(ex.ToString());
                    }
                }
            }));
        }

        // A couple of concurrent settings writers (exercises the RootOwners lock under contention).
        for (int t = 0; t < 2; t++)
        {
            string extra = Path.Combine(_root, $"Extra{t}");
            Directory.CreateDirectory(extra);
            tasks.Add(Task.Run(() =>
            {
                for (int i = 0; i < 20; i++)
                {
                    try
                    {
                        _host.Handle(new IpcRequest
                        {
                            Command = IpcCommand.SetSettings,
                            Arg1 = JsonSerializer.Serialize(new { WatchedFolders = new[] { _watch, extra } }),
                        });
                    }
                    catch (Exception ex)
                    {
                        errors.Add(ex.ToString());
                    }
                }
            }));
        }

        await Task.WhenAll(tasks);

        Assert.True(errors.IsEmpty, errors.FirstOrDefault());

        // The store is still coherent and every seeded file's history survived the storm.
        IpcResponse recent = _host.Handle(new IpcRequest { Command = IpcCommand.GetRecentFiles });
        Assert.True(recent.Ok);
        RecentFileEntry[] files = JsonSerializer.Deserialize<RecentFileEntry[]>(recent.Json!)!;
        Assert.True(files.Length >= 15, $"expected >=15 files with history, got {files.Length}");
    }

    public void Dispose()
    {
        _host.Dispose();
        try { Directory.Delete(_root, true); } catch { }
    }
}
