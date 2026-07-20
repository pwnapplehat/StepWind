using System.Runtime.Versioning;
using StepWind.Core.Engine;
using StepWind.Core.Ipc;
using StepWind.Core.Storage;

namespace StepWind.Service;

/// <summary>
/// The StepWind background service (runs as LocalSystem so it can read the USN journal and
/// ETW). It hosts the engine + flight recorder + retention and serves the GUI over a named
/// pipe. When encryption is enabled the store's AES key is DPAPI-sealed at machine scope (see
/// <see cref="StepWind.Core.Storage.KeyProtector"/>) so the unattended service can use it with
/// no passphrase; otherwise the store is compressed and ACL-guarded.
/// </summary>
[SupportedOSPlatform("windows")]
public static class Program
{
    public static int Main(string[] args)
    {
        // Installer-invoked service management verbs (run elevated by the installer).
        switch (args.FirstOrDefault()?.ToLowerInvariant())
        {
            case "install-service": return ServiceControl.Install();
            case "uninstall-service": return ServiceControl.Uninstall();
            case "start-service": return ServiceControl.Start();
            case "stop-service": return ServiceControl.Stop();
        }

        HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddWindowsService(o => o.ServiceName = "StepWind");
        builder.Services.AddHostedService<StepWindWorker>();
        builder.Build().Run();
        return 0;
    }
}

[SupportedOSPlatform("windows")]
public sealed class StepWindWorker : BackgroundService
{
    private readonly ILogger<StepWindWorker> _logger;
    private StepWindHost? _host;
    private PipeServer? _pipe;
    private Mutex? _singleInstance;

    public StepWindWorker(ILogger<StepWindWorker> logger) => _logger = logger;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Named pipes allow MULTIPLE servers on one name (clients round-robin between them),
        // so two service instances — e.g. the installed service plus a console-mode debug run,
        // or an old build lingering through an update — would silently split the traffic and
        // answer with inconsistent state. Refuse to be the second instance.
        _singleInstance = new Mutex(initiallyOwned: true, @"Global\StepWind.Service.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            _logger.LogError("another StepWind service instance is already running — exiting so it keeps sole ownership of the pipe.");
            Environment.Exit(1);
        }

        StepWindSettings settings = StepWindSettings.Load();
        settings.Save(); // materialize defaults on first run

        IBlobCodec codec = SelectCodec(settings);
        _host = new StepWindHost(settings, codec, msg => _logger.LogInformation("{Message}", msg));
        _pipe = new PipeServer(req => _host.Handle(req), msg => _logger.LogWarning("{Message}", msg));
        _pipe.Start();

        _logger.LogInformation("StepWind service started; watching {Count} folder(s).", settings.WatchedFolders.Count);

        if (settings.AutoUpdateEnabled)
        {
            _ = RunUpdateLoopAsync(stoppingToken);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Always a <see cref="MigratingBlobCodec"/>, so encryption is a live setting: the GUI can
    /// toggle it at any time and the store re-encodes itself in the background (both formats
    /// stay readable throughout). The AES key is created lazily on first enable and sealed
    /// with machine-scope DPAPI. Falls back to plain-only if key material is unavailable so
    /// protection never stops over an encryption hiccup.
    /// </summary>
    private IBlobCodec SelectCodec(StepWindSettings settings)
    {
        IBlobCodec CipherFactory() => new AesGcmBlobCodec(KeyProtector.LoadOrCreate(settings.StoreRoot));

        try
        {
            var codec = new MigratingBlobCodec(new GzipBlobCodec(), CipherFactory, settings.EncryptionEnabled);
            if (settings.EncryptionEnabled)
            {
                _logger.LogInformation("store encryption enabled (AES-256-GCM, machine-sealed key)");
            }

            return codec;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("encryption unavailable ({Message}); using compressed store", ex.Message);
            return new MigratingBlobCodec(new GzipBlobCodec(), CipherFactory, encryptNew: false);
        }
    }

    /// <summary>Automatic silent updates: one check shortly after start, then daily.</summary>
    private async Task RunUpdateLoopAsync(CancellationToken ct)
    {
        var updater = new UpdateService(msg => _logger.LogInformation("[update] {Message}", msg));
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), ct);
            while (!ct.IsCancellationRequested)
            {
                await updater.CheckAndApplyAsync(ct);
                await Task.Delay(TimeSpan.FromHours(24), ct);
            }
        }
        catch (OperationCanceledException)
        {
            // service stopping
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _pipe?.Dispose();
        _host?.Dispose();
        _singleInstance?.Dispose();
        return base.StopAsync(cancellationToken);
    }
}
