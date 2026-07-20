using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

    /// <summary>
    /// Single click opens the row: a folder drills in, a file loads its history — the snappy,
    /// modern behavior (no hunting for double-click). Resolved from the clicked element so a
    /// click on padding or the scrollbar does nothing.
    /// </summary>
    private async void OnEntryClick(object sender, MouseButtonEventArgs e)
    {
        if (RowFrom(e.OriginalSource) is { } row)
        {
            await Vm.OpenBrowseRowAsync(row);
        }
    }

    private async void OnEntryKey(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Right && BrowseList.SelectedItem is BrowseRow row)
        {
            await Vm.OpenBrowseRowAsync(row);
        }
        else if (e.Key is Key.Back or Key.Left)
        {
            await Vm.GoUpAsync();
        }
    }

    private static BrowseRow? RowFrom(object source)
    {
        DependencyObject? d = source as DependencyObject;
        while (d is not null and not ListBoxItem)
        {
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        }

        return (d as ListBoxItem)?.DataContext as BrowseRow;
    }

    private async void OnGoUp(object sender, RoutedEventArgs e) => await Vm.GoUpAsync();

    private async void OnCrumb(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string path)
        {
            await Vm.BrowseToAsync(path);
        }
    }

    private async void OnBrowseFile(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open a file to see its version history",
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
        Vm.ClearHistorySelection();
        SwDialog.Notice(Window.GetWindow(this)!, "History deleted", msg);
    }
}
