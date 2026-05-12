using System;
using Microsoft.Win32;

namespace VlessClient.Services;

public static class AutoStartService
{
    private const string RegKey  = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
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

    public static void Enable()
    {
        try
        {
            var exePath = Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
            using var key = Registry.CurrentUser.OpenSubKey(RegKey, writable: true);
            key?.SetValue(AppName, $"\"{exePath}\" --minimized");
        }
        catch { /* ignore */ }
    }

    public static void Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegKey, writable: true);
            key?.DeleteValue(AppName, throwOnMissingValue: false);
        }
        catch { /* ignore */ }
    }

    public static void SetEnabled(bool enable)
    {
        if (enable) Enable(); else Disable();
    }
}
