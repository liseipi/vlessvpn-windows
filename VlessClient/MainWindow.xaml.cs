using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using VlessClient.Models;
using VlessClient.Services;
using Windows.UI;

namespace VlessClient;

public sealed partial class MainWindow : Window
{
    private readonly ObservableCollection<VlessConfig> _configs = new();
    private bool _suppressEvents;
    private AppWindow? _appWindow;
    private IntPtr _hwnd;
    private int _logLineCount;
    private const int MaxLogLines = 500;

    // 用于退出标志 — 真正的退出不再被 OnWindowClosing 拦截
    internal bool IsExiting { get; set; }

    [DllImport("user32.dll", EntryPoint = "ShowWindow")]
    private static extern bool NativeShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;

    public MainWindow()
    {
        InitializeComponent();
        SetupWindow();
        BindEvents();
        LoadSettings();
    }

    // ── 窗口初始化 ────────────────────────────────────────────────────────────

    private void SetupWindow()
    {
        // 自定义标题栏 & Mica 材质
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // 缓存 HWND 和 AppWindow 用于 Show/Hide/Close 控制
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var winId = Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(winId);
        _appWindow.Resize(new Windows.Graphics.SizeInt32(680, 680));

        // 设置窗口图标 + 标题栏图标
        var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (System.IO.File.Exists(iconPath))
        {
            _appWindow.SetIcon(iconPath);
            TitleBarIcon.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(iconPath));
        }

