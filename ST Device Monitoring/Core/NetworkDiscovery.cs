using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace ST_Device_Monitoring.Core;

/// <summary>One address on one of the machine's own network adapters.</summary>
public sealed class LocalSubnet
{
    public string AdapterName { get; init; } = string.Empty;
    public IPAddress Address { get; init; } = IPAddress.None;
    public IPAddress Mask { get; init; } = IPAddress.None;

    public uint Network => HostScanner.ToUInt(Address) & HostScanner.ToUInt(Mask);

    public bool Contains(IPAddress other)
        => other.AddressFamily == AddressFamily.InterNetwork &&
           (HostScanner.ToUInt(other) & HostScanner.ToUInt(Mask)) == Network;

    public override string ToString() => $"{AdapterName}: {Address} / {Mask}";
}

/// <summary>A device seen on the network during discovery.</summary>
public sealed class DiscoveredHost
{
    public string Ip { get; init; } = string.Empty;
    public string Mac { get; set; } = string.Empty;
    public string Info { get; set; } = string.Empty;
    public bool InLocalSubnet { get; set; }

    /// <summary>Which network adapter on this machine the device was seen on.</summary>
    public string Adapter { get; set; } = string.Empty;
    public DateTime FirstSeen { get; init; } = DateTime.Now;
    public DateTime LastSeen { get; set; } = DateTime.Now;
    public HashSet<string> Protocols { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string ProtocolText => string.Join(", ", Protocols.OrderBy(p => p));
}

/// <summary>
/// Finds devices on the wire without knowing their IP address - including devices that sit on a
/// completely different subnet than this machine (e.g. the PC is 192.168.1.10/24 and the device
/// is 192.168.44.1). Broadcast and multicast traffic reaches the network card regardless of
/// subnet, so the device can be spotted even though it cannot be pinged.
///
/// Three sources are used, all with plain .NET sockets - no packet driver is needed:
///   1. Passive listening on the ports devices chatter on by themselves (DHCP, NetBIOS, mDNS,
///      LLMNR, SSDP, WS-Discovery, BACnet, EtherNet/IP).
///   2. Active broadcast/multicast queries that provoke an answer from those same protocols.
///   3. The Windows ARP table, which sometimes holds devices from foreign subnets.
///
/// Note: Profinet DCP and LLDP/CDP run directly on Ethernet (no IP) and cannot be reached from
/// .NET sockets - those need a packet capture driver such as Npcap.
/// </summary>
public sealed class NetworkDiscovery : IDisposable
{
    private static readonly int[] ListenPorts = { 68, 67, 137, 138, 5353, 5355, 1900, 3702, 47808, 44818 };

    private static readonly IPAddress MdnsGroup = IPAddress.Parse("224.0.0.251");
    private static readonly IPAddress LlmnrGroup = IPAddress.Parse("224.0.0.252");
    private static readonly IPAddress SsdpGroup = IPAddress.Parse("239.255.255.250");

    private readonly List<Socket> _sockets = new();
    private readonly Dictionary<string, DiscoveredHost> _hosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    /// <summary>Raised when a host is seen for the first time or updated.</summary>
    public event Action<DiscoveredHost>? HostUpdated;

    public IReadOnlyList<LocalSubnet> LocalSubnets { get; }

    private readonly Dictionary<int, string> _adapterNames = new();

    public NetworkDiscovery()
    {
        LocalSubnets = GetLocalSubnets();
        _adapterNames = GetAdapterIndexMap();
    }

