using System.Windows;
using System.Windows.Controls;
using StepWind.App.ViewModels;

namespace StepWind.App.Views;

public partial class TimelineView : UserControl
{
    public TimelineView()
    {
        InitializeComponent();
        // Reflect the persisted scope once the VM arrives (checked state isn't bindable
        // two-way across two radio buttons without extra converters).
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                (vm.TimelineProtectedOnly ? ScopeProtected : ScopeAll).IsChecked = true;
                vm.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(MainViewModel.TimelineProtectedOnly))
                    {
                        (vm.TimelineProtectedOnly ? ScopeProtected : ScopeAll).IsChecked = true;
                    }
                };
            }
        };
    }

    private MainViewModel Vm => (MainViewModel)DataContext;

    private async void OnRefresh(object sender, RoutedEventArgs e) => await Vm.RefreshAsync();

    private void OnFilter(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string kind })
        {
            Vm.TimelineFilter = kind;
        }
    }

    private void OnScope(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string scope })
        {
            Vm.TimelineProtectedOnly = scope == "protected";
        }
    }

    private async void OnReverse(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TimelineRow row)
        {
            string msg = await Vm.ReverseAsync(row);
            SwDialog.Notice(Window.GetWindow(this)!, "Undo", msg);
        }
    }
}
