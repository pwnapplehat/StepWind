using System.Windows;
using System.Windows.Controls;
using StepWind.App.ViewModels;

namespace StepWind.App.Views;

public partial class FoldersView : UserControl
{
    public FoldersView()
    {
        InitializeComponent();
        Motion.AnimateOnShow(this);
    }

    private MainViewModel Vm => (MainViewModel)DataContext;

    private async void OnAddFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a folder to protect" };
        if (dialog.ShowDialog() == true)
        {
            await Vm.AddWatchedFolderAsync(dialog.FolderName);
        }
    }

    private async void OnRemoveFolder(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not string folder)
        {
            return;
        }

        // The professional ask: removal must be an explicit decision about the DATA, not
        // just the watching. Keep = versions stay restorable; Delete = gone now.
        SwDialog.Choice choice = SwDialog.ThreeChoice(
            Window.GetWindow(this)!,
            "Stop protecting this folder?",
            $"{folder}\n\nNew changes will no longer be captured. What should happen to the versions already saved for this folder?",
            "Keep history",
            "Delete history");

        if (choice == SwDialog.Choice.Cancel)
        {
            return;
        }

        await Vm.RemoveWatchedFolderAsync(folder);
        if (choice == SwDialog.Choice.Secondary)
        {
            string msg = await Vm.PurgeHistoryAsync(ViewModels.MainViewModel.FolderSelector(folder));
            SwDialog.Notice(Window.GetWindow(this)!, "History deleted", msg);
        }
    }
}
