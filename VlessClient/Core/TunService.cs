using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using VlessClient.Models;

namespace VlessClient.Core;

public sealed class TunService : IDisposable
{
    private readonly VlessConfig _config;
    private Process? _process;
    private bool _disposed;
    private string? _originalGateway;
    private string? _serverIp;

    public event Action<string>? LogMessage;

    public bool IsRunning => _process?.HasExited == false;

    public TunService(VlessConfig config)
    {
        _config = config;
    }

    public async Task StartAsync()
    {
        if (IsRunning) return;

        // 在启动 TUN 之前解析服务器 IP，避免 DNS 走 TUN 的死循环
        _serverIp = ResolveServerIp();
        _originalGateway = GetDefaultGateway();
        Log($"VLESS 服务器: {_config.Server} → {_serverIp}");
        Log($"原默认网关: {_originalGateway}");

        // 先添加服务器直连路由，确保后续连接不会走 TUN 自身
        RunRoute("add", _serverIp, "255.255.255.255", _originalGateway!);
        Log($"添加直连路由: {_serverIp} → {_originalGateway}");

        string exePath = Path.Combine(AppContext.BaseDirectory, "hev-socks5-tunnel.exe");
        if (!File.Exists(exePath))
        {
            RunRoute("delete", _serverIp);
            throw new FileNotFoundException($"找不到 hev-socks5-tunnel.exe，请检查 runtimes 目录。路径: {exePath}");
        }

        string configPath = WriteYamlConfig();

        Log($"启动 hev-socks5-tunnel.exe...");
        Log($"配置文件: {configPath}");

        var startInfo = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = $"\"{configPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        _process = new Process { StartInfo = startInfo };
        _process.OutputDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) Log(e.Data); };
        _process.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) Log($"[ERR] {e.Data}"); };

        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        await Task.Delay(2000);
        if (_process.HasExited)
        {
            var errMsg = $"hev-socks5-tunnel 启动失败，退出码: {_process.ExitCode}。请检查 TUN 日志或驱动 (wintun.dll) 是否正常。";
            Log(errMsg);
            RunRoute("delete", _serverIp);
            throw new Exception(errMsg);
        }
        Log("hev-socks5-tunnel 已启动");

        await Task.Delay(500);
        SwitchDefaultRoute();
    }

    private void SwitchDefaultRoute()
    {
        // 先删后加，删失败则不加，防止出现两条默认路由
        RunRoute("delete", "0.0.0.0", "0.0.0.0", _originalGateway!);
        RunRoute("add", "0.0.0.0", "0.0.0.0", _config.TunAddress);
        Log($"默认路由已切换: 0.0.0.0/0 → {_config.TunAddress}");
        Log("TUN 路由配置完成，全局流量已接管");
    }

    public async Task StopAsync()
    {
        RemoveRoutes();

        if (_process == null || _process.HasExited)
        {
            RemoveTunAdapter();
            return;
        }

        try
        {
            Log("正在停止 TUN...");
            _process.Kill();
            await _process.WaitForExitAsync();
        }
        catch { }
        finally
        {
            _process?.Dispose();
            _process = null;
        }

        await Task.Delay(500);
        RemoveTunAdapter();
    }

    private void RemoveRoutes()
    {
        // 恢复默认网关，始终尝试
        if (_originalGateway != null)
        {
            try { RunRoute("delete", "0.0.0.0", "0.0.0.0", _config.TunAddress); }
            catch (Exception ex) { Log($"删除 TUN 默认路由失败: {ex.Message}"); }

            try { RunRoute("add", "0.0.0.0", "0.0.0.0", _originalGateway); }
            catch (Exception ex) { Log($"恢复默认路由失败: {ex.Message}"); }
        }

        // 删除直连路由
        if (_serverIp != null)
        {
            try { RunRoute("delete", _serverIp); }
            catch (Exception ex) { Log($"删除直连路由失败: {ex.Message}"); }
        }
    }

    private string ResolveServerIp()
    {
        // 如果已经是纯 IP 地址，直接使用
        if (IPAddress.TryParse(_config.Server, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
            return _config.Server;

        try
        {
            var addresses = Dns.GetHostAddresses(_config.Server);
            var ipv4 = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            if (ipv4 != null)
                return ipv4.ToString();
        }
        catch (Exception ex)
        {
            throw new Exception($"无法解析服务器域名 {_config.Server}: {ex.Message}");
        }

        throw new Exception($"无法解析服务器域名 {_config.Server}，未找到 IPv4 地址");
    }

    private static string GetDefaultGateway()
    {
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = "-NoProfile -Command \"(Get-NetRoute -DestinationPrefix '0.0.0.0/0' | Sort-Object RouteMetric | Select-Object -First 1).NextHop\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            }
        };
        proc.Start();
        var gw = proc.StandardOutput.ReadToEnd().Trim();
        proc.WaitForExit(3000);
        if (string.IsNullOrEmpty(gw))
            throw new Exception("无法获取默认网关");
        return gw;
    }

    private static void RunRoute(string action, string dest, string mask, string gateway)
    {
        var args = $"{action} {dest} mask {mask} {gateway}";

        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "route",
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            }
        };
        proc.Start();
        proc.WaitForExit(3000);
        if (proc.ExitCode != 0)
        {
            var err = proc.StandardError.ReadToEnd().Trim();
            throw new Exception($"route {args} 失败 (exit {proc.ExitCode}): {err}");
        }
    }

    private void RemoveTunAdapter()
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "powershell",
                    Arguments = "-NoProfile -Command \"Get-NetAdapter -Name 'VlessTUN' -ErrorAction SilentlyContinue | Remove-NetAdapter -Confirm:$false\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                }
            };
            proc.Start();
            proc.WaitForExit(5000);
            if (proc.ExitCode == 0)
                Log("VlessTUN 网卡已移除");
        }
        catch (Exception ex)
        {
            Log($"移除 TUN 网卡时出错: {ex.Message}");
        }
    }

    private static void RunRoute(string action, string dest)
    {
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "route",
                Arguments = $"{action} {dest}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            }
        };
        proc.Start();
        proc.WaitForExit(3000);
        if (proc.ExitCode != 0)
        {
            var err = proc.StandardError.ReadToEnd().Trim();
            throw new Exception($"route {action} {dest} 失败 (exit {proc.ExitCode}): {err}");
        }
    }

    // ── YAML 配置 ─────────────────────────────────────────────────────────

    private string WriteYamlConfig()
    {
        var yaml = new StringBuilder();
        yaml.AppendLine("tunnel:");
        yaml.AppendLine("  name: VlessTUN");
        yaml.AppendLine("  mtu: 1500");
        yaml.AppendLine($"  ipv4: {_config.TunAddress}");

        yaml.AppendLine("socks5:");
        yaml.AppendLine($"  address: 127.0.0.1");
        yaml.AppendLine($"  port: {_config.ListenPort}");
        yaml.AppendLine("  udp: full");

        var logFile = Path.Combine(Path.GetTempPath(), "vless-tun.log").Replace("\\", "/");
        yaml.AppendLine("misc:");
        yaml.AppendLine("  log-level: debug");
        yaml.AppendLine($"  log-file: {logFile}");

        var path = Path.Combine(Path.GetTempPath(), $"vless-tun-{DateTime.Now:HHmmss}.yaml");
        File.WriteAllText(path, yaml.ToString(), new UTF8Encoding(false));
        return path;
    }

    private void Log(string msg) => LogMessage?.Invoke($"[TUN] {msg}");

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ = StopAsync();
    }
}
