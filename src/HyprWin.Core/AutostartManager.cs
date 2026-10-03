using System.Diagnostics;
using Microsoft.Win32;

namespace HyprWin.Core;

/// <summary>
/// Manages HyprWin autostart via the current-user Run key.
/// HyprWin runs as the logged-in user (asInvoker), so HKCU Run is the correct path.
/// </summary>
public static class AutostartManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "HyprWin";

    /// <summary>
    /// Returns true if HyprWin is registered in HKCU\Software\Microsoft\Windows\CurrentVersion\Run.
    /// </summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(AppName) != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Enable autostart by writing the current executable path to the HKCU Run key.
    /// </summary>
    public static void Enable()
    {
        try
        {
            var exePath = GetExePath();
            if (exePath == null)
            {
                Logger.Instance.Warn("Cannot enable autostart: executable path unknown");
                return;
            }

            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (key != null)
            {
                key.SetValue(AppName, $"\"{exePath}\"");
                Logger.Instance.Info($"Autostart registry entry set: {exePath}");
            }
        }
        catch (Exception ex)
        {
            Logger.Instance.Error("Failed to enable autostart", ex);
        }
    }

    /// <summary>
    /// Disable autostart by removing the HKCU Run registry entry.
    /// </summary>
    public static void Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(AppName) != null)
            {
                key.DeleteValue(AppName, throwOnMissingValue: false);
                Logger.Instance.Info("Autostart registry entry removed");
            }
        }
        catch (Exception ex)
        {
            Logger.Instance.Error("Failed to disable autostart", ex);
        }
    }

    /// <summary>
    /// Set autostart state based on config value.
    /// </summary>
    public static void SetEnabled(bool enabled)
    {
        if (enabled)
            Enable();
        else
            Disable();
    }

    private static string? GetExePath()
    {
        return Environment.ProcessPath
            ?? Process.GetCurrentProcess().MainModule?.FileName;
    }
}
