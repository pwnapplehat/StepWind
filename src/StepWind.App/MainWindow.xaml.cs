using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using StepWind.App.ViewModels;
using Wpf.Ui.Controls;

namespace StepWind.App;

public partial class MainWindow : FluentWindow
{
    // Global "oh no" hotkey: Ctrl+Shift+Z brings StepWind up from anywhere the instant you
    // realize something went wrong — no hunting for the tray icon mid-panic.
    private const int HotkeyId = 0xB001;
    private const uint ModControl = 0x0002, ModShift = 0x0004, ModNoRepeat = 0x4000;
    private const uint VkZ = 0x5A;

    private readonly MainViewModel _viewModel = new();
    private bool _reallyExit;
    private bool _hotkeyRegistered;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        Tray.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        RegisterGlobalHotkey();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private void RegisterGlobalHotkey()
    {
        if (_hotkeyRegistered)
        {
            return;
        }

        var helper = new WindowInteropHelper(this);
        IntPtr handle = helper.EnsureHandle();
        HwndSource? source = HwndSource.FromHwnd(handle);
        source?.AddHook(WndProc);
        // No-repeat so holding the keys doesn't spam; failure is non-fatal (another app may
        // already own the combo) — the tray menu's "Oh no" item still works.
        _hotkeyRegistered = RegisterHotKey(handle, HotkeyId, ModControl | ModShift | ModNoRepeat, VkZ);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WmHotkey = 0x0312;
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            ShowFromTray();
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false; // bounce to the foreground without staying pinned
    }

    /// <summary>Rail navigation: each item's Tag names the view it shows.</summary>
    private void OnNav(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string view })
        {
            _viewModel.CurrentView = view;
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_reallyExit)
        {
            e.Cancel = true;
            Hide(); // close hides to tray; StepWind keeps protecting via the service
        }
    }

    private void OnTrayOpen(object sender, RoutedEventArgs e) => ShowFromTray();

    private async void OnOhNo(object sender, RoutedEventArgs e)
    {
        ShowFromTray();
        NavTimeline.IsChecked = true;
        _viewModel.CurrentView = "timeline";
        await _viewModel.RefreshAsync();
    }

    private void OnTrayExit(object sender, RoutedEventArgs e)
    {
        _reallyExit = true;
        if (_hotkeyRegistered)
        {
            try { UnregisterHotKey(new WindowInteropHelper(this).Handle, HotkeyId); } catch { }
        }

        Tray.Dispose();
        Close();
        System.Windows.Application.Current.Shutdown();
    }
}
