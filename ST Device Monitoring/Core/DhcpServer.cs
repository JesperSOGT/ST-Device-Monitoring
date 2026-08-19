using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ST_Device_Monitoring.Core;

/// <summary>Settings for the small built-in DHCP server.</summary>
public sealed class DhcpServerSettings
{
    /// <summary>The address of the network adapter the server listens on (also the server id).</summary>
    public IPAddress ServerAddress { get; set; } = IPAddress.None;

    /// <summary>The address handed out to the device.</summary>
    public IPAddress OfferedAddress { get; set; } = IPAddress.None;

    public IPAddress SubnetMask { get; set; } = IPAddress.Parse("255.255.255.0");

    /// <summary>Optional router/gateway handed out. None = not sent.</summary>
    public IPAddress Gateway { get; set; } = IPAddress.None;

    /// <summary>Optional DNS server handed out. None = not sent.</summary>
    public IPAddress Dns { get; set; } = IPAddress.None;

    public int LeaseMinutes { get; set; } = 60;

    /// <summary>
    /// When set, only this MAC ("AA-BB-CC-DD-EE-FF") gets an answer. Strongly recommended:
    /// it makes the server harmless if the cable ends up in the office network.
    /// </summary>
    public string? AllowedMac { get; set; }
}

/// <summary>Reported when a device has accepted the address.</summary>
public sealed class DhcpLease
{
    public string Mac { get; init; } = string.Empty;
    public string Ip { get; init; } = string.Empty;
    public string HostName { get; init; } = string.Empty;
    public DateTime Time { get; init; } = DateTime.Now;
}

/// <summary>
/// A minimal DHCP server for one directly connected device - the case where a PLC or a panel
/// only asks for an address by DHCP and there is no DHCP server on the cable.
///
/// It binds to ONE adapter address and (by default) answers ONE MAC address, so it cannot
/// hand out addresses on a company network by accident. It implements just what is needed:
/// DISCOVER -> OFFER and REQUEST -> ACK, plus logging of DECLINE/RELEASE/INFORM.
/// </summary>
public sealed class DhcpServer : IDisposable
{
    private const int ServerPort = 67;
    private const int ClientPort = 68;

    private static readonly byte[] MagicCookie = { 0x63, 0x82, 0x53, 0x63 };

    private Socket? _socket;
    private CancellationTokenSource? _cts;

    public DhcpServerSettings Settings { get; }

    /// <summary>Every packet in and out, ready for the log window.</summary>
    public event Action<string>? Log;

    /// <summary>Raised when a device has been given the address (ACK sent).</summary>
    public event Action<DhcpLease>? LeaseGranted;

    public bool IsRunning => _socket != null;

    public DhcpServer(DhcpServerSettings settings) => Settings = settings;

    public void Start()
    {
        if (IsRunning) return;

        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.ExclusiveAddressUse = false;
        socket.EnableBroadcast = true;

        // Bound to the chosen adapter only - not to 0.0.0.0.
        socket.Bind(new IPEndPoint(Settings.ServerAddress, ServerPort));

        _socket = socket;
        _cts = new CancellationTokenSource();

        Write($"DHCP server started on {Settings.ServerAddress} (offering {Settings.OfferedAddress}" +
              (string.IsNullOrWhiteSpace(Settings.AllowedMac) ? ", any MAC" : $" to {Settings.AllowedMac}") + ")");

        _ = Task.Run(() => ReceiveLoopAsync(socket, _cts.Token));
    }

    public void Stop()
    {
        if (!IsRunning) return;

        _cts?.Cancel();
        try { _socket?.Close(); } catch { /* ignored */ }
        try { _socket?.Dispose(); } catch { /* ignored */ }
        _socket = null;
        _cts?.Dispose();
        _cts = null;

        Write("DHCP server stopped.");
    }

