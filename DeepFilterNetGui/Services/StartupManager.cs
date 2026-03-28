using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace DeepFilterNetGui.Services;

internal static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsEnabled(string appName)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            var value = key?.GetValue(appName) as string;
            if (string.IsNullOrWhiteSpace(value))
                return false;

            string exePath = GetCurrentExePath();
            if (string.IsNullOrWhiteSpace(exePath))
                return true;

            string stored = NormalizePath(value);
            return string.Equals(stored, exePath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            AppLogger.Warning($"检测开机启动失败: {ex.Message}");
            return false;
        }
    }

    public static bool Enable(string appName)
    {
        try
        {
            string exePath = GetCurrentExePath();
            if (string.IsNullOrWhiteSpace(exePath))
            {
                AppLogger.Warning("无法获取程序路径，无法启用开机启动。");
                return false;
            }

            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true) ??
                            Registry.CurrentUser.CreateSubKey(RunKey, true);
            key.SetValue(appName, Quote(exePath));
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Warning($"启用开机启动失败: {ex.Message}");
            return false;
        }
    }

    public static bool Disable(string appName)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            key?.DeleteValue(appName, false);
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Warning($"关闭开机启动失败: {ex.Message}");
            return false;
        }
    }

    private static string GetCurrentExePath()
    {
        string? path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            path = Process.GetCurrentProcess().MainModule?.FileName;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            path = System.Reflection.Assembly.GetEntryAssembly()?.Location;
        }

        return string.IsNullOrWhiteSpace(path) ? string.Empty : NormalizePath(path);
    }

    private static string Quote(string path)
    {
        return path.Contains(' ') ? $"\"{path}\"" : path;
    }

    private static string NormalizePath(string value)
    {
        string trimmed = value.Trim().Trim('"');
        try
        {
            return Path.GetFullPath(trimmed);
        }
        catch
        {
            return trimmed;
        }
    }
}

