using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;

namespace StepWind.App;

/// <summary>
/// The chromeless host window. Everything visible is rendered by the web layer (./web served
/// over a virtual host inside WebView2); this class owns only what the web platform can't:
/// the window itself, the tray icon, the global panic hotkey, and the <see cref="Bridge"/>
/// that connects the web UI to the elevated service's named pipe.
/// </summary>
public partial class MainWindow : Window
{
    // Global "oh no" hotkey: Ctrl+Shift+Z brings StepWind up from anywhere the instant you
    // realize something went wrong — no hunting for the tray icon mid-panic.
    private const int HotkeyId = 0xB001;
    private const uint ModControl = 0x0002, ModShift = 0x0004, ModNoRepeat = 0x4000;
    private const uint VkZ = 0x5A;

    private readonly Bridge _bridge;
    private readonly HostStatusMonitor _statusMonitor;
    private Task? _webInit;
    private HwndSource? _hotkeySource;
    private bool _reallyExit;
    private bool _hotkeyRegistered;

    public MainWindow()
    {
        InitializeComponent();
        _bridge = new Bridge(this);
        Tray.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        Loaded += async (_, _) => await InitWebAsync();
        StateChanged += (_, _) => OnHostStateChanged();

        // The hotkey lives on its own message-only window, created RIGHT NOW: registering in
        // OnSourceInitialized (the old approach) silently meant NO panic hotkey until the
        // window was first shown — i.e. never, in the app's most common state (autostarted
        // --minimized to tray). Found by the tray-open repro harness.
        RegisterGlobalHotkey();

        // First run is now an explicit onboarding flow in the web layer (welcome + choose which
        // folders get version history) rather than silently seeding folders — the user consents.
        // The whole-machine flight recorder (undo of moves/renames/deletes) is on regardless.

        // Watch protection state in the background so the tray can warn about a stopped service,
        // a disk-full pause, or a ready update even while minimized — and keep the tooltip live.
        _statusMonitor = new HostStatusMonitor(ShowTrayNotice, SetTrayTooltip);
        _statusMonitor.Start();

        // Bring the window up when a second launch signals us (foreground-on-relaunch).
        App.SecondInstanceRequested += () => Dispatcher.Invoke(() => ShowFromTray());
    }

    private void SetTrayTooltip(string text) => Dispatcher.Invoke(() => Tray.ToolTipText = text);

    private void ShowTrayNotice(TrayNotice notice) => Dispatcher.Invoke(() =>
    {
        // Clicking the balloon opens StepWind (to the timeline for warnings — the "what happened").
        void OpenOnce(object s, RoutedEventArgs args)
        {
            Tray.TrayBalloonTipClicked -= OpenOnce;
            ShowFromTray(navigateTimeline: notice.Warning);
        }

        Tray.TrayBalloonTipClicked += OpenOnce;
        Tray.ShowBalloonTip("StepWind — " + notice.Title, notice.Message,
            notice.Warning ? Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Warning
                           : Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Info);
    });

    /// <summary>
    /// Idempotent web init. The naive "if (CoreWebView2 is null)" guard is NOT enough:
    /// CoreWebView2 stays null until EnsureCoreWebView2Async COMPLETES, so two callers
    /// (the window's Loaded event and a tray-open) both passed the check and initialized
    /// with two different environments — the exact "already initialized with a different
    /// CoreWebView2Environment" crash seen live. Caching the Task makes every caller await
    /// the same single initialization.
    /// </summary>
    private Task InitWebAsync() => _webInit ??= InitWebGuardedAsync();