    private async Task ReceiveLoopAsync(Socket socket, CancellationToken ct)
    {
        var buffer = new byte[2048];
        var any = new IPEndPoint(IPAddress.Any, 0);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, ct).ConfigureAwait(false);
                Handle(socket, buffer.AsSpan(0, result.ReceivedBytes).ToArray());
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) return;
                Write("Error: " + ex.Message);
                return;
            }
        }
    }

    private void Handle(Socket socket, byte[] packet)
    {
        if (packet.Length < 240) return;
        if (packet[0] != 1) return;                       // not a request
        if (!packet.AsSpan(236, 4).SequenceEqual(MagicCookie)) return;

        var mac = FormatMac(packet, 28, packet[2]);
        var options = ParseOptions(packet);

        var messageType = options.TryGetValue(53, out var typeBytes) && typeBytes.Length > 0 ? typeBytes[0] : (byte)0;
        var hostName = options.TryGetValue(12, out var nameBytes) ? Encoding.ASCII.GetString(nameBytes).Trim() : string.Empty;

        if (!string.IsNullOrWhiteSpace(Settings.AllowedMac) &&
            !string.Equals(mac, Settings.AllowedMac, StringComparison.OrdinalIgnoreCase))
        {
            Write($"Ignored {Describe(messageType)} from {mac} (not the allowed MAC)");
            return;
        }

        switch (messageType)
        {
            case 1:   // DISCOVER
                Write($"DISCOVER from {mac}" + (hostName.Length > 0 ? $" ({hostName})" : ""));
                Send(socket, BuildReply(packet, 2), "OFFER", mac);
                break;

            case 3:   // REQUEST
                Write($"REQUEST from {mac}" + (hostName.Length > 0 ? $" ({hostName})" : ""));
                Send(socket, BuildReply(packet, 5), "ACK", mac);
                LeaseGranted?.Invoke(new DhcpLease
                {
                    Mac = mac,
                    Ip = Settings.OfferedAddress.ToString(),
                    HostName = hostName
                });
                break;

            case 4:   // DECLINE
                Write($"DECLINE from {mac} - the device says the address is already in use");
                break;

            case 7:   // RELEASE
                Write($"RELEASE from {mac}");
                break;

            case 8:   // INFORM
                Write($"INFORM from {mac} - the device already has an address");
                break;

            default:
                Write($"{Describe(messageType)} from {mac}");
                break;
        }
    }

    private void Send(Socket socket, byte[] reply, string label, string mac)
    {
        try
        {
            // The device has no address yet, so the answer goes out as a broadcast on this adapter.
            socket.SendTo(reply, new IPEndPoint(IPAddress.Broadcast, ClientPort));
            Write($"{label} {Settings.OfferedAddress} -> {mac}");
        }
        catch (Exception ex)
        {
            Write($"Could not send {label}: {ex.Message}");
        }
    }

    /// <summary>Builds an OFFER (type 2) or ACK (type 5) for the request.</summary>
    private byte[] BuildReply(byte[] request, byte messageType)
    {
        var reply = new byte[300];

        reply[0] = 2;                        // BOOTREPLY
        reply[1] = request[1];               // htype
        reply[2] = request[2];               // hlen
        reply[3] = 0;                        // hops
        Array.Copy(request, 4, reply, 4, 4); // xid
        Array.Copy(request, 10, reply, 10, 2); // flags

        Settings.OfferedAddress.GetAddressBytes().CopyTo(reply, 16);   // yiaddr
        Settings.ServerAddress.GetAddressBytes().CopyTo(reply, 20);    // siaddr
        Array.Copy(request, 24, reply, 24, 4);                          // giaddr
        Array.Copy(request, 28, reply, 28, 16);                         // chaddr
        MagicCookie.CopyTo(reply, 236);

        var i = 240;
        reply[i++] = 53; reply[i++] = 1; reply[i++] = messageType;                 // message type
        reply[i++] = 54; reply[i++] = 4;                                           // server identifier
        foreach (var b in Settings.ServerAddress.GetAddressBytes()) reply[i++] = b;
        reply[i++] = 51; reply[i++] = 4;                                           // lease time
        foreach (var b in BitConverter.GetBytes(
                     IPAddress.HostToNetworkOrder(Math.Max(60, Settings.LeaseMinutes * 60))))
            reply[i++] = b;
        reply[i++] = 1; reply[i++] = 4;                                            // subnet mask
        foreach (var b in Settings.SubnetMask.GetAddressBytes()) reply[i++] = b;

        if (!Equals(Settings.Gateway, IPAddress.None) && !Equals(Settings.Gateway, IPAddress.Any))
        {
            reply[i++] = 3; reply[i++] = 4;                                        // router
            foreach (var b in Settings.Gateway.GetAddressBytes()) reply[i++] = b;
        }

        if (!Equals(Settings.Dns, IPAddress.None) && !Equals(Settings.Dns, IPAddress.Any))
        {
            reply[i++] = 6; reply[i++] = 4;                                        // DNS
            foreach (var b in Settings.Dns.GetAddressBytes()) reply[i++] = b;
        }

        reply[i++] = 255;                                                          // end

        return reply.AsSpan(0, Math.Max(300, i)).ToArray();
    }

    private static Dictionary<byte, byte[]> ParseOptions(byte[] packet)
    {
        var options = new Dictionary<byte, byte[]>();
        var i = 240;

        while (i + 1 < packet.Length)
        {
            var code = packet[i];
            if (code == 255) break;
            if (code == 0) { i++; continue; }

            var length = packet[i + 1];
            if (i + 2 + length > packet.Length) break;

            options[code] = packet.AsSpan(i + 2, length).ToArray();
            i += 2 + length;
        }

        return options;
    }

    private static string FormatMac(byte[] packet, int offset, int length)
    {
        if (length is < 1 or > 16) length = 6;
        return string.Join("-", packet.Skip(offset).Take(length).Select(b => b.ToString("X2")));
    }

    private static string Describe(byte messageType) => messageType switch
    {
        1 => "DISCOVER",
        2 => "OFFER",
        3 => "REQUEST",
        4 => "DECLINE",
        5 => "ACK",
        6 => "NAK",
        7 => "RELEASE",
        8 => "INFORM",
        _ => "DHCP message " + messageType
    };

    private void Write(string message)
    {
        try { Log?.Invoke($"{DateTime.Now:HH:mm:ss}  {message}"); } catch { /* ignored */ }
    }

    public void Dispose() => Stop();
}
