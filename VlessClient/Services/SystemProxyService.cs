using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace VlessClient.Services;

/// <summary>
/// Windows 系统代理设置（Internet Settings）
/// </summary>
public static class SystemProxyService
{
    private const string RegPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(
        IntPtr hInternet,
        int dwOption,
        IntPtr lpBuffer,
        int dwBufferLength);

    private const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
    private const int INTERNET_OPTION_REFRESH          = 37;

    public static void Apply(string host, int port)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RegPath);
        key.SetValue("ProxyServer",   $"{host}:{port}");
        key.SetValue("ProxyEnable",   1);
        key.SetValue("ProxyOverride", "<local>");

        Broadcast();
    }

    public static void Disable()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RegPath);
        key.SetValue("ProxyEnable",   0);
        key.DeleteValue("ProxyServer",   false);
        key.DeleteValue("ProxyOverride", false);

        Broadcast();
    }

    private static void Broadcast()
    {
        InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH,          IntPtr.Zero, 0);
    }
}
