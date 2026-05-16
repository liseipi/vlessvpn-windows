using System.Diagnostics;
using System.Text;
using VlessClient.Models;

namespace VlessClient.Core;

public sealed class TunService : IDisposable
{
    private readonly VlessConfig _config;
    private Process? _process;
    private bool _disposed;

    public event Action<string>? LogMessage;

    public bool IsRunning => _process?.HasExited == false;

    public TunService(VlessConfig config)
    {
        _config = config;
    }

    public async Task StartAsync()
    {
        if (IsRunning) return;

        string exePath = Path.Combine(AppContext.BaseDirectory, "hev-socks5-tunnel.exe");
        if (!File.Exists(exePath))
            throw new FileNotFoundException($"找不到 hev-socks5-tunnel.exe，请检查 runtimes 目录。路径: {exePath}");

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

        // 等待启动并检测进程是否崩溃
        await Task.Delay(2000);
        if (_process.HasExited)
        {
            var errMsg = $"hev-socks5-tunnel 启动失败，退出码: {_process.ExitCode}。请检查 TUN 日志或驱动 (wintun.dll) 是否正常。";
            Log(errMsg);
            throw new Exception(errMsg);
        }
        Log("hev-socks5-tunnel 已启动，TUN 全局模式工作中");
    }

    public async Task StopAsync()
    {
        if (_process == null || _process.HasExited) return;

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
    }

    private string WriteYamlConfig()
    {
        var yaml = new StringBuilder();
        yaml.AppendLine("tunnel:");
        yaml.AppendLine("  name: VlessTUN");
        yaml.AppendLine("  mtu: 1500");
        yaml.AppendLine($"  ipv4: {_config.TunAddress}/{_config.TunPrefix}");
        yaml.AppendLine("  auto-route: true");
        yaml.AppendLine("  strict-route: true");
        yaml.AppendLine($"  dns: {_config.TunDns}");

        yaml.AppendLine("socks5:");
        yaml.AppendLine($"  address: 127.0.0.1");
        yaml.AppendLine($"  port: {_config.ListenPort}");
        yaml.AppendLine("  udp: full");

        // log-file 使用 Windows 可写路径（/dev/stderr 仅在 Linux 下有效）
        var logFile = Path.Combine(Path.GetTempPath(), "vless-tun.log").Replace("\\", "/");
        yaml.AppendLine("misc:");
        yaml.AppendLine("  log-level: warn");
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