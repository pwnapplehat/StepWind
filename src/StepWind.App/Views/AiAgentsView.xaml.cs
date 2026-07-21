using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using StepWind.App.ViewModels;
using StepWind.Core.Integration;

namespace StepWind.App.Views;

public partial class AiAgentsView : UserControl
{
    public AiAgentsView()
    {
        InitializeComponent();
        Motion.AnimateOnShow(this);

        // Probe the disk only when the tab is actually opened (and re-probe on each return —
        // the user may have installed a new AI tool since), not on the window's 3s status timer.
        IsVisibleChanged += async (_, e) =>
        {
            if ((bool)e.NewValue && DataContext is MainViewModel vm)
            {
                await vm.RefreshAgentsAsync();
            }
        };
    }

    private MainViewModel Vm => (MainViewModel)DataContext;

    private async void OnRescan(object sender, RoutedEventArgs e) => await Vm.RefreshAgentsAsync();

    private async void OnConnect(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AgentRow row)
        {
            return;
        }

        McpInstallResult result = await Vm.ConnectAgentAsync(row);
        SwDialog.Notice(Window.GetWindow(this)!, result.Ok ? "Connected" : "Not connected", result.Message);
    }

    private async void OnDisconnect(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AgentRow row)
        {
            return;
        }

        SwDialog.Choice c = SwDialog.Confirm(Window.GetWindow(this)!,
            $"Disconnect {row.Name}?",
            "StepWind's entry will be removed from this tool's MCP config. The tool itself and the rest of its config are untouched, and you can reconnect any time.",
            "Disconnect", danger: false);
        if (c != SwDialog.Choice.Primary)
        {
            return;
        }

        McpInstallResult result = await Vm.DisconnectAgentAsync(row);
        SwDialog.Notice(Window.GetWindow(this)!, result.Ok ? "Disconnected" : "Problem", result.Message);
    }

    private void OnCopyMcpConfig(object sender, RoutedEventArgs e)
    {
        System.Windows.Clipboard.SetText(MainViewModel.McpConfigSnippet);
        SwDialog.Notice(Window.GetWindow(this)!, "Copied",
            "MCP config copied. Paste it into the AI tool's MCP settings, then restart the tool if it doesn't pick it up right away.");
    }

    private void OnOpenBackups(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(McpInstaller.BackupRoot);
        Process.Start(new ProcessStartInfo(McpInstaller.BackupRoot) { UseShellExecute = true });
    }
}