        _appWindow.Closing += OnWindowClosing;
    }

    private void BindEvents()
    {
        App.Proxy.StatusChanged += OnProxyStatusChanged;
        App.Proxy.LogMessage += OnProxyLog;
        App.Proxy.ConnectionCountChanged += OnConnectionCountChanged;
    }

    private void LoadSettings()
    {
        _suppressEvents = true;
        var s = App.Settings.Settings;

        // 配置列表
        _configs.Clear();
        foreach (var c in s.Configs) _configs.Add(c);
        ConfigSelector.ItemsSource = _configs;
        ConfigSelector.DisplayMemberPath = "Name";

        if (_configs.Count > 0)
        {
            int idx = Math.Clamp(s.SelectedIndex, 0, _configs.Count - 1);
            ConfigSelector.SelectedIndex = idx;
            ShowConfigDetail(_configs[idx]);
        }

        // 端口
        Socks5PortBox.Value = s.ListenPort;

        // LAN 共享
        ShareOverLanToggle.IsOn = s.ShareOverLan;

        // 系统代理
        SystemProxyToggle.IsOn = s.EnableSystemProxy;

        // TUN
        TunToggle.IsOn = s.EnableTun;
        TunSettings.Visibility = s.EnableTun ? Visibility.Visible : Visibility.Collapsed;
        TunAddressBox.Text = s.TunAddress;
        TunPrefixBox.Value = s.TunPrefix;
        TunDnsBox.Text = s.TunDns;

        // 系统
        AutoStartToggle.IsOn = s.AutoStart;
        MinimizeToTrayToggle.IsOn = s.StartMinimized;

        _suppressEvents = false;
    }

    // ── 开关按钮 ──────────────────────────────────────────────────────────────

    private async void ToggleButton_Click(object sender, RoutedEventArgs e)
    {
        var proxy = App.Proxy;
        if (proxy.Status == ProxyStatus.Running || proxy.Status == ProxyStatus.Starting)
        {
            if (SystemProxyToggle.IsOn)
                SystemProxyService.Disable();
            await proxy.StopAsync();
        }
        else
        {
            var cfg = GetSelectedConfig();
            if (cfg == null)
            {
                await ShowErrorAsync("请先选择或添加一个服务器配置");
                return;
            }

            // 应用当前端口设置
            cfg = cfg.Clone();
            cfg.ListenPort = (int)Socks5PortBox.Value;
            cfg.ShareOverLan = ShareOverLanToggle.IsOn;
            cfg.EnableTun = TunToggle.IsOn;
            cfg.TunAddress = TunAddressBox.Text;
            cfg.TunPrefix = (int)TunPrefixBox.Value;
            cfg.TunDns = TunDnsBox.Text;

            await proxy.StartAsync(cfg, TunToggle.IsOn);

            if (SystemProxyToggle.IsOn)
                SystemProxyService.Apply("127.0.0.1", (int)Socks5PortBox.Value);
        }
    }

    // ── 状态回调 ──────────────────────────────────────────────────────────────

    private void OnProxyStatusChanged(ProxyStatus status)
    {
        // 代理断开时自动关闭系统代理
        if (status == ProxyStatus.Stopped || status == ProxyStatus.Error)
        {
            try { SystemProxyService.Disable(); }
            catch { /* 静默处理，避免 shutdown 期间异常 */ }
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            StatusText.Text = status switch
            {
                ProxyStatus.Running => "已连接",
                ProxyStatus.Starting => "连接中...",
                ProxyStatus.Stopping => "断开中...",
                ProxyStatus.Error => "连接错误",
                _ => "未连接"
            };

            var color = status switch
            {
                ProxyStatus.Running => Color.FromArgb(255, 34, 197, 94),   // green
                ProxyStatus.Starting => Color.FromArgb(255, 245, 158, 11),  // amber
                ProxyStatus.Error => Color.FromArgb(255, 239, 68, 68),   // red
                _ => Color.FromArgb(255, 239, 68, 68)     // red
            };
            StatusDot.Background = new SolidColorBrush(color);
            FooterDot.Background = new SolidColorBrush(color);

            // 开关按钮
            bool running = status == ProxyStatus.Running;
            ToggleButton.Background = new SolidColorBrush(running
                ? Color.FromArgb(255, 34, 197, 94)
                : Color.FromArgb(255, 239, 68, 68));
            ToggleIcon.Glyph = running ? "\uE71A" : "\uE768";   // Stop / Play
            ToggleButton.IsEnabled = status != ProxyStatus.Starting && status != ProxyStatus.Stopping;

            FooterText.Text = status switch
            {
                ProxyStatus.Running => $"代理运行中 → socks5://127.0.0.1:{(int)Socks5PortBox.Value}",
                ProxyStatus.Starting => "正在启动...",
                ProxyStatus.Error => "启动失败，请检查配置",
                _ => "就绪"
            };
        });
    }

    private void OnConnectionCountChanged(int count)
        => DispatcherQueue.TryEnqueue(() =>
            ConnectionCountText.Text = $"活动连接: {count}");

    private void OnProxyLog(string msg)
        => DispatcherQueue.TryEnqueue(() => AppendLog(msg));

    private void AppendLog(string msg)
    {
        _logLineCount++;
        if (_logLineCount > MaxLogLines)
        {
            var text = LogText.Text;
            var newlineIdx = text.IndexOf('\n', text.Length / 2);
            LogText.Text = newlineIdx > 0 ? text[(newlineIdx + 1)..] : "";
            _logLineCount = MaxLogLines / 2;
        }
        LogText.Text += msg + "\n";

        // 自动滚动：UpdateLayoutAsync 在 WinUI 不存在，改用 DispatcherQueue 延迟执行
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            LogScroller.ScrollToVerticalOffset(LogScroller.ScrollableHeight);
        });
    }

    private void ClearLogBtn_Click(object sender, RoutedEventArgs e)
    {
        LogText.Text = "";
        _logLineCount = 0;
    }

    // ── 配置管理 ──────────────────────────────────────────────────────────────

    private void ConfigSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        var cfg = GetSelectedConfig();
        if (cfg != null)
        {
            ShowConfigDetail(cfg);
            App.Settings.Settings.SelectedIndex = ConfigSelector.SelectedIndex;
            _ = App.Settings.SaveAsync();
        }
        else
        {
            ConfigDetail.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowConfigDetail(VlessConfig cfg)
    {
        ConfigDetail.Visibility = Visibility.Visible;
        DetailServer.Text = cfg.Server;
        DetailPort.Text = cfg.Port.ToString();
        DetailNetwork.Text = $"{cfg.Network.ToUpperInvariant()} / {cfg.Encryption}";
        DetailSecurity.Text = cfg.Security.ToUpperInvariant();
        DetailListenPort.Text = $":{cfg.ListenPort}";
    }

    private async void ImportBtn_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "导入 VLESS 配置",
            PrimaryButtonText = "导入",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot
        };

        var tb = new TextBox
        {
            PlaceholderText = "粘贴 vless:// 链接（每行一个）",
            AcceptsReturn = true,
            Height = 160,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12
        };
        dialog.Content = tb;

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        int imported = 0, failed = 0;
        foreach (var line in tb.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (VlessConfig.TryParse(line, out var cfg, out _))
            {
                _configs.Add(cfg);
                App.Settings.Settings.Configs.Add(cfg);
                imported++;
            }
            else { failed++; }
        }
        await App.Settings.SaveAsync();

        if (_configs.Count > 0 && ConfigSelector.SelectedIndex < 0)
            ConfigSelector.SelectedIndex = 0;

        AppendLog($"导入完成: 成功 {imported}，失败 {failed}");
    }

    private async void ExportBtn_Click(object sender, RoutedEventArgs e)
    {
        var cfg = GetSelectedConfig();
        if (cfg == null) { await ShowErrorAsync("请先选择配置"); return; }

        var uri = cfg.ToUri();
        var dialog = new ContentDialog
        {
            Title = "导出配置",
            PrimaryButtonText = "复制",
            CloseButtonText = "关闭",
            XamlRoot = Content.XamlRoot
        };
        var tb = new TextBox
        {
            Text = uri,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11
        };
        dialog.Content = tb;
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetText(uri);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
        }
    }

    private async void NewConfigBtn_Click(object sender, RoutedEventArgs e)
        => await ShowConfigEditorAsync(null);

    private async void EditConfigBtn_Click(object sender, RoutedEventArgs e)
        => await ShowConfigEditorAsync(GetSelectedConfig());

    private async void DeleteConfigBtn_Click(object sender, RoutedEventArgs e)
    {
        var cfg = GetSelectedConfig();
        if (cfg == null) return;

        var dialog = new ContentDialog
        {
            Title = "删除配置",
            Content = $"确定删除「{cfg.Name}」？",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        App.Settings.Settings.Configs.Remove(cfg);
        _configs.Remove(cfg);
        await App.Settings.SaveAsync();
        ConfigDetail.Visibility = Visibility.Collapsed;
    }

    // ── 配置编辑器 ────────────────────────────────────────────────────────────

    private async Task ShowConfigEditorAsync(VlessConfig? existing)
    {
        var dialog = new ContentDialog
        {
            Title = existing == null ? "新建配置" : "编辑配置",
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot
        };

        var panel = new StackPanel { Spacing = 12, Width = 400 };

        TextBox MakeField(string label, string val)
        {
            var sp = new StackPanel { Spacing = 4 };
            sp.Children.Add(new TextBlock { Text = label, FontSize = 12 });
            var tb = new TextBox { Text = val };
            sp.Children.Add(tb);
            panel.Children.Add(sp);
            return tb;
        }

        var nameBox = MakeField("配置名称", existing?.Name ?? "");
        var uriBox = MakeField("VLESS URI (可选，填入后点保存自动解析)", "");

        panel.Children.Add(new TextBlock
        {
            Text = "— 或手动填写 —",
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromArgb(120, 128, 128, 128))
        });

        var serverBox = MakeField("服务器", existing?.Server ?? "");
        var portBox = MakeField("端口", (existing?.Port ?? 443).ToString());
        var uuidBox = MakeField("UUID", existing?.Uuid ?? "");
        var pathBox = MakeField("WS 路径", existing?.Path ?? "/?ed=2560");
        var sniBox = MakeField("SNI", existing?.Sni ?? "");
        var hostBox = MakeField("WS Host", existing?.WsHost ?? "");

        dialog.Content = new ScrollViewer { Content = panel, MaxHeight = 500 };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        VlessConfig cfg;
        // 如果填了 URI，优先解析
        if (!string.IsNullOrWhiteSpace(uriBox.Text))
        {
            if (!VlessConfig.TryParse(uriBox.Text, out cfg, out var err))
            {
                await ShowErrorAsync($"URI 解析失败: {err}");
                return;
            }
        }
        else
        {
            cfg = existing?.Clone() ?? new VlessConfig();
            cfg.Server = serverBox.Text.Trim();
            cfg.Uuid = uuidBox.Text.Trim();
            cfg.Path = pathBox.Text.Trim();
            cfg.Sni = sniBox.Text.Trim();
            cfg.WsHost = hostBox.Text.Trim();
            if (!int.TryParse(portBox.Text, out var p)) { await ShowErrorAsync("端口无效"); return; }
            cfg.Port = p;
        }

        if (!string.IsNullOrWhiteSpace(nameBox.Text))
            cfg.Name = nameBox.Text.Trim();

        if (existing != null)
        {
            int idx = App.Settings.Settings.Configs.IndexOf(existing);
            if (idx >= 0) App.Settings.Settings.Configs[idx] = cfg;
            int uiIdx = _configs.IndexOf(existing);
            if (uiIdx >= 0) _configs[uiIdx] = cfg;
            ShowConfigDetail(cfg);
        }
        else
        {
            App.Settings.Settings.Configs.Add(cfg);
            _configs.Add(cfg);
            ConfigSelector.SelectedIndex = _configs.Count - 1;
        }
        await App.Settings.SaveAsync();
    }

    // ── LAN 共享 ──────────────────────────────────────────────────────────────

    private void ShareOverLanToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        App.Settings.Settings.ShareOverLan = ShareOverLanToggle.IsOn;
        _ = App.Settings.SaveAsync();
    }

    // ── 系统代理 ────────────────────────────────────────────────────────────

    private void SystemProxyToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        App.Settings.Settings.EnableSystemProxy = SystemProxyToggle.IsOn;
        _ = App.Settings.SaveAsync();

        // 代理已运行时实时生效
        if (App.Proxy.Status == ProxyStatus.Running)
        {
            if (SystemProxyToggle.IsOn)
                SystemProxyService.Apply("127.0.0.1", (int)Socks5PortBox.Value);
            else
                SystemProxyService.Disable();
        }
    }

    // ── TUN 设置 ──────────────────────────────────────────────────────────────

    private void TunToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        TunSettings.Visibility = TunToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
        App.Settings.Settings.EnableTun = TunToggle.IsOn;
        _ = App.Settings.SaveAsync();
    }

    // ── 系统设置 ──────────────────────────────────────────────────────────────

    private async void AutoStartToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        var (success, error) = AutoStartService.SetEnabled(AutoStartToggle.IsOn);
        if (!success)
        {
            // 回滚开关状态并提示
            _suppressEvents = true;
            AutoStartToggle.IsOn = !AutoStartToggle.IsOn;
            _suppressEvents = false;
            await ShowErrorAsync($"开机自启设置失败: {error}");
            return;
        }
        App.Settings.Settings.AutoStart = AutoStartToggle.IsOn;
        _ = App.Settings.SaveAsync();
    }

    private void MinimizeToTrayToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        App.Settings.Settings.StartMinimized = MinimizeToTrayToggle.IsOn;
        _ = App.Settings.SaveAsync();
    }

    // ── 窗口关闭 → 最小化到托盘 ──────────────────────────────────────────────

    private void OnWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (IsExiting)
            return; // 真正退出，不拦截
        args.Cancel = true;
        HideWindow();
    }

    /// <summary>隐藏窗口到托盘（使用原生 Win32 API）</summary>
    public void HideWindow() => NativeShowWindow(_hwnd, SW_HIDE);

    /// <summary>从托盘还原窗口（使用原生 Win32 API）</summary>
    public void ShowWindow() => NativeShowWindow(_hwnd, SW_SHOW);

    public void BringToFront()
    {
        NativeShowWindow(_hwnd, SW_SHOW);
        _appWindow?.MoveInZOrderAtTop();
    }

    // ── 辅助 ─────────────────────────────────────────────────────────────────

    private VlessConfig? GetSelectedConfig()
    {
        var idx = ConfigSelector.SelectedIndex;
        return (idx >= 0 && idx < _configs.Count) ? _configs[idx] : null;
    }

    private async Task ShowErrorAsync(string msg)
    {
        var d = new ContentDialog
        {
            Title = "错误",
            Content = msg,
            CloseButtonText = "确定",
            XamlRoot = Content.XamlRoot
        };
        await d.ShowAsync();
    }
}
