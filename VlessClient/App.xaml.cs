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

    public App() { InitializeComponent(); }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWin = new MainWindow();

        bool startMinimized = Environment.GetCommandLineArgs().Contains("--minimized")
                              || Settings.Settings.StartMinimized;
        if (!startMinimized)
            MainWin.Activate();

        SetupTrayIcon();

        if (startMinimized)
            MainWin.HideWindow();
    }

    // ── 托盘图标 ──────────────────────────────────────────────────────────────

    private void SetupTrayIcon()
    {
        // IconSource 是 ImageSource，用 BitmapImage 加载打包资源中的 .ico
        var iconSource = new BitmapImage(new Uri("ms-appx:///Assets/tray.ico"));

        _trayIcon = new TaskbarIcon
        {
            ToolTipText    = "VLESS Client",
            IconSource     = iconSource,
            // 双击图标还原窗口（DoubleClickCommand 接受 ICommand）
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