    /// <summary>
    /// Wraps web init so a transient WebView2 failure doesn't brick the window forever: the naive
    /// cached-task approach would keep handing every caller the SAME faulted task. On failure we
    /// clear the cache (so Retry re-attempts) and show a WPF fallback with Retry + install-runtime,
    /// rather than a permanently blank window. Protection keeps running in the service regardless.
    /// </summary>
    private async Task InitWebGuardedAsync()
    {
        try
        {
            await InitWebCoreAsync();
            Dispatcher.Invoke(() =>
            {
                WebFail.Visibility = Visibility.Collapsed;
                Web.Visibility = Visibility.Visible;
            });
        }
        catch (Exception ex)
        {
            _webInit = null; // don't cache the failure — allow a real retry
            Dispatcher.Invoke(() =>
            {
                WebFailMsg.Text = "The web view runtime didn't load: " + ex.Message;
                Web.Visibility = Visibility.Collapsed;
                WebFail.Visibility = Visibility.Visible;
            });
        }
    }

    private void OnWebRetry(object sender, RoutedEventArgs e)
    {
        WebFail.Visibility = Visibility.Collapsed;
        Web.Visibility = Visibility.Visible;
        _ = InitWebAsync();
    }

    private void OnInstallRuntime(object sender, RoutedEventArgs e)
    {
        // Prefer the installer-bundled bootstrapper if it shipped beside us; otherwise send the
        // user to Microsoft's official runtime download. Either way the user drives the install.
        try
        {
            string bundled = Path.Combine(AppContext.BaseDirectory, "redist", "MicrosoftEdgeWebView2Setup.exe");
            string target = File.Exists(bundled) ? bundled : "https://developer.microsoft.com/microsoft-edge/webview2/";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show("Couldn't start the WebView2 runtime installer:\n\n" + ex.Message,
                "StepWind", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task InitWebCoreAsync()
    {
        string dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StepWind", "webview");
        CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(null, dataDir);
        await Web.EnsureCoreWebView2Async(env);

        CoreWebView2Settings s = Web.CoreWebView2!.Settings;
        s.AreDefaultContextMenusEnabled = false;
        s.IsStatusBarEnabled = false;
        s.IsZoomControlEnabled = false;
#if DEBUG
        s.AreDevToolsEnabled = true;
#else
        s.AreDevToolsEnabled = false;
#endif
        // app-region: drag CSS = native title-bar behavior (drag, snap layouts, double-click
        // maximize, right-click system menu) — what makes chromeless feel native.
        s.IsNonClientRegionSupportEnabled = true;

        string webDir = Path.Combine(AppContext.BaseDirectory, "web");
        Web.CoreWebView2.SetVirtualHostNameToFolderMapping(
            "app.stepwind", webDir, CoreWebView2HostResourceAccessKind.Deny);

        Web.CoreWebView2.WebMessageReceived += (_, e) => _bridge.HandleMessage(e.WebMessageAsJson);
        Web.CoreWebView2.NavigationCompleted += async (_, _) =>
        {
            // Deep links (used by the screenshot/E2E harnesses): --view=files [--autopick]
            string[] args = Environment.GetCommandLineArgs();
            string? view = args.FirstOrDefault(a => a.StartsWith("--view=", StringComparison.OrdinalIgnoreCase))?[7..];
            if (!string.IsNullOrWhiteSpace(view))
            {
                await Web.CoreWebView2.ExecuteScriptAsync($"setTimeout(() => navigate('{view}'), 400)");
            }
            // Screenshot/E2E helper: force a theme for this session without persisting it.
            string? theme = args.FirstOrDefault(a => a.StartsWith("--theme=", StringComparison.OrdinalIgnoreCase))?[8..];
            if (theme is "light" or "dark")
            {
                await Web.CoreWebView2.ExecuteScriptAsync(
                    $"document.documentElement.dataset.theme='{theme}'; call('chromeTheme', {{ theme: '{theme}' }});");
            }
            // --settled: freeze entrance animations at their final state so a screenshot never
            // catches a mid-fade frame (the fast-capture artifact). Kept out of DEBUG guard so
            // the screenshot harness can use the release build too.
            if (args.Contains("--settled", StringComparer.OrdinalIgnoreCase))
            {
                await Web.CoreWebView2.ExecuteScriptAsync("document.body.classList.add('no-anim')");
            }
            if (args.Contains("--autopick", StringComparer.OrdinalIgnoreCase))
            {
                await Web.CoreWebView2.ExecuteScriptAsync(
                    "setTimeout(() => { document.querySelector('.f-row')?.click(); " +
                    "setTimeout(() => { document.querySelector('.f-row:not(:has(.f-ico.folder))')?.click(); " +
                    "setTimeout(() => document.querySelector('.v-row')?.click(), 900); }, 900); }, 1600)");
            }
#if DEBUG
            await RunE2EIfRequestedAsync(args);
#endif
        };
        Web.CoreWebView2.Navigate("https://app.stepwind/index.html");
        OnHostStateChanged();
    }

    /// <summary>Keeps a resize strip when windowed; edge-to-edge when maximized.</summary>
    private void OnHostStateChanged()
    {
        Web.Margin = new Thickness(WindowState == WindowState.Maximized ? 0 : 6);
        _bridge.NotifyWindowState(WindowState == WindowState.Maximized);
    }

    /// <summary>Posts a JSON string to the web layer (bridge replies + host events).</summary>
    internal void PostToWeb(string json) =>
        Dispatcher.Invoke(() => Web.CoreWebView2?.PostWebMessageAsJson(json));

    internal void RunOnUi(Action action) => Dispatcher.Invoke(action);

    /// <summary>Matches the window frame + WebView backdrop to the web theme (light/dark).</summary>
    internal void SetChromeTheme(bool light)
    {
        var color = light
            ? System.Windows.Media.Color.FromRgb(0xEE, 0xF0, 0xF4)  // --bg light
            : System.Windows.Media.Color.FromRgb(0x07, 0x09, 0x0D); // --bg dark
        Background = new System.Windows.Media.SolidColorBrush(color);
        if (Web.CoreWebView2 is not null)
        {
            Web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(color.R, color.G, color.B);
        }
    }

    // ─────────────────────────── window verbs (from the web chrome) ───────────────────────────

    internal void WebMinimize() => WindowState = WindowState.Minimized;

    internal void WebMaximizeRestore() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    internal void WebClose() => Close();

    // ─────────────────────────── tray + hotkey ───────────────────────────

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

        // A dedicated message-only HwndSource: alive from construction (works while hidden
        // in the tray), independent of whether the visible window's handle exists yet.
        _hotkeySource = new HwndSource(new HwndSourceParameters("StepWind.Hotkey")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            HwndSourceHook = WndProc,
        });
        // No-repeat so holding the keys doesn't spam; failure is non-fatal (another app may
        // already own the combo) — the tray menu's "Oh no" item still works.
        _hotkeyRegistered = RegisterHotKey(_hotkeySource.Handle, HotkeyId, ModControl | ModShift | ModNoRepeat, VkZ);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WmHotkey = 0x0312;
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            ShowFromTray(navigateTimeline: true);
            handled = true;
        }

        return IntPtr.Zero;
    }

    private async void ShowFromTray(bool navigateTimeline = false)
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false; // bounce to the foreground without staying pinned

        await InitWebAsync(); // first Show after a --minimized start initializes the web layer
        if (navigateTimeline && Web.CoreWebView2 is not null)
        {
            await Web.CoreWebView2.ExecuteScriptAsync("typeof navigate==='function' && navigate('timeline')");
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

    private void OnOhNo(object sender, RoutedEventArgs e) => ShowFromTray(navigateTimeline: true);

    private void OnTrayExit(object sender, RoutedEventArgs e)
    {
        _reallyExit = true;
        if (_hotkeyRegistered && _hotkeySource is not null)
        {
            try { UnregisterHotKey(_hotkeySource.Handle, HotkeyId); _hotkeySource.Dispose(); } catch { }
        }

        _statusMonitor.Dispose();
        Tray.Dispose();
        Close();
        Application.Current.Shutdown();
    }
}
