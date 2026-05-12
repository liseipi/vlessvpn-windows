using System;
using System.Linq;
using System.Threading.Tasks;
using H.NotifyIcon;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using VlessClient.Services;

namespace VlessClient;

public partial class App : Application
{
    public static App         Current     => (App)Application.Current;
    public static MainWindow? MainWin     { get; private set; }
    public static ProxyManager Proxy      { get; } = new();
    public static SettingsService Settings { get; } = new();

    private TaskbarIcon? _trayIcon;

    public App() { InitializeComponent(); }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWin = new MainWindow();

        // 开机自启时最小化启动
        bool startMinimized = Environment.GetCommandLineArgs().Contains("--minimized")
                              || Settings.Settings.StartMinimized;
        if (!startMinimized)
            MainWin.Activate();

        // 系统托盘
        SetupTrayIcon();

        // 如果最小化启动，隐藏窗口
        if (startMinimized)
            MainWin.Hide();
    }

    // ── 托盘图标 ─────────────────────────────────────────────────────────────

    private void SetupTrayIcon()
    {
        _trayIcon = new TaskbarIcon
        {
            ToolTipText    = "VLESS Client",
            IconSource     = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(
                                 new Uri("ms-appx:///Assets/tray.ico")),
        };

        // 双击还原窗口
        _trayIcon.TrayMouseDoubleClick += (_, _) => ShowMainWindow();

        // 右键菜单
        var menu = new MenuFlyout();

        var showItem = new MenuFlyoutItem { Text = "显示主窗口" };
        showItem.Click += (_, _) => ShowMainWindow();

        var toggleItem = new MenuFlyoutItem { Text = "开始连接" };
        toggleItem.Click += async (_, _) =>
        {
            if (Proxy.Status == ProxyStatus.Running)
                await Proxy.StopAsync();
            else
                await StartProxyFromTray();
        };

        var exitItem = new MenuFlyoutItem { Text = "退出" };
        exitItem.Click += async (_, _) =>
        {
            await Proxy.StopAsync();
            _trayIcon?.Dispose();
            MainWin?.Close();
        };

        menu.Items.Add(showItem);
        menu.Items.Add(toggleItem);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(exitItem);
        _trayIcon.ContextFlyout = menu;

        // 监听状态变化更新托盘提示和菜单文字
        Proxy.StatusChanged += status =>
        {
            MainWin?.DispatcherQueue.TryEnqueue(() =>
            {
                var statusText = status switch
                {
                    ProxyStatus.Running  => "已连接",
                    ProxyStatus.Starting => "连接中...",
                    ProxyStatus.Stopping => "断开中...",
                    ProxyStatus.Error    => "错误",
                    _                   => "已断开"
                };
                _trayIcon!.ToolTipText = $"VLESS Client — {statusText}";
                toggleItem.Text        = status == ProxyStatus.Running ? "断开连接" : "开始连接";
            });
        };
    }

    private void ShowMainWindow()
    {
        MainWin?.Show();
        MainWin?.Activate();
        MainWin?.BringToFront();
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
