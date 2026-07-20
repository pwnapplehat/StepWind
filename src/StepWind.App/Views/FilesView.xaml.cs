using System.Windows;
using System.Windows.Controls;
using StepWind.App.ViewModels;

namespace StepWind.App.Views;

public partial class FilesView : UserControl
{
    public FilesView()
    {
        InitializeComponent();
        Motion.AnimateOnShow(this);
    }

    private MainViewModel Vm => (MainViewModel)DataContext;

    private async void OnRecentFileSelected(object sender, SelectionChangedEventArgs e)
    {
        if (RecentList.SelectedItem is RecentFileRow row)
        {
            await Vm.LoadHistoryAsync(row.RelativePath);
        }
    }

    private async void OnBrowseFile(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Pick a file to see its version history",
            CheckFileExists = false, // deleted files still have history
        };
        if (dialog.ShowDialog() == true)
        {
            await Vm.LoadHistoryAsync(dialog.FileName);
        }
    }

    private async void OnRestore(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is VersionRow row)
        {
            string msg = await Vm.RestoreAsync(row);
            SwDialog.Notice(Window.GetWindow(this)!, "Restore", msg);
        }
    }

    /// <summary>Deletes the selected file's entire saved history (asks first).</summary>
    private async void OnDeleteFileHistory(object sender, RoutedEventArgs e)
    {
        string target = Vm.HistoryPath;
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        SwDialog.Choice c = SwDialog.Confirm(Window.GetWindow(this)!,
            "Delete this file's history?",
            $"{target}\n\nEvery saved version of this file will be permanently deleted. The file itself on disk is not touched.",
            "Delete history", danger: true);
        if (c != SwDialog.Choice.Primary)
        {
            return;
        }

        string msg = await Vm.PurgeHistoryAsync(target);
        Vm.History.Clear();
        SwDialog.Notice(Window.GetWindow(this)!, "History deleted", msg);
    }
}
