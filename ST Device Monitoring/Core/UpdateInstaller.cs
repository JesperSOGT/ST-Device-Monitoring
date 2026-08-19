using System.Diagnostics;
using System.IO;
using System.Text;

namespace ST_Device_Monitoring.Core;

/// <summary>What the update script will have to do on this machine.</summary>
public sealed class UpdatePlan
{
    /// <summary>The exe that will be replaced.</summary>
    public string TargetPath { get; init; } = string.Empty;

    /// <summary>Where the replaced exe is kept, so a bad version can be rolled back by hand.</summary>
    public string BackupPath { get; init; } = string.Empty;

    /// <summary>True when the Windows service is installed and has to be stopped and started again.</summary>
    public bool ServiceInstalled { get; init; }

    /// <summary>True when the service was running and should be started again afterwards.</summary>
    public bool ServiceRunning { get; init; }

    /// <summary>True when the program folder cannot be written to without administrator rights.</summary>
    public bool FolderNeedsAdmin { get; init; }

    /// <summary>True when Windows will show a UAC prompt for the update.</summary>
    public bool NeedsElevation => ServiceInstalled || FolderNeedsAdmin;

    /// <summary>Plain sentence describing what will happen, shown before the user confirms.</summary>
    public string Description
    {
        get
        {
            var text = new StringBuilder();
            text.Append("The program will close, the exe will be replaced and the program will start again.");
            if (ServiceInstalled)
                text.Append(ServiceRunning
                    ? " The Windows service is running and will be stopped and started again."
                    : " The Windows service is installed and will be left stopped.");
            if (NeedsElevation)
                text.Append(" Windows will ask for administrator rights.");
            text.Append(" The version being replaced is kept next to the program as \"")
                .Append(Path.GetFileName(BackupPath)).Append("\".");
            return text.ToString();
        }
    }
}

/// <summary>
/// Swaps the running exe for a newly downloaded one.
///
/// A running program holds its own exe locked, so it cannot replace itself. Instead a small batch
/// file is written to the temporary folder and started; it keeps trying to copy the new file over
/// the old one until the program has closed, then starts it again and deletes itself. Nothing is
/// touched until the download has been verified, and the replaced version is kept as a backup.
/// </summary>
public static class UpdateInstaller
{
    /// <summary>Works out what has to happen on this machine before anything is changed.</summary>
    public static UpdatePlan CreatePlan()
    {
        var target = WindowsServiceInstaller.ExecutablePath;
        var folder = Path.GetDirectoryName(target) ?? AppContext.BaseDirectory;
        var state = WindowsServiceInstaller.Query();

        return new UpdatePlan
        {
            TargetPath = target,
            BackupPath = target + ".previous",
            ServiceInstalled = state is ServiceState.Running or ServiceState.Stopped or ServiceState.Other,
            ServiceRunning = state == ServiceState.Running,
            FolderNeedsAdmin = !CanWrite(folder)
        };
    }

