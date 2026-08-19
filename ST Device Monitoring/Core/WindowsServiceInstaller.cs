using System.Diagnostics;
using System.IO;
using System.Text;

namespace ST_Device_Monitoring.Core;

public enum ServiceState
{
    NotInstalled,
    Stopped,
    Running,
    Other,
    Unknown
}

/// <summary>
/// Installs/removes/starts/stops the Windows service using sc.exe. Every operation that changes
/// the service requires administrator rights, so the commands are run elevated (UAC prompt).
/// </summary>
public static class WindowsServiceInstaller
{
    public static string ExecutablePath => Environment.ProcessPath
        ?? System.IO.Path.Combine(AppContext.BaseDirectory, "ST Device Monitoring.exe");

    public static ServiceState Query()
    {
        try
        {
            var (exitCode, output) = Run("sc.exe", $"query \"{WindowsServiceHost.ServiceName}\"", elevated: false);
            if (exitCode != 0) return ServiceState.NotInstalled;
            if (output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase)) return ServiceState.Running;
            if (output.Contains("STOPPED", StringComparison.OrdinalIgnoreCase)) return ServiceState.Stopped;
            return ServiceState.Other;
        }
        catch
        {
            return ServiceState.Unknown;
        }
    }

    public static string StateText(ServiceState state) => state switch
    {
        ServiceState.NotInstalled => "Not installed",
        ServiceState.Stopped => "Installed - stopped",
        ServiceState.Running => "Installed - running",
        ServiceState.Other => "Installed - changing state",
        _ => "Unknown"
    };

    /// <summary>Creates the service (auto start) and starts it. Shows a UAC prompt.</summary>
    public static (bool ok, string message) Install(bool autoStart = true)
    {
        var exe = ExecutablePath;
        var start = autoStart ? "auto" : "demand";
        var commands = new StringBuilder()
            .Append($"sc create \"{WindowsServiceHost.ServiceName}\" binPath= \"\\\"{exe}\\\" --service\" ")
            .Append($"start= {start} DisplayName= \"{WindowsServiceHost.ServiceDisplayName}\"")
            .Append(" & ")
            .Append($"sc description \"{WindowsServiceHost.ServiceName}\" \"{WindowsServiceHost.ServiceDescription}\"")
            .Append(" & ")
            .Append($"sc failure \"{WindowsServiceHost.ServiceName}\" reset= 86400 actions= restart/5000/restart/5000/restart/30000")
            .Append(" & ")
            .Append($"sc start \"{WindowsServiceHost.ServiceName}\"")
            .ToString();

        var (exitCode, _) = Run("cmd.exe", $"/c {commands}", elevated: true);
        return exitCode == 0
            ? (true, "The service has been installed and started.")
            : (false, "Installation failed or was cancelled (administrator rights are required).");
    }

    /// <summary>Stops and deletes the service. Shows a UAC prompt.</summary>
    public static (bool ok, string message) Uninstall()
    {
        var commands = $"sc stop \"{WindowsServiceHost.ServiceName}\" & " +
                       $"sc delete \"{WindowsServiceHost.ServiceName}\"";
        var (exitCode, _) = Run("cmd.exe", $"/c {commands}", elevated: true);
        return exitCode == 0
            ? (true, "The service has been removed.")
            : (false, "Removal failed or was cancelled (administrator rights are required).");
    }

    public static (bool ok, string message) Start()
    {
        var (exitCode, _) = Run("cmd.exe", $"/c sc start \"{WindowsServiceHost.ServiceName}\"", elevated: true);
        return exitCode == 0 ? (true, "The service has been started.") : (false, "Could not start the service.");
    }

    public static (bool ok, string message) Stop()
    {
        var (exitCode, _) = Run("cmd.exe", $"/c sc stop \"{WindowsServiceHost.ServiceName}\"", elevated: true);
        return exitCode == 0 ? (true, "The service has been stopped.") : (false, "Could not stop the service.");
    }

    private static (int exitCode, string output) Run(string fileName, string arguments, bool elevated)
    {
        var info = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = elevated,
            CreateNoWindow = !elevated,
            RedirectStandardOutput = !elevated,
            RedirectStandardError = !elevated,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        if (elevated) info.Verb = "runas";

        using var process = Process.Start(info);
        if (process == null) return (-1, string.Empty);

        var output = string.Empty;
        if (!elevated)
        {
            output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        }

        process.WaitForExit(60_000);
        return (process.HasExited ? process.ExitCode : -1, output);
    }
}
