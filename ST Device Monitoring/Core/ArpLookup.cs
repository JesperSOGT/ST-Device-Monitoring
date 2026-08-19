using System.Net;
using System.Runtime.InteropServices;

namespace ST_Device_Monitoring.Core;

/// <summary>
/// Reads the MAC address of a device from the Windows ARP table (iphlpapi SendARP).
/// Works for devices on the same subnet; a device behind a router has no ARP entry of its own,
/// so nothing is returned in that case.
/// </summary>
public static class ArpLookup
{
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int SendARP(int destIp, int srcIp, byte[] macAddr, ref uint physicalAddrLen);

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_IPNETROW
    {
        public int dwIndex;
        public int dwPhysAddrLen;
        public byte mac0, mac1, mac2, mac3, mac4, mac5, mac6, mac7;
        public int dwAddr;
        public int dwType;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int GetIpNetTable(IntPtr pIpNetTable, ref int pdwSize, bool bOrder);

    private const int ERROR_INSUFFICIENT_BUFFER = 122;

    /// <summary>
    /// The whole Windows ARP table as (ip, mac) pairs. Handy during discovery: Windows sometimes
    /// picks up devices from other subnets, which is exactly what cannot be pinged.
    /// </summary>
    public static List<(string ip, string mac, int interfaceIndex)> GetArpTable()
    {
        var result = new List<(string, string, int)>();
        var buffer = IntPtr.Zero;

        try
        {
            var size = 0;
            if (GetIpNetTable(IntPtr.Zero, ref size, false) != ERROR_INSUFFICIENT_BUFFER || size <= 0)
                return result;

            buffer = Marshal.AllocCoTaskMem(size);
            if (GetIpNetTable(buffer, ref size, false) != 0) return result;

            var entries = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<MIB_IPNETROW>();

            for (int i = 0; i < entries; i++)
            {
                var ptr = IntPtr.Add(buffer, 4 + i * rowSize);
                var row = Marshal.PtrToStructure<MIB_IPNETROW>(ptr);

                if (row.dwPhysAddrLen != 6) continue;
                if (row.dwType == 2) continue;      // invalid entry

                var ip = new IPAddress(BitConverter.GetBytes(row.dwAddr)).ToString();
                var mac = string.Join("-", new[] { row.mac0, row.mac1, row.mac2, row.mac3, row.mac4, row.mac5 }
                    .Select(b => b.ToString("X2")));

                if (mac == "00-00-00-00-00-00" || mac == "FF-FF-FF-FF-FF-FF") continue;
                if (ip.StartsWith("224.") || ip.StartsWith("239.") || ip == "255.255.255.255") continue;

                result.Add((ip, mac, row.dwIndex));
            }
        }
        catch
        {
            // an unreadable ARP table simply gives no extra hits
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeCoTaskMem(buffer);
        }

        return result;
    }

    /// <summary>Returns the MAC as "AA-BB-CC-DD-EE-FF", or null when it cannot be determined.</summary>
    public static string? TryGetMacAddress(string host)
    {
        try
        {
            if (!IPAddress.TryParse(host, out var address))
            {
                var resolved = Dns.GetHostAddresses(host)
                    .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                if (resolved == null) return null;
                address = resolved;
            }

            if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return null;

            var destIp = BitConverter.ToInt32(address.GetAddressBytes(), 0);
            var mac = new byte[6];
            var length = (uint)mac.Length;

            if (SendARP(destIp, 0, mac, ref length) != 0 || length < 6) return null;
            if (mac.All(b => b == 0)) return null;

            return string.Join("-", mac.Take(6).Select(b => b.ToString("X2")));
        }
        catch
        {
            return null;
        }
    }
}
