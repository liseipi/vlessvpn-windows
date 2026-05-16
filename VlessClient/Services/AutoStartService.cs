using Microsoft.Win32;

namespace VlessClient.Services;

public static class AutoStartService
{
    private const string RegKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "VlessClient";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegKey);
                return key?.GetValue(AppName) != null;
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// 启用开机自启。失败时抛出异常，由调用方处理并告知用户。
    /// </summary>
    public static void Enable()
    {
        var exePath = Environment.ProcessPath
                      ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;

        if (string.IsNullOrWhiteSpace(exePath))
            throw new InvalidOperationException("无法获取当前程序路径，开机自启设置失败。");

        using var key = Registry.CurrentUser.OpenSubKey(RegKey, writable: true)
                        ?? throw new InvalidOperationException($"无法打开注册表项: {RegKey}");

        key.SetValue(AppName, $"\"{exePath}\" --minimized");
    }

    /// <summary>
    /// 禁用开机自启。失败时抛出异常，由调用方处理并告知用户。
    /// </summary>
    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegKey, writable: true)
                        ?? throw new InvalidOperationException($"无法打开注册表项: {RegKey}");

        key.DeleteValue(AppName, throwOnMissingValue: false);
    }

    /// <summary>
    /// 设置开机自启状态，返回操作是否成功及错误信息。
    /// </summary>
    public static (bool Success, string? Error) SetEnabled(bool enable)
    {
        try
        {
            if (enable) Enable(); else Disable();
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
