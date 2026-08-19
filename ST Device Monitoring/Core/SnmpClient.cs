using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ST_Device_Monitoring.Core;

/// <summary>Result of an SNMP GET.</summary>
public sealed class SnmpResult
{
    public bool Success { get; init; }
    public long ElapsedMs { get; init; }
    public string? Error { get; init; }
    public Dictionary<string, string> Values { get; init; } = new();

    public string? Get(string oid) => Values.TryGetValue(oid, out var value) ? value : null;
}

/// <summary>
/// Minimal SNMP v1/v2c GET client over UDP (BER encoded by hand - no external package).
/// Enough to check that a device answers and to read the standard system information,
/// which is used as the device description.
/// </summary>
public static class SnmpClient
{
    public const string OidSysDescr = "1.3.6.1.2.1.1.1.0";
    public const string OidSysObjectId = "1.3.6.1.2.1.1.2.0";
    public const string OidSysUpTime = "1.3.6.1.2.1.1.3.0";
    public const string OidSysContact = "1.3.6.1.2.1.1.4.0";
    public const string OidSysName = "1.3.6.1.2.1.1.5.0";
    public const string OidSysLocation = "1.3.6.1.2.1.1.6.0";

    public const int DefaultPort = 161;
    public const string DefaultCommunity = "public";

    private static int _requestId = Environment.TickCount & 0x7FFFFFF;

    /// <summary>
    /// Sends one GET request. <paramref name="version"/> 1 = v2c (default), 0 = v1.
    /// Never throws - failures are reported in <see cref="SnmpResult.Error"/>.
    /// </summary>
    public static async Task<SnmpResult> GetAsync(string host, int port, string community,
        IReadOnlyList<string> oids, int timeoutMs, CancellationToken ct = default, int version = 1)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            IPAddress address;
            if (!IPAddress.TryParse(host, out address!))
            {
                var resolved = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
                address = resolved.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                          ?? resolved.FirstOrDefault()
                          ?? throw new SocketException((int)SocketError.HostNotFound);
            }

            var requestId = Interlocked.Increment(ref _requestId) & 0x7FFFFFF;
            var request = BuildGetRequest(version, community, requestId, oids);

            using var udp = new UdpClient(address.AddressFamily);
            udp.Client.ReceiveTimeout = timeoutMs;
            var endpoint = new IPEndPoint(address, port <= 0 ? DefaultPort : port);

            await udp.SendAsync(request, endpoint, ct).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(timeoutMs);

            var response = await udp.ReceiveAsync(timeout.Token).ConfigureAwait(false);
            sw.Stop();

            var values = ParseResponse(response.Buffer, out var error);
            if (error != null)
                return new SnmpResult { Success = false, Error = error, ElapsedMs = sw.ElapsedMilliseconds };

