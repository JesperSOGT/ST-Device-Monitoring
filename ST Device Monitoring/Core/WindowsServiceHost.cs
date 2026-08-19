using System.IO;
using System.Runtime.InteropServices;

namespace ST_Device_Monitoring.Core;

/// <summary>
/// Runs the monitoring headless as a Windows service (started with the --service argument).
/// Implemented directly against the Windows service API, so the application needs no NuGet
/// packages. There is no user interface in service mode - only the CSV logs, the daily summary
/// and the e-mail/webhook notifications.
/// </summary>
public static class WindowsServiceHost
{
    public const string ServiceName = "STDeviceMonitoring";
    public const string ServiceDisplayName = "ST Device Monitoring";
    public const string ServiceDescription =
        "Monitors devices with ICMP/TCP checks and writes CSV logs. Part of ST Device Monitoring.";

    private const int SERVICE_WIN32_OWN_PROCESS = 0x10;
    private const int SERVICE_START_PENDING = 2;
    private const int SERVICE_RUNNING = 4;
    private const int SERVICE_STOP_PENDING = 3;
    private const int SERVICE_STOPPED = 1;
    private const int SERVICE_ACCEPT_STOP = 1;
    private const int SERVICE_ACCEPT_SHUTDOWN = 4;
    private const int SERVICE_CONTROL_STOP = 1;
    private const int SERVICE_CONTROL_SHUTDOWN = 5;

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS
    {
        public int dwServiceType;
        public int dwCurrentState;
        public int dwControlsAccepted;
        public int dwWin32ExitCode;
        public int dwServiceSpecificExitCode;
        public int dwCheckPoint;
        public int dwWaitHint;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_TABLE_ENTRY
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string lpServiceName;
        public IntPtr lpServiceProc;
    }

    private delegate void ServiceMainDelegate(int argc, IntPtr argv);
    private delegate int HandlerExDelegate(int control, int eventType, IntPtr eventData, IntPtr context);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool StartServiceCtrlDispatcherW(SERVICE_TABLE_ENTRY[] lpServiceStartTable);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr RegisterServiceCtrlHandlerExW(string lpServiceName,
        HandlerExDelegate lpHandlerProc, IntPtr lpContext);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetServiceStatus(IntPtr hServiceStatus, ref SERVICE_STATUS lpServiceStatus);

    private static IntPtr _statusHandle;
    private static SERVICE_STATUS _status;
    private static readonly ManualResetEventSlim StopEvent = new(false);
    private static MonitorService? _service;

    // The delegates must be kept alive for as long as the service runs.
    private static ServiceMainDelegate? _serviceMain;
    private static HandlerExDelegate? _handler;

    /// <summary>Entry point when the process is started by the service control manager.</summary>
    public static void Run()
    {
        _serviceMain = ServiceMain;
        var table = new[]
        {
            new SERVICE_TABLE_ENTRY
            {
                lpServiceName = ServiceName,
                lpServiceProc = Marshal.GetFunctionPointerForDelegate(_serviceMain)
            },
            new SERVICE_TABLE_ENTRY { lpServiceName = string.Empty, lpServiceProc = IntPtr.Zero }
        };

        if (!StartServiceCtrlDispatcherW(table))
        {
            // Not started by the SCM (e.g. run by hand) - just monitor until the process is killed.
            RunMonitoringLoop();
        }
    }

    private static void ServiceMain(int argc, IntPtr argv)
    {
        _handler = HandleControl;
        _statusHandle = RegisterServiceCtrlHandlerExW(ServiceName, _handler, IntPtr.Zero);
        if (_statusHandle == IntPtr.Zero) return;

        _status.dwServiceType = SERVICE_WIN32_OWN_PROCESS;
        _status.dwControlsAccepted = SERVICE_ACCEPT_STOP | SERVICE_ACCEPT_SHUTDOWN;
        SetState(SERVICE_START_PENDING, 5000);

        try
        {
            StartMonitoring();
            SetState(SERVICE_RUNNING);
        }
        catch (Exception ex)
        {
            WriteServiceLog("Startup failed: " + ex);
            _status.dwWin32ExitCode = 1;
            SetState(SERVICE_STOPPED);
            return;
        }

        StopEvent.Wait();

        SetState(SERVICE_STOP_PENDING, 10000);
        StopMonitoring();
        SetState(SERVICE_STOPPED);
    }

    private static int HandleControl(int control, int eventType, IntPtr eventData, IntPtr context)
    {
        if (control is SERVICE_CONTROL_STOP or SERVICE_CONTROL_SHUTDOWN)
        {
            SetState(SERVICE_STOP_PENDING, 10000);
            StopEvent.Set();
        }
        return 0;
    }

    private static void SetState(int state, int waitHint = 0)
    {
        _status.dwCurrentState = state;
        _status.dwWaitHint = waitHint;
        _status.dwCheckPoint = state is SERVICE_START_PENDING or SERVICE_STOP_PENDING
            ? _status.dwCheckPoint + 1
            : 0;
        if (_statusHandle != IntPtr.Zero) SetServiceStatus(_statusHandle, ref _status);
    }

    private static void RunMonitoringLoop()
    {
        StartMonitoring();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; StopEvent.Set(); };
        StopEvent.Wait();
        StopMonitoring();
    }

    private static void StartMonitoring()
    {
        var config = ConfigStore.Load();
        _service = new MonitorService(config);
        _service.StartAll();
        WriteServiceLog($"Service started - monitoring {config.Devices.Count(d => d.Enabled)} device(s).");
    }

    private static void StopMonitoring()
    {
        try
        {
            if (_service != null)
            {
                _service.DisposeAsync().AsTask().GetAwaiter().GetResult();
                _service = null;
            }
            WriteServiceLog("Service stopped.");
        }
        catch (Exception ex)
        {
            WriteServiceLog("Shutdown error: " + ex.Message);
        }
    }

    /// <summary>Small text log next to the executable - the service has no window to write to.</summary>
    public static void WriteServiceLog(string message)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "service.log");
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
        }
        catch { /* logging must never throw */ }
    }
}
