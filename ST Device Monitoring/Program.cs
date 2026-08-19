using ST_Device_Monitoring.Core;

namespace ST_Device_Monitoring;

/// <summary>
/// Entry point. The same executable can run in two ways:
///   ST Device Monitoring.exe             -> normal application with a window
///   ST Device Monitoring.exe --service   -> headless Windows service (started by Windows)
/// The service is installed/removed from Settings -> Run mode.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Any(a => a.Equals("--service", StringComparison.OrdinalIgnoreCase)))
        {
            WindowsServiceHost.Run();
            return 0;
        }

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
