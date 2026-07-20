using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using StepWind.App.ViewModels;

namespace StepWind.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        Motion.AnimateOnShow(this);
    }

    private MainViewModel Vm => (MainViewModel)DataContext;

    private static void Open(string url)
        => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    private void OnOpenSite(object sender, RoutedEventArgs e) => Open("https://stepwind.app");

    private void OnOpenRepo(object sender, RoutedEventArgs e) => Open("https://github.com/pwnapplehat/StepWind");

    private async void OnApplyRetention(object sender, RoutedEventArgs e)
    {
        await Vm.ApplyRetentionAsync();
        SwDialog.Notice(Window.GetWindow(this)!, "Retention updated",
            "New retention rules saved. They apply on the next cleanup pass — or run one now.");
    }

    private async void OnRunRetention(object sender, RoutedEventArgs e)
    {
        string msg = await Vm.RunRetentionNowAsync();
        SwDialog.Notice(Window.GetWindow(this)!, "Cleanup", msg);
    }

    private async void OnPurgeUnprotected(object sender, RoutedEventArgs e)
    {
        SwDialog.Choice c = SwDialog.Confirm(Window.GetWindow(this)!,
            "Clean up unprotected history?",
            "Saved versions belonging to folders you no longer protect will be permanently deleted. Files on disk are not touched.",
            "Clean up", danger: true);
        if (c != SwDialog.Choice.Primary)
        {
            return;
        }

        string msg = await Vm.PurgeHistoryAsync("unprotected");
        SwDialog.Notice(Window.GetWindow(this)!, "Clean-up done", msg);
    }

    private async void OnPurgeAll(object sender, RoutedEventArgs e)
    {
        SwDialog.Choice c = SwDialog.Confirm(Window.GetWindow(this)!,
            "Delete ALL history?",
            "Every saved version of every file will be permanently deleted and the disk space freed. This cannot be undone. Your actual files on disk are not touched.",
            "Delete everything", danger: true);
        if (c != SwDialog.Choice.Primary)
        {
            return;
        }

        string msg = await Vm.PurgeHistoryAsync("*");
        SwDialog.Notice(Window.GetWindow(this)!, "History deleted", msg);
    }
}
