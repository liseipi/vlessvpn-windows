using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using VlessClient.Core;
using VlessClient.Models;

namespace VlessClient.Services;

public enum ProxyStatus { Stopped, Starting, Running, Stopping, Error }

public class ProxyManager : IDisposable, IAsyncDisposable
{
    private VlessProxyService? _proxy;
    private TunService?        _tun;
    private VlessConfig?       _currentConfig;
    private bool               _tunEnabled;

    public ProxyStatus Status { get; private set; } = ProxyStatus.Stopped;
    public int ActiveConnections { get; private set; }

    public event Action<ProxyStatus>? StatusChanged;
    public event Action<string>?      LogMessage;
    public event Action<int>?         ConnectionCountChanged;

    // ── 启动 ────────────────────────────────────────────────────────────────

    public async Task StartAsync(VlessConfig config, bool enableTun)
    {
        if (Status == ProxyStatus.Running || Status == ProxyStatus.Starting) return;

        SetStatus(ProxyStatus.Starting);
        _currentConfig = config;
        _tunEnabled     = enableTun;

        try
        {
            // 1. 启动 SOCKS5/HTTP 代理
            _proxy = new VlessProxyService(config);
            _proxy.LogMessage           += OnLog;
            _proxy.ConnectionCountChanged += c => { ActiveConnections = c; ConnectionCountChanged?.Invoke(c); };
            await _proxy.StartAsync();
            OnLog($"代理启动成功 → socks5://127.0.0.1:{config.ListenPort}");

            // 2. 可选：启动 TUN
            if (enableTun)
            {
                _tun = new TunService(config);
                _tun.LogMessage += OnLog;
                await _tun.StartAsync();
                OnLog("TUN 模式已启动（全局流量）");
            }

            SetStatus(ProxyStatus.Running);
        }
        catch (Exception ex)
        {
            OnLog($"启动失败: {ex.Message}");
            await StopInternalAsync();
            SetStatus(ProxyStatus.Error);
        }
    }

    // ── 停止 ────────────────────────────────────────────────────────────────

    public async Task StopAsync()
    {
        if (Status == ProxyStatus.Stopped || Status == ProxyStatus.Stopping) return;
        SetStatus(ProxyStatus.Stopping);
        await StopInternalAsync();
        SetStatus(ProxyStatus.Stopped);
    }

    private async Task StopInternalAsync()
    {
        if (_tun != null)
        {
            try { await _tun.StopAsync(); } catch { }
            _tun.Dispose();
            _tun = null;
        }
        if (_proxy != null)
        {
            try { await _proxy.StopAsync(); } catch { }
            _proxy.Dispose();
            _proxy = null;
        }
    }

    public async Task RestartAsync(VlessConfig config, bool enableTun)
    {
        await StopAsync();
        await StartAsync(config, enableTun);
    }

    private void SetStatus(ProxyStatus s) { Status = s; StatusChanged?.Invoke(s); }
    private void OnLog(string msg) => LogMessage?.Invoke($"[{DateTime.Now:HH:mm:ss}] {msg}");

    public void Dispose()
    {
        // 同步 Dispose 仅做尽力清理（不 await，避免死锁）
        // 推荐在应用退出时调用 DisposeAsync 或显式 await StopAsync()
        try { _tun?.Dispose(); } catch { }
        try { _proxy?.Dispose(); } catch { }
        _tun   = null;
        _proxy = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopInternalAsync();
    }
}
