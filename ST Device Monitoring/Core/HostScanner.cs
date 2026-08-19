using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using ST_Device_Monitoring.Models;

namespace ST_Device_Monitoring.Core;

/// <summary>Result for one address in a range scan.</summary>
public sealed class ScanResult
{
    public IPAddress Address { get; init; } = IPAddress.None;
    public bool Responded { get; init; }
    public long RoundtripMs { get; init; }
    public string HostName { get; set; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    /// <summary>SNMP sysDescr, when the scan was run in SNMP mode.</summary>
    public string Description { get; set; } = string.Empty;
}

/// <summary>
/// Scans an IP range in parallel (bounded concurrency) so a whole subnet can be added at once.
/// </summary>
public static class HostScanner
{
    public static async Task<List<ScanResult>> ScanAsync(
        IPAddress from, IPAddress to, CheckMode mode, int port, int timeoutMs,
        bool resolveNames, IProgress<int>? progress, CancellationToken ct, int maxParallel = 64,
        string community = SnmpClient.DefaultCommunity)
    {
        var addresses = Expand(from, to);
        var results = new List<ScanResult>(addresses.Count);
        var done = 0;

        using var gate = new SemaphoreSlim(maxParallel);
        var tasks = addresses.Select(async address =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var result = mode switch
                {
                    CheckMode.TcpPort => await ProbeTcpAsync(address, port, timeoutMs, ct).ConfigureAwait(false),
                    CheckMode.Snmp => await ProbeSnmpAsync(address, port, community, timeoutMs, ct).ConfigureAwait(false),
                    _ => await ProbeIcmpAsync(address, timeoutMs).ConfigureAwait(false)
                };

                if (result.Responded && resolveNames && string.IsNullOrEmpty(result.HostName))
                {
                    try
                    {
                        var entry = await Dns.GetHostEntryAsync(address).ConfigureAwait(false);
                        result.HostName = entry.HostName;
                    }
                    catch { /* no reverse DNS - not an error */ }
                }

                progress?.Report(Interlocked.Increment(ref done));
                return result;
            }
            finally
            {
                gate.Release();
            }
        });

        results.AddRange(await Task.WhenAll(tasks).ConfigureAwait(false));
        return results.OrderBy(r => ToUInt(r.Address)).ToList();
    }

    private static async Task<ScanResult> ProbeIcmpAsync(IPAddress address, int timeoutMs)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(address, timeoutMs).ConfigureAwait(false);
            return new ScanResult
            {
                Address = address,
                Responded = reply.Status == IPStatus.Success,
                RoundtripMs = reply.RoundtripTime,
                Status = reply.Status.ToString()
            };
        }
        catch (Exception ex)
        {
            return new ScanResult { Address = address, Responded = false, Status = ex.GetType().Name };
        }
    }

    /// <summary>SNMP GET of sysDescr/sysName - answers and describes the device in one go.</summary>
    private static async Task<ScanResult> ProbeSnmpAsync(IPAddress address, int port, string community,
        int timeoutMs, CancellationToken ct)
    {
        var result = await SnmpClient.GetAsync(address.ToString(), port <= 0 ? SnmpClient.DefaultPort : port,
            community, new[] { SnmpClient.OidSysDescr, SnmpClient.OidSysName }, timeoutMs, ct)
            .ConfigureAwait(false);

        if (!result.Success)
            return new ScanResult { Address = address, Responded = false, Status = result.Error ?? "No SNMP reply" };

        var description = (result.Get(SnmpClient.OidSysDescr) ?? string.Empty)
            .Replace("\r", " ").Replace("\n", " ").Trim();

        return new ScanResult
        {
            Address = address,
            Responded = true,
            RoundtripMs = result.ElapsedMs,
            Status = "SnmpOk",
            HostName = (result.Get(SnmpClient.OidSysName) ?? string.Empty).Trim(),
            Description = description.Length > 400 ? description[..400] : description
        };
    }

    private static async Task<ScanResult> ProbeTcpAsync(IPAddress address, int port, int timeoutMs, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(timeoutMs);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(address, port, timeout.Token).ConfigureAwait(false);
            return new ScanResult
            {
                Address = address,
                Responded = true,
                RoundtripMs = sw.ElapsedMilliseconds,
                Status = "TcpConnected"
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ScanResult { Address = address, Responded = false, Status = "TimedOut" };
        }
        catch (SocketException ex)
        {
            return new ScanResult { Address = address, Responded = false, Status = ex.SocketErrorCode.ToString() };
        }
        catch (Exception ex)
        {
            return new ScanResult { Address = address, Responded = false, Status = ex.GetType().Name };
        }
    }

    /// <summary>All IPv4 addresses from..to inclusive (maximum 4096).</summary>
    public static List<IPAddress> Expand(IPAddress from, IPAddress to)
    {
        var start = ToUInt(from);
        var end = ToUInt(to);
        if (end < start) (start, end) = (end, start);

        var count = Math.Min(end - start + 1, 4096);
        var list = new List<IPAddress>((int)count);
        for (uint i = 0; i < count; i++)
            list.Add(FromUInt(start + i));
        return list;
    }

    public static uint ToUInt(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (bytes.Length != 4) return 0;
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    public static IPAddress FromUInt(uint value) => new(new[]
    {
        (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value
    });
}