            return new SnmpResult { Success = true, Values = values, ElapsedMs = sw.ElapsedMilliseconds };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new SnmpResult { Success = false, Error = "Timeout - no SNMP reply", ElapsedMs = sw.ElapsedMilliseconds };
        }
        catch (SocketException ex)
        {
            return new SnmpResult { Success = false, Error = "SNMP: " + ex.Message, ElapsedMs = sw.ElapsedMilliseconds };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new SnmpResult { Success = false, Error = "SNMP: " + ex.Message, ElapsedMs = sw.ElapsedMilliseconds };
        }
    }

    /// <summary>Reads sysDescr/sysName/sysLocation/sysContact and builds a one-line description.</summary>
    public static async Task<(bool ok, string description, string sysName, string? error)> ReadSystemInfoAsync(
        string host, int port, string community, int timeoutMs, CancellationToken ct = default)
    {
        var result = await GetAsync(host, port, community,
            new[] { OidSysDescr, OidSysName, OidSysLocation, OidSysContact }, timeoutMs, ct)
            .ConfigureAwait(false);

        if (!result.Success)
            return (false, string.Empty, string.Empty, result.Error);

        var descr = Clean(result.Get(OidSysDescr));
        var name = Clean(result.Get(OidSysName));
        var location = Clean(result.Get(OidSysLocation));

        var parts = new List<string>();
        if (descr.Length > 0) parts.Add(descr);
        if (location.Length > 0) parts.Add("Location: " + location);

        return (true, string.Join(" | ", parts), name, null);
    }

    private static string Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var text = value.Replace("\r", " ").Replace("\n", " ").Trim();
        while (text.Contains("  ", StringComparison.Ordinal))
            text = text.Replace("  ", " ", StringComparison.Ordinal);
        return text.Length > 400 ? text[..400] : text;
    }

    // ---------- BER encoding ----------

    private static byte[] BuildGetRequest(int version, string community, int requestId, IReadOnlyList<string> oids)
    {
        var varbinds = new List<byte>();
        foreach (var oid in oids)
        {
            var entry = new List<byte>();
            entry.AddRange(EncodeOid(oid));
            entry.AddRange(new byte[] { 0x05, 0x00 }); // NULL
            varbinds.AddRange(Wrap(0x30, entry));
        }

        var pdu = new List<byte>();
        pdu.AddRange(EncodeInteger(requestId));
        pdu.AddRange(EncodeInteger(0)); // error-status
        pdu.AddRange(EncodeInteger(0)); // error-index
        pdu.AddRange(Wrap(0x30, varbinds));

        var message = new List<byte>();
        message.AddRange(EncodeInteger(version));       // 0 = v1, 1 = v2c
        message.AddRange(EncodeOctetString(Encoding.ASCII.GetBytes(community)));
        message.AddRange(Wrap(0xA0, pdu));             // GetRequest PDU

        return Wrap(0x30, message).ToArray();
    }

    private static List<byte> Wrap(byte tag, List<byte> content)
    {
        var result = new List<byte> { tag };
        result.AddRange(EncodeLength(content.Count));
        result.AddRange(content);
        return result;
    }

    private static List<byte> EncodeLength(int length)
    {
        if (length < 0x80) return new List<byte> { (byte)length };

        var bytes = new List<byte>();
        var value = length;
        while (value > 0)
        {
            bytes.Insert(0, (byte)(value & 0xFF));
            value >>= 8;
        }
        bytes.Insert(0, (byte)(0x80 | bytes.Count));
        return bytes;
    }

    private static List<byte> EncodeInteger(int value)
    {
        var bytes = new List<byte>();
        var v = value;
        if (v == 0) bytes.Add(0);
        while (v != 0 && v != -1)
        {
            bytes.Insert(0, (byte)(v & 0xFF));
            v >>= 8;
        }
        if (bytes.Count == 0) bytes.Add(0);
        if ((bytes[0] & 0x80) != 0 && value > 0) bytes.Insert(0, 0);

        var result = new List<byte> { 0x02 };
        result.AddRange(EncodeLength(bytes.Count));
        result.AddRange(bytes);
        return result;
    }

    private static List<byte> EncodeOctetString(byte[] value)
    {
        var result = new List<byte> { 0x04 };
        result.AddRange(EncodeLength(value.Length));
        result.AddRange(value);
        return result;
    }

    private static List<byte> EncodeOid(string oid)
    {
        var parts = oid.Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => uint.Parse(p))
            .ToArray();
        if (parts.Length < 2) throw new FormatException($"Invalid OID: {oid}");

        var content = new List<byte> { (byte)(parts[0] * 40 + parts[1]) };
        for (int i = 2; i < parts.Length; i++)
            content.AddRange(EncodeSubId(parts[i]));

        var result = new List<byte> { 0x06 };
        result.AddRange(EncodeLength(content.Count));
        result.AddRange(content);
        return result;
    }

    private static IEnumerable<byte> EncodeSubId(uint value)
    {
        var stack = new Stack<byte>();
        stack.Push((byte)(value & 0x7F));
        value >>= 7;
        while (value > 0)
        {
            stack.Push((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }
        return stack;
    }

    // ---------- BER decoding ----------

    private static Dictionary<string, string> ParseResponse(byte[] buffer, out string? error)
    {
        var values = new Dictionary<string, string>();
        error = null;
        var index = 0;

        try
        {
            if (ReadTag(buffer, ref index) != 0x30) { error = "SNMP: malformed reply"; return values; }
            ReadLength(buffer, ref index);

            SkipField(buffer, ref index);   // version
            SkipField(buffer, ref index);   // community

            var pduTag = ReadTag(buffer, ref index);
            if (pduTag != 0xA2 && pduTag != 0xA8)   // GetResponse / Report
            {
                error = $"SNMP: unexpected reply type 0x{pduTag:X2}";
                return values;
            }
            ReadLength(buffer, ref index);

            SkipField(buffer, ref index);                            // request-id
            var errorStatus = ReadIntegerField(buffer, ref index);   // error-status
            SkipField(buffer, ref index);                            // error-index

            if (errorStatus != 0)
            {
                error = "SNMP error status " + errorStatus + DescribeError(errorStatus);
                return values;
            }

            if (ReadTag(buffer, ref index) != 0x30) { error = "SNMP: malformed varbind list"; return values; }
            var listLength = ReadLength(buffer, ref index);
            var listEnd = index + listLength;

            while (index < listEnd && index < buffer.Length)
            {
                if (ReadTag(buffer, ref index) != 0x30) break;
                var entryLength = ReadLength(buffer, ref index);
                var entryEnd = index + entryLength;

                if (ReadTag(buffer, ref index) != 0x06) break;
                var oidLength = ReadLength(buffer, ref index);
                var oid = DecodeOid(buffer, index, oidLength);
                index += oidLength;

                var valueTag = ReadTag(buffer, ref index);
                var valueLength = ReadLength(buffer, ref index);
                values[oid] = DecodeValue(valueTag, buffer, index, valueLength);
                index = entryEnd;
            }
        }
        catch (Exception ex)
        {
            error = "SNMP: could not read the reply (" + ex.Message + ")";
        }

        return values;
    }

    private static string DescribeError(int status) => status switch
    {
        1 => " (reply too big)",
        2 => " (no such name - OID not supported)",
        3 => " (bad value)",
        4 => " (read only)",
        _ => string.Empty
    };

    private static byte ReadTag(byte[] buffer, ref int index) => buffer[index++];

    private static int ReadLength(byte[] buffer, ref int index)
    {
        int first = buffer[index++];
        if ((first & 0x80) == 0) return first;

        var count = first & 0x7F;
        var length = 0;
        for (int i = 0; i < count; i++) length = (length << 8) | buffer[index++];
        return length;
    }

    private static void SkipField(byte[] buffer, ref int index)
    {
        ReadTag(buffer, ref index);
        var length = ReadLength(buffer, ref index);
        index += length;
    }

    private static int ReadIntegerField(byte[] buffer, ref int index)
    {
        ReadTag(buffer, ref index);
        var length = ReadLength(buffer, ref index);
        var value = 0;
        for (int i = 0; i < length; i++) value = (value << 8) | buffer[index++];
        return value;
    }

    private static string DecodeOid(byte[] buffer, int start, int length)
    {
        if (length == 0) return string.Empty;
        var sb = new StringBuilder();
        var first = buffer[start];
        sb.Append(first / 40).Append('.').Append(first % 40);

        uint value = 0;
        for (int i = start + 1; i < start + length; i++)
        {
            value = (value << 7) | (uint)(buffer[i] & 0x7F);
            if ((buffer[i] & 0x80) == 0)
            {
                sb.Append('.').Append(value);
                value = 0;
            }
        }
        return sb.ToString();
    }

    private static string DecodeValue(byte tag, byte[] buffer, int start, int length)
    {
        switch (tag)
        {
            case 0x04: // OCTET STRING
                return Encoding.UTF8.GetString(buffer, start, length).Trim('\0');
            case 0x02: // INTEGER
            case 0x41: // Counter32
            case 0x42: // Gauge32
            case 0x43: // TimeTicks
            {
                long value = 0;
                for (int i = 0; i < length; i++) value = (value << 8) | buffer[start + i];
                return tag == 0x43
                    ? TimeSpan.FromMilliseconds(value * 10).ToString(@"d\.hh\:mm\:ss")
                    : value.ToString();
            }
            case 0x06: // OID
                return DecodeOid(buffer, start, length);
            case 0x40: // IpAddress
                return length == 4
                    ? $"{buffer[start]}.{buffer[start + 1]}.{buffer[start + 2]}.{buffer[start + 3]}"
                    : string.Empty;
            case 0x05: // NULL
                return string.Empty;
            case 0x80: // noSuchObject
            case 0x81: // noSuchInstance
            case 0x82: // endOfMibView
                return string.Empty;
            default:
                return Convert.ToHexString(buffer, start, length);
        }
    }
}
