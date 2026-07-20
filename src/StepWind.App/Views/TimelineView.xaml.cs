using System.Windows;
using System.Windows.Controls;
using StepWind.App.ViewModels;

namespace StepWind.App.Views;

public partial class TimelineView : UserControl
{
    public TimelineView() => InitializeComponent();

    private MainViewModel Vm => (MainViewModel)DataContext;

    private async void OnRefresh(object sender, RoutedEventArgs e) => await Vm.RefreshAsync();

    private void OnFilter(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string kind })
        {
            Vm.TimelineFilter = kind;
        }
    }

    private async void OnReverse(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TimelineRow row)
        {
            string msg = await Vm.ReverseAsync(row);
            MessageBox.Show(msg, "StepWind — Undo", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
