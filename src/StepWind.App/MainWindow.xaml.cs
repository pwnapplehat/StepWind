using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using StepWind.App.ViewModels;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace StepWind.App;

public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _viewModel = new();
    private bool _reallyExit;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        Tray.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Taskbar-style acrylic, same as BitBroom and Rescue: the DWM backdrop only shows
        // through transparent pixels, so apply dark theme + acrylic, then clear the window
        // background so the wallpaper blur comes through with a smoke tint for contrast.
        // When acrylic can't apply (Windows 10, or transparency effects off in Settings),
        // fall back to the stock solid dark background — XAML says None so DWM never paints
        // its own washed-out acrylic over that fallback.
        bool wantAcrylic = IsSystemTransparencyEnabled();
        ApplicationThemeManager.Apply(
            ApplicationTheme.Dark,
            wantAcrylic ? WindowBackdropType.Acrylic : WindowBackdropType.None,
            updateAccent: false);

        if (wantAcrylic && WindowBackdrop.ApplyBackdrop(this, WindowBackdropType.Acrylic))
        {
            WindowBackdropType = WindowBackdropType.Acrylic;
            Background = Brushes.Transparent;
            SmokeTint.Visibility = Visibility.Visible;
        }
        else if (TryFindResource("ApplicationBackgroundBrush") is Brush solid)
        {
            Background = solid;
        }
    }

    /// <summary>Settings → Personalization → Colors → "Transparency effects".</summary>
    private static bool IsSystemTransparencyEnabled()
    {
        try
        {
            using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("EnableTransparency") is not int enabled || enabled != 0;
        }
        catch (Exception)
        {
            return true;
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_reallyExit)
        {
            e.Cancel = true;
            Hide(); // close hides to tray; StepWind keeps protecting via the service
        }
    }

    private void OnTrayOpen(object sender, RoutedEventArgs e)
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private async void OnOhNo(object sender, RoutedEventArgs e)
    {
        OnTrayOpen(sender, e);
        await _viewModel.RefreshAsync();
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await _viewModel.RefreshAsync();

    private async void OnReverse(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TimelineRow row)
        {
            string msg = await _viewModel.ReverseAsync(row);
            System.Windows.MessageBox.Show(msg, "StepWind — Undo", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }
    }

    private async void OnRestore(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is VersionRow row)
        {
            string msg = await _viewModel.RestoreAsync(row);
            System.Windows.MessageBox.Show(msg, "StepWind — Restore", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }
    }

    private async void OnHistoryKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            await _viewModel.LoadHistoryAsync(HistoryBox.Text);
        }
    }

    private void OnTrayExit(object sender, RoutedEventArgs e)
    {
        _reallyExit = true;
        Tray.Dispose();
        Close();
        System.Windows.Application.Current.Shutdown();
    }
}
