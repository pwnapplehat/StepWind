using System.Windows;
using System.Windows.Controls;
using StepWind.App.ViewModels;

namespace StepWind.App.Views;

public partial class FilesView : UserControl
{
    public FilesView() => InitializeComponent();

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
            MessageBox.Show(msg, "StepWind — Restore", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
