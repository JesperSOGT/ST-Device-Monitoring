using Microsoft.Win32;

namespace ST_Device_Monitoring.Controls;

/// <summary>
/// Registers the application under HKCU\Software\Microsoft\Windows\CurrentVersion\Run so it
/// starts when the current user logs on. No administrator rights needed.
/// (For running without a logged-in user, install the Windows service instead.)
/// </summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ST Device Monitoring";

    public static bool IsRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(ValueName) != null;
        }
        catch
        {
            return false;
        }
    }

    public static string? Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (key == null) return "Could not open the Windows startup registry key.";

            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return "Could not determine the path to the executable.";
                key.SetValue(ValueName, $"\"{exe}\"");
            }
            else if (key.GetValue(ValueName) != null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
