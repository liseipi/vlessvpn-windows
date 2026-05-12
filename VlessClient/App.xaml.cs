using System;
using System.Linq;
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

    private TaskbarIcon?    _trayIcon;
    private MenuFlyoutItem? _toggleMenuItem;   // 需要动态更新文字

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

        bool startMinimized = Environment.GetCommandLineArgs().Contains("--minimized")
                              || Settings.Settings.StartMinimized;
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
        // ms-appx:// 在 unpackaged 应用中会崩溃，用绝对文件路径加载 .ico
        BitmapImage? iconSource = null;
        try
        {
            var icoPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "tray.ico");
            if (System.IO.File.Exists(icoPath))
                iconSource = new BitmapImage(new Uri(icoPath));
        }
        catch { /* 图标加载失败不影响功能 */ }

        _trayIcon = new TaskbarIcon
        {
            ToolTipText        = "VLESS Client",
            IconSource         = iconSource,
            DoubleClickCommand = new RelayCommand(() => ShowMainWindow()),
        };

        // ── 右键菜单（标准 WinUI MenuFlyout / MenuFlyoutItem）─────────────
        var showItem   = new MenuFlyoutItem { Text = "显示主窗口" };
        showItem.Click += (_, _) => ShowMainWindow();

        _toggleMenuItem = new MenuFlyoutItem { Text = "开始连接" };
        _toggleMenuItem.Click += async (_, _) => await ToggleProxyAsync();

        var exitItem = new MenuFlyoutItem { Text = "退出" };
        exitItem.Click += async (_, _) => await ExitAsync();

        var menu = new MenuFlyout();
        menu.Items.Add(showItem);
        menu.Items.Add(_toggleMenuItem);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(exitItem);

        _trayIcon.ContextFlyout = menu;

        // unpackaged WinUI 应用必须显式调用 ForceCreate，否则托盘图标不会出现
        _trayIcon.ForceCreate(enablesEfficiencyMode: false);

        // ── 监听代理状态，更新提示文字和菜单项 ───────────────────────────
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
                _trayIcon!.ToolTipText    = $"VLESS Client — {label}";
                _toggleMenuItem!.Text     = status == ProxyStatus.Running ? "断开连接" : "开始连接";
            });
        };
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