    /// <summary>
    /// Starts the update script. When it returns true the caller must shut the application down
    /// straight away - the script is waiting for the exe to be released.
    /// </summary>
    public static (bool started, string? error) Launch(UpdatePlan plan, string downloadedFile, string versionText)
    {
        try
        {
            if (!File.Exists(downloadedFile))
                return (false, "The downloaded file is gone. Try again.");

            var script = WriteScript(plan, downloadedFile, versionText);

            var info = new ProcessStartInfo("cmd.exe", $"/c \"\"{script}\"\"")
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(script) ?? Path.GetTempPath(),
                WindowStyle = ProcessWindowStyle.Normal
            };
            if (plan.NeedsElevation) info.Verb = "runas";

            var process = Process.Start(info);
            return process == null
                ? (false, "The update could not be started.")
                : (true, null);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return (false, "The update was cancelled at the administrator prompt. Nothing was changed.");
        }
        catch (Exception ex)
        {
            return (false, "The update could not be started: " + ex.GetBaseException().Message);
        }
    }

    private static string WriteScript(UpdatePlan plan, string downloadedFile, string versionText)
    {
        var folder = Path.Combine(Path.GetTempPath(), "ST Device Monitoring", "update");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "apply-update.cmd");

        // No byte order mark - cmd.exe would try to run it as a command.
        File.WriteAllText(path, BuildScript(plan, downloadedFile, versionText), new UTF8Encoding(false));
        return path;
    }

    /// <summary>
    /// The batch file that does the swap. Separated from writing it to disk so it can be read and
    /// checked without starting anything.
    /// </summary>
    public static string BuildScript(UpdatePlan plan, string downloadedFile, string versionText)
    {
        var service = WindowsServiceHost.ServiceName;
        var stopService = plan.ServiceInstalled
            ? $"echo Stopping the Windows service...\r\nsc stop \"{service}\" >nul 2>&1\r\n" +
              "timeout /t 3 /nobreak >nul\r\n"
            : string.Empty;
        var startService = plan.ServiceRunning
            ? $"echo Starting the Windows service...\r\nsc start \"{service}\" >nul 2>&1\r\n"
            : string.Empty;

        var script = new StringBuilder()
            .Append("@echo off\r\n")
            .Append("chcp 65001 >nul\r\n")
            .Append("title ST Device Monitoring - update\r\n")
            .Append("echo.\r\n")
            .Append($"echo Updating ST Device Monitoring to {versionText}\r\n")
            .Append("echo.\r\n")
            .Append($"set \"TARGET={plan.TargetPath}\"\r\n")
            .Append($"set \"NEWFILE={downloadedFile}\"\r\n")
            .Append($"set \"BACKUP={plan.BackupPath}\"\r\n")
            .Append("\r\n")
            .Append("rem Keep the version that is being replaced, so it can be put back by hand.\r\n")
            .Append("copy /y \"%TARGET%\" \"%BACKUP%\" >nul 2>&1\r\n")
            .Append("\r\n")
            .Append(stopService)
            .Append("echo Waiting for ST Device Monitoring to close...\r\n")
            .Append("set /a TRIES=0\r\n")
            .Append(":wait\r\n")
            .Append("copy /y \"%NEWFILE%\" \"%TARGET%\" >nul 2>&1\r\n")
            .Append("if not errorlevel 1 goto replaced\r\n")
            .Append("set /a TRIES+=1\r\n")
            .Append("if %TRIES% GEQ 90 goto failed\r\n")
            .Append("timeout /t 1 /nobreak >nul\r\n")
            .Append("goto wait\r\n")
            .Append("\r\n")
            .Append(":replaced\r\n")
            .Append("echo The program file has been replaced.\r\n")
            .Append(startService)
            .Append("echo Starting ST Device Monitoring...\r\n")
            .Append("start \"\" \"%TARGET%\"\r\n")
            .Append("del \"%NEWFILE%\" >nul 2>&1\r\n")
            .Append("(goto) 2>nul & del \"%~f0\"\r\n")
            .Append("exit /b 0\r\n")
            .Append("\r\n")
            .Append(":failed\r\n")
            .Append("echo.\r\n")
            .Append("echo The program file could not be replaced:\r\n")
            .Append("echo    %TARGET%\r\n")
            .Append("echo.\r\n")
            .Append("echo The new version is still on disk here:\r\n")
            .Append("echo    %NEWFILE%\r\n")
            .Append("echo.\r\n")
            .Append("echo Close ST Device Monitoring completely and copy that file over the old one.\r\n")
            .Append("echo.\r\n")
            .Append("pause\r\n")
            .Append("exit /b 1\r\n")
            .ToString();

        return script;
    }

    private static bool CanWrite(string folder)
    {
        try
        {
            var probe = Path.Combine(folder, $".update-probe-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "probe");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
