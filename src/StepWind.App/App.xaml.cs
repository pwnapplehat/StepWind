using System.Windows;
using System.Windows.Threading;

namespace StepWind.App;

public partial class App : Application
{
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstance = new Mutex(initiallyOwned: true, "StepWind.App.SingleInstance", out bool isNew);
        if (!isNew)
        {
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show("StepWind hit an unexpected error:\n\n" + args.Exception.Message,
                "StepWind", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        Wpf.Ui.Appearance.ApplicationAccentColorManager.Apply(
            System.Windows.Media.Color.FromRgb(0x63, 0x66, 0xF1),
            Wpf.Ui.Appearance.ApplicationTheme.Dark);

        base.OnStartup(e);

        var window = new MainWindow();
        MainWindow = window;
        if (!e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase))
        {
            window.Show();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