    /// <summary>Maps the Windows interface index to the adapter name, so a received packet can be
    /// tied to the network card it arrived on - also when the sender is on a foreign subnet.</summary>
    public static Dictionary<int, string> GetAdapterIndexMap()
    {
        var map = new Dictionary<int, string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    var properties = nic.GetIPProperties().GetIPv4Properties();
                    if (properties != null) map[properties.Index] = nic.Name;
                }
                catch { /* adapter without IPv4 */ }
            }
        }
        catch { /* ignored */ }
        return map;
    }

    private string AdapterName(int interfaceIndex)
        => _adapterNames.TryGetValue(interfaceIndex, out var name) ? name : string.Empty;

    public static List<LocalSubnet> GetLocalSubnets()
    {
        var result = new List<LocalSubnet>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                foreach (var address in nic.GetIPProperties().UnicastAddresses)
                {
                    if (address.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (address.IPv4Mask == null || Equals(address.IPv4Mask, IPAddress.Any)) continue;

                    result.Add(new LocalSubnet
                    {
                        AdapterName = nic.Name,
                        Address = address.Address,
                        Mask = address.IPv4Mask
                    });
                }
            }
        }
        catch { /* an adapter that disappears must not stop discovery */ }
        return result;
    }

    /// <summary>
    /// Listens (and probes) for the given time. Results arrive through <see cref="HostUpdated"/>
    /// while it runs; the full list is returned at the end.
    /// </summary>
    public async Task<List<DiscoveredHost>> RunAsync(TimeSpan duration, IProgress<int>? secondsLeft,
        CancellationToken ct)
    {
        AddArpTable();
        StartListeners();

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stop.CancelAfter(duration);

        var probe = Task.Run(() => ProbeLoopAsync(stop.Token), CancellationToken.None);
        var arpWatch = Task.Run(() => ArpWatchLoopAsync(stop.Token), CancellationToken.None);

        try
        {
            var end = DateTime.UtcNow + duration;
            while (!stop.IsCancellationRequested)
            {
                secondsLeft?.Report(Math.Max(0, (int)(end - DateTime.UtcNow).TotalSeconds));
                await Task.Delay(500, stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }

        try { await probe.ConfigureAwait(false); } catch { /* ignored */ }
        try { await arpWatch.ConfigureAwait(false); } catch { /* ignored */ }

        AddArpTable();
        Stop();

        lock (_lock)
            return _hosts.Values.OrderBy(h => HostScanner.ToUInt(IPAddress.Parse(h.Ip))).ToList();
    }

    // ---------- passive listening ----------

    private void StartListeners()
    {
        foreach (var port in ListenPorts)
        {
            try
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                socket.ExclusiveAddressUse = false;
                socket.EnableBroadcast = true;
                socket.Bind(new IPEndPoint(IPAddress.Any, port));

                JoinGroups(socket, port);

                _sockets.Add(socket);
                _ = Task.Run(() => ReceiveLoopAsync(socket, port));
            }
            catch
            {
                // The port may be taken by a Windows service that does not allow sharing -
                // discovery simply continues without that one.
            }
        }
    }

    private void JoinGroups(Socket socket, int port)
    {
        var groups = port switch
        {
            5353 => new[] { MdnsGroup },
            5355 => new[] { LlmnrGroup },
            1900 => new[] { SsdpGroup },
            3702 => new[] { SsdpGroup },
            _ => Array.Empty<IPAddress>()
        };

        foreach (var group in groups)
        {
            foreach (var local in LocalSubnets)
            {
                try
                {
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                        new MulticastOption(group, local.Address));
                }
                catch { /* not all adapters accept every group */ }
            }
        }
    }

    private async Task ReceiveLoopAsync(Socket socket, int port)
    {
        var buffer = new byte[4096];
        var any = new IPEndPoint(IPAddress.Any, 0);

        try { socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true); }
        catch { /* then the adapter simply stays unknown */ }

        while (true)
        {
            try
            {
                var result = await socket
                    .ReceiveMessageFromAsync(buffer, SocketFlags.None, any)
                    .ConfigureAwait(false);
                if (result.RemoteEndPoint is not IPEndPoint remote) continue;

                var data = buffer.AsSpan(0, result.ReceivedBytes).ToArray();
                Handle(remote.Address, port, data, AdapterName(result.PacketInformation.Interface));
            }
            catch
            {
                return;   // socket closed
            }
        }
    }

    private void Handle(IPAddress from, int port, byte[] data, string adapter)
    {
        if (from.AddressFamily != AddressFamily.InterNetwork) return;
        if (IPAddress.IsLoopback(from) || Equals(from, IPAddress.Any)) return;
        if (LocalSubnets.Any(s => Equals(s.Address, from))) return;   // ourselves

        var protocol = port switch
        {
            67 or 68 => "DHCP",
            137 or 138 => "NetBIOS",
            5353 => "mDNS",
            5355 => "LLMNR",
            1900 => "SSDP",
            3702 => "WS-Discovery",
            47808 => "BACnet",
            44818 => "EtherNet/IP",
            _ => "UDP " + port
        };

        string? info = null;
        string? mac = null;

        if (protocol == "DHCP") (mac, info) = ParseDhcp(data);
        else if (protocol == "SSDP") info = ParseSsdpServer(data);

        Add(from.ToString(), protocol, mac, info, adapter);
    }

    /// <summary>DHCP packets carry the client's MAC address and often its hostname.</summary>
    private static (string? mac, string? info) ParseDhcp(byte[] data)
    {
        try
        {
            if (data.Length < 240) return (null, null);

            var hlen = data[2];
            string? mac = null;
            if (hlen == 6)
                mac = string.Join("-", data.Skip(28).Take(6).Select(b => b.ToString("X2")));

            // Options start at 240 (after the magic cookie); option 12 = host name.
            string? host = null;
            var i = 240;
            while (i + 1 < data.Length)
            {
                var code = data[i];
                if (code == 255) break;
                if (code == 0) { i++; continue; }
                var length = data[i + 1];
                if (i + 2 + length > data.Length) break;

                if (code == 12) host = Encoding.ASCII.GetString(data, i + 2, length).Trim();
                i += 2 + length;
            }

            return (mac, host == null ? null : "Hostname: " + host);
        }
        catch
        {
            return (null, null);
        }
    }

    private static string? ParseSsdpServer(byte[] data)
    {
        try
        {
            var text = Encoding.ASCII.GetString(data);
            foreach (var line in text.Split('\n'))
            {
                if (line.StartsWith("SERVER:", StringComparison.OrdinalIgnoreCase))
                    return line[7..].Trim();
            }
        }
        catch { /* not text */ }
        return null;
    }

    private void Add(string ip, string protocol, string? mac = null, string? info = null, string? adapter = null)
    {
        DiscoveredHost host;
        lock (_lock)
        {
            if (!_hosts.TryGetValue(ip, out host!))
            {
                host = new DiscoveredHost { Ip = ip };
                if (IPAddress.TryParse(ip, out var parsed))
                    host.InLocalSubnet = LocalSubnets.Any(s => s.Contains(parsed));
                _hosts[ip] = host;
            }

            host.LastSeen = DateTime.Now;
            if (!string.IsNullOrEmpty(protocol)) host.Protocols.Add(protocol);
            if (!string.IsNullOrWhiteSpace(mac) && string.IsNullOrWhiteSpace(host.Mac)) host.Mac = mac;
            if (!string.IsNullOrWhiteSpace(info) && string.IsNullOrWhiteSpace(host.Info)) host.Info = info!;
            if (!string.IsNullOrWhiteSpace(adapter) && string.IsNullOrWhiteSpace(host.Adapter)) host.Adapter = adapter!;

            if (string.IsNullOrWhiteSpace(host.Adapter) && IPAddress.TryParse(ip, out var forSubnet))
            {
                var match = LocalSubnets.FirstOrDefault(s => s.Contains(forSubnet));
                if (match != null) host.Adapter = match.AdapterName;
            }

            if (string.IsNullOrWhiteSpace(host.Mac) && host.InLocalSubnet)
            {
                var arp = ArpLookup.TryGetMacAddress(ip);
                if (arp != null) host.Mac = arp;
            }
        }

        HostUpdated?.Invoke(host);
    }

    /// <summary>
    /// Re-reads the ARP table every two seconds while listening. A device that only speaks ARP -
    /// a controller looking for its gateway, for instance - never sends anything this program can
    /// receive on a socket, but Windows adds it to the ARP table as soon as the request is aimed
    /// at an address this machine holds. Polling therefore catches it as it appears.
    /// </summary>
    private async Task ArpWatchLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            AddArpTable();
        }
    }

    /// <summary>Everything Windows already knows - sometimes including foreign subnets.</summary>
    private void AddArpTable()
    {
        foreach (var (ip, mac, interfaceIndex) in ArpLookup.GetArpTable())
            Add(ip, "ARP table", mac, null, AdapterName(interfaceIndex));
    }

    // ---------- active probes ----------

    private async Task ProbeLoopAsync(CancellationToken ct)
    {
        using var sender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        sender.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        sender.EnableBroadcast = true;
        sender.Bind(new IPEndPoint(IPAddress.Any, 0));

        _ = Task.Run(() => ReceiveLoopAsync(sender, 0), CancellationToken.None);

        while (!ct.IsCancellationRequested)
        {
            SendProbes(sender);
            try { await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void SendProbes(Socket sender)
    {
        var targets = new List<IPAddress> { IPAddress.Broadcast };
        foreach (var subnet in LocalSubnets)
        {
            // directed broadcast for each local subnet
            var broadcast = HostScanner.FromUInt(subnet.Network | ~HostScanner.ToUInt(subnet.Mask));
            targets.Add(broadcast);
        }

        Send(sender, BuildNetBiosNodeStatus(), targets, 137);
        Send(sender, BuildBacnetWhoIs(), targets, 47808);
        Send(sender, BuildEtherNetIpListIdentity(), targets, 44818);
        Send(sender, BuildMdnsQuery(), new[] { MdnsGroup }, 5353);
        Send(sender, BuildSsdpSearch(), new[] { SsdpGroup }, 1900);
    }

    private static void Send(Socket sender, byte[] payload, IEnumerable<IPAddress> targets, int port)
    {
        foreach (var target in targets)
        {
            try { sender.SendTo(payload, new IPEndPoint(target, port)); }
            catch { /* an unreachable adapter must not stop the rest */ }
        }
    }

    /// <summary>NetBIOS node status request for "*" - answers with the device's name table.</summary>
    private static byte[] BuildNetBiosNodeStatus()
    {
        var packet = new byte[50];
        packet[0] = 0x12; packet[1] = 0x34;      // transaction id
        packet[2] = 0x00; packet[3] = 0x00;      // flags: query
        packet[4] = 0x00; packet[5] = 0x01;      // one question
        packet[12] = 0x20;                        // name length
        packet[13] = (byte)'C'; packet[14] = (byte)'K';   // encoded "*"
        for (int i = 15; i < 45; i++) packet[i] = (byte)'A';
        packet[45] = 0x00;
        packet[46] = 0x00; packet[47] = 0x21;    // type NBSTAT
        packet[48] = 0x00; packet[49] = 0x01;    // class IN
        return packet;
    }

    /// <summary>BACnet/IP Who-Is (unconfirmed request), asks every BACnet device to identify itself.</summary>
    private static byte[] BuildBacnetWhoIs()
        => new byte[] { 0x81, 0x0B, 0x00, 0x08, 0x01, 0x20, 0xFF, 0xFF };

    /// <summary>EtherNet/IP List Identity - Rockwell, Schneider, Omron and many drives answer.</summary>
    private static byte[] BuildEtherNetIpListIdentity()
    {
        var packet = new byte[24];
        packet[0] = 0x63; packet[1] = 0x00;      // command: ListIdentity
        return packet;
    }

    /// <summary>mDNS query for _services._dns-sd._udp.local (PTR).</summary>
    private static byte[] BuildMdnsQuery()
    {
        var name = new[] { "_services", "_dns-sd", "_udp", "local" };
        var body = new List<byte> { 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
        foreach (var label in name)
        {
            body.Add((byte)label.Length);
            body.AddRange(Encoding.ASCII.GetBytes(label));
        }
        body.Add(0x00);
        body.AddRange(new byte[] { 0x00, 0x0C, 0x00, 0x01 });   // PTR, IN
        return body.ToArray();
    }

    private static byte[] BuildSsdpSearch() => Encoding.ASCII.GetBytes(
        "M-SEARCH * HTTP/1.1\r\n" +
        "HOST: 239.255.255.250:1900\r\n" +
        "MAN: \"ssdp:discover\"\r\n" +
        "MX: 2\r\n" +
        "ST: ssdp:all\r\n\r\n");

    // ---------- shutdown ----------

    private void Stop()
    {
        foreach (var socket in _sockets)
        {
            try { socket.Close(); } catch { /* ignored */ }
            try { socket.Dispose(); } catch { /* ignored */ }
        }
        _sockets.Clear();
    }

    public void Dispose() => Stop();
}
