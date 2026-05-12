using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
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
        string configPath = WriteYamlConfig();

        Log($"启动 hev-socks5-tunnel.exe...");
        Log($"配置文件: {configPath}");

        var startInfo = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = $"-c \"{configPath}\"",
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

        await Task.Delay(1500); // 等待启动
        Log("hev-socks5-tunnel 已启动");
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
        yaml.AppendLine("  mtu: 8500");
        yaml.AppendLine($"  ipv4: {_config.TunAddress}/{_config.TunPrefix}");
        yaml.AppendLine("  auto-route: false");
        yaml.AppendLine("  strict-route: false");

        yaml.AppendLine("socks5:");
        yaml.AppendLine($"  address: 127.0.0.1");
        yaml.AppendLine($"  port: {_config.ListenPort}");
        yaml.AppendLine("  udp: full");

        yaml.AppendLine("misc:");
        yaml.AppendLine("  log-level: warn");
        yaml.AppendLine($"  log-file: /dev/stderr");

        var path = Path.Combine(Path.GetTempPath(), $"vless-tun-{DateTime.Now:HHmmss}.yaml");
        File.WriteAllText(path, yaml.ToString(), Encoding.UTF8);
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