using System.Runtime.Versioning;
using StepWind.Core.Engine;
using StepWind.Core.Ipc;
using StepWind.Core.Storage;

namespace StepWind.Service;

/// <summary>
/// The StepWind background service (runs as LocalSystem so it can read the USN journal and
/// ETW). It hosts the engine + flight recorder + retention and serves the GUI over a named
/// pipe. Encryption key handling: for v1 the store is ACL-protected by default; if the user
/// enabled passphrase encryption, the key is supplied out-of-band (DPAPI-protected under the
/// service account) — wired in a later step. Here we default to the compressed, ACL-guarded
/// codec so the service always starts.
/// </summary>
[SupportedOSPlatform("windows")]
public static class Program
{
    public static void Main(string[] args)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddWindowsService(o => o.ServiceName = "StepWind");
        builder.Services.AddHostedService<StepWindWorker>();
        builder.Build().Run();
    }
}

[SupportedOSPlatform("windows")]
public sealed class StepWindWorker : BackgroundService
{
    private readonly ILogger<StepWindWorker> _logger;
    private StepWindHost? _host;
    private PipeServer? _pipe;

    public StepWindWorker(ILogger<StepWindWorker> logger) => _logger = logger;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        StepWindSettings settings = StepWindSettings.Load();
        settings.Save(); // materialize defaults on first run

        _host = new StepWindHost(settings, new GzipBlobCodec(), msg => _logger.LogInformation("{Message}", msg));
        _pipe = new PipeServer(req => _host.Handle(req), msg => _logger.LogWarning("{Message}", msg));
        _pipe.Start();

        _logger.LogInformation("StepWind service started; watching {Count} folder(s).", settings.WatchedFolders.Count);
        return Task.CompletedTask;
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _pipe?.Dispose();
        _host?.Dispose();
        return base.StopAsync(cancellationToken);
    }
}
