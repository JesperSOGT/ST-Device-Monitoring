using System.Diagnostics;
using System.Net;

namespace ST_Device_Monitoring.Core;

/// <summary>
/// Changes the IP settings of a network adapter with netsh. Every change needs administrator
/// rights, so the command is run elevated and Windows shows a UAC prompt.
///
/// Used for the common case where a device sits on a completely different subnet (or fell back
/// to 169.254.x because no DHCP answered): give this machine an address in the device's subnet,
/// open the device's web page and set the address you want on the device itself.
/// </summary>
public static class NetworkAdapterConfig
{
    /// <summary>Suggests a free-looking address in the same subnet as <paramref name="deviceIp"/>.</summary>
    public static (IPAddress address, IPAddress mask) SuggestAddressFor(IPAddress deviceIp)
    {
        var bytes = deviceIp.GetAddressBytes();

        // 169.254.x.x is link-local and always /16.
        if (bytes[0] == 169 && bytes[1] == 254)
        {
            var last = bytes[3] == 200 ? 201 : 200;
            return (new IPAddress(new[] { bytes[0], bytes[1], bytes[2], (byte)last }),
                    IPAddress.Parse("255.255.0.0"));
        }

        var host = bytes[3] == 250 ? 251 : 250;
        return (new IPAddress(new[] { bytes[0], bytes[1], bytes[2], (byte)host }),
                IPAddress.Parse("255.255.255.0"));
    }

    public static string BuildStaticCommand(string adapter, IPAddress address, IPAddress mask)
        => $"netsh interface ipv4 set address name=\"{adapter}\" static {address} {mask}";

    public static string BuildDhcpCommand(string adapter)
        => $"netsh interface ipv4 set address name=\"{adapter}\" source=dhcp";

    /// <summary>Gives the adapter a fixed address. Shows a UAC prompt.</summary>
    public static (bool ok, string message) SetStaticAddress(string adapter, IPAddress address, IPAddress mask)
    {
        var exit = RunElevated(BuildStaticCommand(adapter, address, mask));
        return exit == 0
            ? (true, $"\"{adapter}\" now has {address} / {mask}.")
            : (false, "The change failed or was cancelled (administrator rights are required).");
    }

    /// <summary>Puts the adapter back on automatic (DHCP). Shows a UAC prompt.</summary>
    public static (bool ok, string message) SetDhcp(string adapter)
    {
        var exit = RunElevated(BuildDhcpCommand(adapter));
        return exit == 0
            ? (true, $"\"{adapter}\" is back on automatic (DHCP).")
            : (false, "The change failed or was cancelled (administrator rights are required).");
    }

    /// <summary>Opens the device's web interface in the default browser.</summary>
    public static void OpenWebInterface(string host, int port = 80)
    {
        var url = port == 443 ? $"https://{host}" : port == 80 ? $"http://{host}" : $"http://{host}:{port}";
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // a machine without a default browser is not worth an exception
        }
    }

    private static int RunElevated(string command)
    {
        try
        {
            var info = new ProcessStartInfo("cmd.exe", "/c " + command)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var process = Process.Start(info);
            if (process == null) return -1;
            process.WaitForExit(60_000);
            return process.HasExited ? process.ExitCode : -1;
        }
        catch
        {
            return -1;   // cancelled UAC prompt
        }
    }
}
