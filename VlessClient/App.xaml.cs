using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Input;
using H.NotifyIcon;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using VlessClient.Services;

namespace VlessClient;

public partial class App : Application
{
    // new 关键字消除 CS0108 警告
    public static new App          Current  => (App)Application.Current;
    public static MainWindow?      MainWin  { get; private set; }
    public static ProxyManager     Proxy    { get; } = new();
    public static SettingsService  Settings { get; } = new();

    private TaskbarIcon? _trayIcon;
    private string       _toggleMenuText = "开始连接"; // 本地存储，原生菜单不再有 MenuFlyoutItem 引用

    // ═══ Win32 原生弹出菜单 P/Invoke ═══════════════════════════════════════
    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, uint uIDNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    private const uint MF_STRING    = 0x00000000;
    private const uint MF_SEPARATOR = 0x00000800;
    private const uint TPM_RETURNCMD  = 0x0100;
    private const uint TPM_NONOTIFY   = 0x0080;
    private const uint TPM_RIGHTALIGN = 0x0008;

    // ═══════════════════════════════════════════════════════════════════════

    public App()
    {
        // ── 全局异常捕获，写入日志防止闪退无痕 ──────────────────────────
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            WriteCrashLog("UnhandledException", e.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteCrashLog("UnobservedTaskException", e.Exception);
            e.SetObserved();
        };

        this.UnhandledException += (_, e) =>
        {
            e.Handled = true;
            WriteCrashLog("App.UnhandledException", e.Exception);
        };

        InitializeComponent();
    }

    private static void WriteCrashLog(string source, Exception? ex)
    {
        try
        {
            var path = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                "VlessClient_crash.log");
            var msg = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}]\n{ex}\n\n";
            System.IO.File.AppendAllText(path, msg);
        }
        catch { }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            OnLaunchedCore(args);
        }
        catch (Exception ex)
        {
            WriteCrashLog("OnLaunched", ex);
            // 显示一个最简错误窗口，避免完全无声退出
            ShowCrashDialog(ex.Message);
        }
    }

    private void OnLaunchedCore(LaunchActivatedEventArgs args)
    {
        MainWin = new MainWindow();

        bool startMinimized = Environment.GetCommandLineArgs().Contains("--minimized");
        if (!startMinimized)
            MainWin.Activate();

        SetupTrayIcon();

        if (startMinimized)
            MainWin.HideWindow();
    }   // end OnLaunchedCore

    private static void ShowCrashDialog(string message)
    {
        // 框架可能已损坏，用记事本打开日志作为兜底提示
        var logPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            "VlessClient_crash.log");
        try { System.Diagnostics.Process.Start("notepad.exe", logPath); } catch { }
    }

    // ── 托盘图标 ──────────────────────────────────────────────────────────────

    private void SetupTrayIcon()
    {
        BitmapImage? iconSource = null;
        try
        {
            var icoPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (System.IO.File.Exists(icoPath))
                iconSource = new BitmapImage(new Uri(icoPath));
        }
        catch { }

        _trayIcon = new TaskbarIcon
        {
            ToolTipText        = "VLESS Client",
            IconSource         = iconSource,
            LeftClickCommand   = new RelayCommand(() => ShowMainWindow()),
            DoubleClickCommand = new RelayCommand(() => ShowMainWindow()),
            RightClickCommand  = new RelayCommand(ShowNativeContextMenu),
        };

        _trayIcon.ForceCreate(enablesEfficiencyMode: false);

        Proxy.StatusChanged += status =>
        {
            MainWin?.DispatcherQueue.TryEnqueue(() =>
            {
                var label = status switch
                {
                    ProxyStatus.Running  => "已连接",
                    ProxyStatus.Starting => "连接中...",
                    ProxyStatus.Stopping => "断开中...",
                    ProxyStatus.Error    => "错误",
                    _                   => "已断开"
                };
                _trayIcon!.ToolTipText = $"VLESS Client — {label}";
                _toggleMenuText        = status == ProxyStatus.Running ? "断开连接" : "开始连接";
            });
        };
    }

    // ── 原生 Win32 右键菜单 ────────────────────────────────────────────────────

    private async void ShowNativeContextMenu()
    {
        IntPtr hMenu = CreatePopupMenu();
        AppendMenuW(hMenu, MF_STRING, 1, "显示主窗口");
        AppendMenuW(hMenu, MF_STRING, 2, _toggleMenuText);
        AppendMenuW(hMenu, MF_SEPARATOR, 0, "");
        AppendMenuW(hMenu, MF_STRING, 3, "退出");

        GetCursorPos(out var pt);

        // TrackPopupMenu 需要置于前台，否则点击空白处无法关闭菜单
        IntPtr hwnd = IntPtr.Zero;
        if (MainWin != null)
            hwnd = WinRT.Interop.WindowNative.GetWindowHandle(MainWin);
        SetForegroundWindow(hwnd);

        uint flags = TPM_RETURNCMD | TPM_NONOTIFY | TPM_RIGHTALIGN;
        int result = TrackPopupMenu(hMenu, flags, pt.X, pt.Y, 0, hwnd, IntPtr.Zero);
        DestroyMenu(hMenu);

        switch (result)
        {
            case 1: ShowMainWindow(); break;
            case 2: await ToggleProxyAsync(); break;
            case 3: await ExitAsync(); break;
            case 0: break; // 用户点击菜单外部取消
        }
    }

    // ── 辅助方法 ──────────────────────────────────────────────────────────────

    private void ShowMainWindow()
    {
        MainWin?.ShowWindow();
        MainWin?.Activate();
        MainWin?.BringToFront();
    }

    private async Task ToggleProxyAsync()
    {
        if (Proxy.Status == ProxyStatus.Running)
            await Proxy.StopAsync();
        else
            await StartProxyFromTray();
    }

    private async Task ExitAsync()
    {
        if (MainWin != null)
            MainWin.IsExiting = true;
        await Proxy.StopAsync();
        _trayIcon?.Dispose();
        await Proxy.DisposeAsync();
        MainWin?.Close();
    }

    private async Task StartProxyFromTray()
    {
        var s = Settings.Settings;
        if (s.Configs.Count == 0) { ShowMainWindow(); return; }
        int idx = Math.Clamp(s.SelectedIndex, 0, s.Configs.Count - 1);
        await Proxy.StartAsync(s.Configs[idx], s.EnableTun);
    }

    public TaskbarIcon? TrayIcon => _trayIcon;
}

// ── 轻量 ICommand 实现（避免引入 MVVM 框架）────────────────────────────────
internal sealed class RelayCommand(Action execute) : ICommand
{
    // 实现接口要求，用 _ 前缀明确表示此事件不会触发（always-enabled 命令）
#pragma warning disable CS0067
    public event EventHandler? CanExecuteChanged;
#pragma warning restore CS0067
    public bool CanExecute(object? _) => true;
    public void Execute(object? _)    => execute();
}
