using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace StepWind.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    private static void Open(string url)
        => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    private void OnOpenSite(object sender, RoutedEventArgs e) => Open("https://stepwind.app");

    private void OnOpenRepo(object sender, RoutedEventArgs e) => Open("https://github.com/pwnapplehat/StepWind");
}
