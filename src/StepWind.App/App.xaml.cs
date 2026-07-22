using System.Threading;
using System.Windows;

namespace StepWind.App;

public partial class App : Application
{
    private const string MutexName = "StepWind.App.SingleInstance";
    // A named event the running instance waits on; a second launch sets it to say "show yourself".
    private const string ShowEventName = @"Global\StepWind.App.ShowWindow";

    private Mutex? _singleInstance;
    private EventWaitHandle? _showEvent;
    private CancellationTokenSource? _showWaitCts;

    /// <summary>Raised (on a background thread) when another launch asks the running app to come forward.</summary>
    public static event Action? SecondInstanceRequested;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstance = new Mutex(initiallyOwned: true, MutexName, out bool isNew);
        if (!isNew)
        {
            // Already running: instead of just exiting (which reads as "the app is broken, nothing
            // happened"), signal the running instance to surface itself, then quit.
            try
            {
                if (EventWaitHandle.TryOpenExisting(ShowEventName, out EventWaitHandle? existing))
                {
                    existing.Set();
                    existing.Dispose();
                }
            }
            catch
            {
                // best effort — worst case the second launch just exits silently as before
            }

            Shutdown();
            return;
        }

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _showWaitCts = new CancellationTokenSource();
        WaitForShowRequests(_showEvent, _showWaitCts.Token);

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show("StepWind hit an unexpected error:\n\n" + args.Exception.Message,
                "StepWind", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        base.OnStartup(e);

        var window = new MainWindow();
        MainWindow = window;
        if (!e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase))
        {
            window.Show();
        }
    }

    private static void WaitForShowRequests(EventWaitHandle handle, CancellationToken ct)
    {
        var thread = new Thread(() =>
        {
            using var registration = ct.Register(() => handle.Set()); // wake the wait so the thread can exit
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    handle.WaitOne();
                    if (ct.IsCancellationRequested)
                    {
                        return;
                    }

                    SecondInstanceRequested?.Invoke();
                }
                catch
                {
                    return;
                }
            }
        })
        { IsBackground = true, Name = "StepWind.ShowWait" };
        thread.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showWaitCts?.Cancel();
        _showEvent?.Dispose();
        _showWaitCts?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
