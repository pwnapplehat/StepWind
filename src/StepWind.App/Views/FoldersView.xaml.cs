using System.Windows;
using System.Windows.Controls;
using StepWind.App.ViewModels;

namespace StepWind.App.Views;

public partial class FoldersView : UserControl
{
    public FoldersView() => InitializeComponent();

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
        if ((sender as FrameworkElement)?.DataContext is string folder)
        {
            await Vm.RemoveWatchedFolderAsync(folder);
        }
    }
}
