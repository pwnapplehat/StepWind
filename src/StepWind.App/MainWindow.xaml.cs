using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using StepWind.App.ViewModels;
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
