using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using StepWind.App.ViewModels;
using Wpf.Ui.Appearance;
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

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Taskbar-style acrylic, same as BitBroom and Rescue: the DWM backdrop only shows
        // through transparent pixels, so apply dark theme + acrylic, then clear the window
        // background so the wallpaper blur comes through with a smoke tint for contrast.
        // When acrylic can't apply (Windows 10, or transparency effects off in Settings),
        // fall back to the stock solid dark background — XAML says None so DWM never paints
        // its own washed-out acrylic over that fallback.
        bool wantAcrylic = IsSystemTransparencyEnabled();
        ApplicationThemeManager.Apply(
            ApplicationTheme.Dark,
            wantAcrylic ? WindowBackdropType.Acrylic : WindowBackdropType.None,
            updateAccent: false);

        if (wantAcrylic && WindowBackdrop.ApplyBackdrop(this, WindowBackdropType.Acrylic))
        {
            WindowBackdropType = WindowBackdropType.Acrylic;
            Background = Brushes.Transparent;
            SmokeTint.Visibility = Visibility.Visible;
        }
        else if (TryFindResource("ApplicationBackgroundBrush") is Brush solid)
        {
            Background = solid;
        }

        RegisterGlobalHotkey();
    }

    /// <summary>Settings → Personalization → Colors → "Transparency effects".</summary>
    private static bool IsSystemTransparencyEnabled()
    {
        try
        {
            using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("EnableTransparency") is not int enabled || enabled != 0;
        }
        catch (Exception)
        {
            return true;
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
        await _viewModel.RefreshAsync();
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await _viewModel.RefreshAsync();

    private async void OnReverse(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TimelineRow row)
        {
            string msg = await _viewModel.ReverseAsync(row);
            System.Windows.MessageBox.Show(msg, "StepWind — Undo", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }
    }

    private async void OnRestore(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is VersionRow row)
        {
            string msg = await _viewModel.RestoreAsync(row);
            System.Windows.MessageBox.Show(msg, "StepWind — Restore", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }
    }

    private async void OnBrowseFile(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Pick a file to see its version history",
            CheckFileExists = false, // a deleted file can still have history
        };
        if (dialog.ShowDialog() == true)
        {
            HistoryHint.Text = "History for: " + dialog.FileName;
            await _viewModel.LoadHistoryAsync(dialog.FileName); // absolute path; service resolves it
            if (_viewModel.History.Count == 0)
            {
                HistoryHint.Text = "No versions yet for that file — it may be outside a protected folder, " +
                    "or it hasn't been saved since protection started.";
            }
        }
    }

    private async void OnRecentFileSelected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (RecentList.SelectedItem is ViewModels.RecentFileRow row)
        {
            HistoryHint.Text = "History for: " + row.RelativePath;
            await _viewModel.LoadHistoryAsync(row.RelativePath);
        }
    }

    private async void OnAddFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a folder to protect" };
        if (dialog.ShowDialog() == true)
        {
            await _viewModel.AddWatchedFolderAsync(dialog.FolderName);
        }
    }

    private async void OnRemoveFolder(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is string folder)
        {
            await _viewModel.RemoveWatchedFolderAsync(folder);
        }
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
