using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using ST_Device_Monitoring.Models;

namespace ST_Device_Monitoring.Core;

/// <summary>Answer from the gate that decides whether a device may be checked right now.</summary>
public readonly struct GateResult
{
    /// <summary>True when the device may be checked.</summary>
    public bool Open { get; init; }

    /// <summary>Why checking is paused. Only set when <see cref="Open"/> is false.</summary>
    public string? Reason { get; init; }

    /// <summary>True when the pause comes from the group schedule rather than the group master.</summary>
    public bool FromSchedule { get; init; }

    public static GateResult Allow => new() { Open = true };

    public static GateResult Block(string reason, bool fromSchedule)
        => new() { Open = false, Reason = reason, FromSchedule = fromSchedule };
}

/// <summary>Raised when a device goes down or comes back up.</summary>
public readonly struct DeviceAlert
{
    public DeviceConfig Device { get; init; }
    public bool IsDown { get; init; }
    public DateTime Timestamp { get; init; }
    public string Message { get; init; }
    public TimeSpan Downtime { get; init; }
}

/// <summary>
/// Monitors a single device. Runs its own asynchronous check loop on the thread pool, so all
/// devices are checked in parallel and independently of each other.
/// Counters are updated lock-free with Interlocked; the UI reads a snapshot via <see cref="GetStats"/>.
///
/// Check mode is either ICMP echo or a TCP connect to a port (for devices that block ICMP).
///
/// Log suppression: when the device keeps failing, only the first
/// <see cref="DeviceConfig.MaxLoggedFailures"/> consecutive failures are written to the log.
/// After that the device is still checked, but the repeated failures are not logged. When it
/// answers again, a RECOVERED entry is written including the downtime and how many failures
/// were not logged.
///
/// Adaptive interval: while the device is down, <see cref="DeviceConfig.DownIntervalMs"/> is used
/// instead of the normal interval, so a 100 ms device does not flood the network while offline.
/// </summary>
public sealed class DeviceMonitor : IDisposable
{
    public const int HistorySize = 120;

    private static readonly byte[] Payload = new byte[32];

    private readonly CsvLogger _logger;
    private readonly object _historyLock = new();
    private readonly PingSample[] _history = new PingSample[HistorySize];
    private readonly RollingWindow _rolling = new(60);
    private readonly DailyAccumulator _daily = new();
    private int _historyIndex;

    private CancellationTokenSource? _cts;
    private Task? _loop;

    private long _sent, _success, _failed, _consecutiveFails, _maxConsecutiveFails, _outages;
    private long _lastRtt, _minRtt = long.MaxValue, _maxRtt, _rttSum, _rttCount;
    private long _lastSuccessTicks, _lastFailTicks, _outageStartTicks;
    private long _suppressedEntries;
    private volatile string? _lastError;
    private volatile bool _running;
    private volatile bool _inOutage;
    private volatile bool _loggingSuppressed;
    private volatile bool _descriptionChecked;
    private volatile bool _blocked;
    private volatile bool _blockedBySchedule;
    private volatile string? _blockReason;
    private volatile bool _firstSuccessLogged;

    public DeviceConfig Config { get; private set; }

    /// <summary>
    /// Optional gate deciding whether the device may be checked right now. Two things use it: the
    /// group master (the devices behind a dead uplink are paused instead of producing a storm of
    /// failures and alarms) and the group schedule (the group is only checked in short runs).
    /// </summary>
    public Func<GateResult>? CheckGate { get; set; }

    /// <summary>Name of the master that controls this device (only used for logging/UI).</summary>
    public string? GateSourceName { get; set; }

    /// <summary>The schedule that governs this device's group, if any. Used by the UI only.</summary>
    public GroupSchedule? Schedule { get; set; }

    /// <summary>True when the device is currently counted as down (past its fail threshold).</summary>
    public bool IsDown => _running && _inOutage;

    /// <summary>True when checking is paused - by the group master or by the group schedule.</summary>
    public bool IsBlocked => _blocked;

    /// <summary>Why checking is paused, or null.</summary>
    public string? BlockReason => _blockReason;

    /// <summary>Raised from the check thread when the device goes down or recovers.</summary>
    public event Action<DeviceAlert>? Alert;

    /// <summary>Raised when an SNMP description has been read and stored on the device.</summary>
    public event Action<DeviceMonitor>? DescriptionDiscovered;

    public DeviceMonitor(DeviceConfig config, CsvLogger logger)
    {
        Config = config;
        _logger = logger;
    }

    public bool IsRunning => _running;
    public DailyAccumulator Daily => _daily;

    /// <summary>
    /// Applies edited settings. Only THIS device's check loop is stopped and restarted -
    /// all other devices keep running untouched.
    /// </summary>
    public async Task UpdateConfigAsync(DeviceConfig config)
    {
        var wasRunning = _running;
        if (wasRunning) await StopAsync().ConfigureAwait(false);
        Config = config;
        _descriptionChecked = false;
        if (wasRunning && config.Enabled) Start();
    }

    public void Start()
    {
        if (_running || !Config.Enabled) return;
        _blocked = false;
        _blockedBySchedule = false;
        _blockReason = null;
        _firstSuccessLogged = false;
        _cts = new CancellationTokenSource();
        _running = true;
        var token = _cts.Token;
        _loop = Task.Run(() => RunAsync(token), token);
    }

    public async Task StopAsync()
    {
        var cts = _cts;
        var loop = _loop;
        if (cts == null) { _running = false; return; }

        cts.Cancel();
        if (loop != null)
        {
            try { await loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch { /* ignored during shutdown */ }
        }
        cts.Dispose();
        _cts = null;
        _loop = null;
        _running = false;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var ping = new Ping();
        IPAddress? parsed = IPAddress.TryParse(Config.Host, out var ip) ? ip : null;
        var options = new PingOptions { DontFragment = true, Ttl = 128 };
        var sw = new Stopwatch();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                sw.Restart();

                // The group master and the group schedule decide whether this device is checked
                // at all right now.
                var gate = CheckGate;
                if (gate != null)
                {
                    var decision = gate();
                    if (!decision.Open)
                    {
                        EnterBlocked(decision.Reason, decision.FromSchedule);
                        await Task.Delay(Math.Clamp(Config.IntervalMs, 250, 1000), ct).ConfigureAwait(false);
                        continue;
                    }
                }
                if (_blocked) LeaveBlocked();

                CheckResult result = Config.Mode switch
                {
                    CheckMode.TcpPort => await CheckTcpAsync(parsed, ct).ConfigureAwait(false),
                    CheckMode.Snmp => await CheckSnmpAsync(ct).ConfigureAwait(false),
                    _ => await CheckIcmpAsync(ping, parsed, options).ConfigureAwait(false)
                };

                Record(result.Success, result.RoundtripMs, result.Status, result.Error);

                var interval = EffectiveInterval();
                var remaining = interval - (int)sw.ElapsedMilliseconds;
                if (remaining > 0)
                    await Task.Delay(remaining, ct).ConfigureAwait(false);
                else
                    await Task.Yield();
            }
        }
        catch (OperationCanceledException)
        {
            // normal stop
        }
        finally
        {
            _running = false;
        }
    }

    /// <summary>Normal interval while up, the (usually slower) down interval while down.</summary>
    private int EffectiveInterval()
    {
        var down = Interlocked.Read(ref _consecutiveFails) >= Config.FailThreshold;
        if (down && Config.DownIntervalMs > 0)
            return Math.Max(Config.DownIntervalMs, 20);
        return Math.Max(Config.IntervalMs, 20);
    }

    private readonly struct CheckResult
    {
        public bool Success { get; init; }
        public long RoundtripMs { get; init; }
        public string Status { get; init; }
        public string? Error { get; init; }
    }

    private async Task<CheckResult> CheckIcmpAsync(Ping ping, IPAddress? parsed, PingOptions options)
    {
        try
        {
            PingReply reply = parsed != null
                ? await ping.SendPingAsync(parsed, Config.TimeoutMs, Payload, options).ConfigureAwait(false)
                : await ping.SendPingAsync(Config.Host, Config.TimeoutMs, Payload, options).ConfigureAwait(false);

            var ok = reply.Status == IPStatus.Success;
            return new CheckResult
            {
                Success = ok,
                RoundtripMs = reply.RoundtripTime,
                Status = reply.Status.ToString(),
                Error = ok ? null : DescribeStatus(reply.Status)
            };
        }
        catch (PingException ex)
        {
            return new CheckResult { Status = "PingException", Error = ex.GetBaseException().Message };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new CheckResult { Status = ex.GetType().Name, Error = ex.Message };
        }
    }

    private async Task<CheckResult> CheckTcpAsync(IPAddress? parsed, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Config.TimeoutMs);

        try
        {
            using var client = new TcpClient();
            client.NoDelay = true;

            if (parsed != null)
                await client.ConnectAsync(parsed, Config.Port, timeout.Token).ConfigureAwait(false);
            else
                await client.ConnectAsync(Config.Host, Config.Port, timeout.Token).ConfigureAwait(false);

            sw.Stop();
            return new CheckResult
            {
                Success = true,
                RoundtripMs = sw.ElapsedMilliseconds,
                Status = "TcpConnected"
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new CheckResult
            {
                Status = "TimedOut",
                Error = $"Timeout - no TCP connection to port {Config.Port}"
            };
        }
        catch (SocketException ex)
        {
            return new CheckResult
            {
                Status = ex.SocketErrorCode.ToString(),
                Error = $"TCP {Config.Port}: {ex.Message}"
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new CheckResult { Status = ex.GetType().Name, Error = ex.Message };
        }
    }

    /// <summary>
    /// Pauses checking: the counters are frozen, any ongoing outage is cleared (so no alarm is
    /// raised for something that is only unreachable because the uplink is down) and one PAUSED
    /// line is written to the log.
    /// </summary>
    private void EnterBlocked(string? reason, bool fromSchedule)
    {
        // The reason is refreshed on every pass so the countdown to the next run stays current
        // in the list, but only the first pass writes to the log.
        var alreadyBlocked = _blocked;
        _blockReason = reason;
        _blockedBySchedule = fromSchedule;
        if (alreadyBlocked) return;

        _blocked = true;

        Interlocked.Exchange(ref _consecutiveFails, 0);
        Interlocked.Exchange(ref _suppressedEntries, 0);
        Interlocked.Exchange(ref _outageStartTicks, 0);
        _inOutage = false;
        _loggingSuppressed = false;

        _logger.Log(new LogRecord
        {
            Timestamp = DateTime.Now,
            DeviceName = Config.Name,
            Host = Config.Host,
            Event = PingEvent.Paused,
            Status = fromSchedule ? "Scheduled pause" : "Paused",
            Info = reason ?? "Checking paused",
            ConsecutiveFails = 0
        });
    }

    private void LeaveBlocked()
    {
        if (!_blocked) return;
        var wasSchedule = _blockedBySchedule;
        _blocked = false;
        _blockedBySchedule = false;
        _blockReason = null;

        _logger.Log(new LogRecord
        {
            Timestamp = DateTime.Now,
            DeviceName = Config.Name,
            Host = Config.Host,
            Event = PingEvent.Resumed,
            Status = wasSchedule ? "Scheduled run" : "Resumed",
            Info = wasSchedule
                ? $"Scheduled run started for group \"{Config.Group}\" - checking resumed"
                : GateSourceName is { Length: > 0 } master
                    ? $"Group master \"{master}\" answers again - checking resumed"
                    : "Checking resumed",
            ConsecutiveFails = 0
        });
    }

    /// <summary>
    /// SNMP GET of sysUpTime. The first time it succeeds and the device has no description yet,
    /// sysDescr/sysName are read and stored on the device.
    /// </summary>
    private async Task<CheckResult> CheckSnmpAsync(CancellationToken ct)
    {
        var community = string.IsNullOrWhiteSpace(Config.Community)
            ? SnmpClient.DefaultCommunity
            : Config.Community;
        var port = Config.Port <= 0 ? SnmpClient.DefaultPort : Config.Port;

        var result = await SnmpClient.GetAsync(Config.Host, port, community,
            new[] { SnmpClient.OidSysUpTime }, Config.TimeoutMs, ct).ConfigureAwait(false);

        if (!result.Success)
            return new CheckResult
            {
                Status = result.Error != null && result.Error.Contains("Timeout", StringComparison.OrdinalIgnoreCase)
                    ? "TimedOut"
                    : "SnmpError",
                Error = result.Error
            };

        if (!_descriptionChecked && string.IsNullOrWhiteSpace(Config.Description))
        {
            _descriptionChecked = true;
            _ = Task.Run(() => DiscoverDescriptionAsync(port, community), CancellationToken.None);
        }

        return new CheckResult
        {
            Success = true,
            RoundtripMs = result.ElapsedMs,
            Status = "SnmpOk"
        };
    }

    /// <summary>
    /// Looks the MAC address up in the ARP table and stores it on the device configuration.
    /// Only works for devices on the same subnet.
    /// </summary>
    private void DiscoverMacAddress()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(Config.MacAddress)) return;

            var mac = ArpLookup.TryGetMacAddress(Config.Host);
            if (string.IsNullOrWhiteSpace(mac)) return;

            Config.MacAddress = mac;
            DescriptionDiscovered?.Invoke(this);
        }
        catch
        {
            // looking up the MAC must never disturb the monitoring
        }
    }

    /// <summary>Reads sysDescr/sysName once and stores it on the device configuration.</summary>
    private async Task DiscoverDescriptionAsync(int port, string community)
    {
        try
        {
            var (ok, description, sysName, _) = await SnmpClient
                .ReadSystemInfoAsync(Config.Host, port, community, Math.Max(Config.TimeoutMs, 2000))
                .ConfigureAwait(false);

            if (!ok || string.IsNullOrWhiteSpace(description)) return;
            if (!string.IsNullOrWhiteSpace(Config.Description)) return;

            Config.Description = description;
            if (!string.IsNullOrWhiteSpace(sysName) && string.IsNullOrWhiteSpace(Config.Name))
                Config.Name = sysName;

            DescriptionDiscovered?.Invoke(this);
        }
        catch
        {
            // reading the description must never disturb the monitoring
        }
    }

    private void Record(bool ok, long rtt, string status, string? error)
    {
        var now = DateTime.Now;
        Interlocked.Increment(ref _sent);
        _rolling.Add(now, ok, rtt);
        _daily.AddCheck(now, ok, rtt);

        if (ok)
            RecordSuccess(now, rtt, status);
        else
            RecordFailure(now, status, error);

        AddHistory(new PingSample
        {
            Timestamp = now,
            Success = ok,
            RoundtripMs = rtt,
            Status = status,
            Error = error,
            HasValue = true
        });
    }

    private void RecordSuccess(DateTime now, long rtt, string status)
    {
        Interlocked.Increment(ref _success);
        var previousFails = Interlocked.Exchange(ref _consecutiveFails, 0);
        Interlocked.Exchange(ref _lastRtt, rtt);
        Interlocked.Add(ref _rttSum, rtt);
        Interlocked.Increment(ref _rttCount);
        Interlocked.Exchange(ref _lastSuccessTicks, now.Ticks);
        UpdateMin(rtt);
        UpdateMax(rtt);

        if (previousFails <= 0)
        {
            // The very first successful check is always logged, even when only failures are
            // being logged - so the log shows when the device was first seen online.
            var isFirst = !_firstSuccessLogged;
            if (isFirst)
            {
                _firstSuccessLogged = true;
                // The device answered, so it is in the ARP table now - read its MAC address once.
                if (string.IsNullOrWhiteSpace(Config.MacAddress))
                    _ = Task.Run(DiscoverMacAddress, CancellationToken.None);
            }

            _logger.Log(new LogRecord
            {
                Timestamp = now,
                DeviceName = Config.Name,
                Host = Config.Host,
                Event = PingEvent.Success,
                RoundtripMs = rtt,
                Status = status,
                Info = isFirst ? "First successful check" : null,
                ConsecutiveFails = 0,
                Force = isFirst
            });
            return;
        }

        // The device is back online - always log this, also when logging was suppressed.
        var suppressed = Interlocked.Exchange(ref _suppressedEntries, 0);
        var startTicks = Interlocked.Exchange(ref _outageStartTicks, 0);
        var downtime = startTicks == 0 ? TimeSpan.Zero : now - new DateTime(startTicks);
        var wasDown = _inOutage;

        _loggingSuppressed = false;
        _inOutage = false;

        if (wasDown) _daily.AddOutage(now, downtime);

        var info = $"Back online after {downtime.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s, " +
                   $"{previousFails} failed check(s)" +
                   (suppressed > 0 ? $", {suppressed} not logged" : string.Empty);

        _logger.Log(new LogRecord
        {
            Timestamp = now,
            DeviceName = Config.Name,
            Host = Config.Host,
            Event = PingEvent.Recovered,
            RoundtripMs = rtt,
            Status = status,
            Info = info,
            ConsecutiveFails = 0
        });

        if (wasDown)
            RaiseAlert(new DeviceAlert
            {
                Device = Config,
                IsDown = false,
                Timestamp = now,
                Downtime = downtime,
                Message = info
            });
    }

    private void RecordFailure(DateTime now, string status, string? error)
    {
        Interlocked.Increment(ref _failed);
        var consecutive = Interlocked.Increment(ref _consecutiveFails);
        Interlocked.Exchange(ref _lastFailTicks, now.Ticks);
        _lastError = string.IsNullOrEmpty(error) ? status : error;

        if (consecutive == 1)
            Interlocked.Exchange(ref _outageStartTicks, now.Ticks);

        var max = Interlocked.Read(ref _maxConsecutiveFails);
        while (consecutive > max)
        {
            var prev = Interlocked.CompareExchange(ref _maxConsecutiveFails, consecutive, max);
            if (prev == max) break;
            max = prev;
        }

        if (!_inOutage && consecutive >= Config.FailThreshold)
        {
            _inOutage = true;
            Interlocked.Increment(ref _outages);
            RaiseAlert(new DeviceAlert
            {
                Device = Config,
                IsDown = true,
                Timestamp = now,
                Message = $"{consecutive} failed check(s): {_lastError}"
            });
        }

        var limit = Config.MaxLoggedFailures;

        if (limit <= 0 || consecutive < limit)
        {
            _logger.Log(new LogRecord
            {
                Timestamp = now,
                DeviceName = Config.Name,
                Host = Config.Host,
                Event = PingEvent.Failure,
                Status = status,
                Info = error,
                ConsecutiveFails = consecutive
            });
            return;
        }

        if (consecutive == limit)
        {
            // Last logged failure: note that further failures are suppressed.
            _loggingSuppressed = true;
            _logger.Log(new LogRecord
            {
                Timestamp = now,
                DeviceName = Config.Name,
                Host = Config.Host,
                Event = PingEvent.SuppressionStarted,
                Status = status,
                Info = $"{error} | {limit} consecutive failures - further failures are not logged, " +
                       "checking continues until the device recovers",
                ConsecutiveFails = consecutive
            });
            return;
        }

        // Suppressed: keep checking, count it, but write nothing.
        Interlocked.Increment(ref _suppressedEntries);
    }

    private void RaiseAlert(DeviceAlert alert)
    {
        try { Alert?.Invoke(alert); } catch { /* a failing notifier must not stop monitoring */ }
    }

    private void UpdateMin(long rtt)
    {
        var current = Interlocked.Read(ref _minRtt);
        while (rtt < current)
        {
            var prev = Interlocked.CompareExchange(ref _minRtt, rtt, current);
            if (prev == current) break;
            current = prev;
        }
    }

    private void UpdateMax(long rtt)
    {
        var current = Interlocked.Read(ref _maxRtt);
        while (rtt > current)
        {
            var prev = Interlocked.CompareExchange(ref _maxRtt, rtt, current);
            if (prev == current) break;
            current = prev;
        }
    }

    private void AddHistory(in PingSample sample)
    {
        lock (_historyLock)
        {
            _history[_historyIndex] = sample;
            _historyIndex = (_historyIndex + 1) % HistorySize;
        }
    }

    /// <summary>Copy of the history in chronological order (oldest first).</summary>
    public PingSample[] GetHistory()
    {
        var result = new PingSample[HistorySize];
        lock (_historyLock)
        {
            for (int i = 0; i < HistorySize; i++)
                result[i] = _history[(_historyIndex + i) % HistorySize];
        }
        return result;
    }

    public DeviceStats GetStats()
    {
        var now = DateTime.Now;
        var sent = Interlocked.Read(ref _sent);
        var failed = Interlocked.Read(ref _failed);
        var consecutive = Interlocked.Read(ref _consecutiveFails);
        var rttCount = Interlocked.Read(ref _rttCount);
        var lastSuccess = Interlocked.Read(ref _lastSuccessTicks);
        var lastFail = Interlocked.Read(ref _lastFailTicks);
        var min = Interlocked.Read(ref _minRtt);

        return new DeviceStats
        {
            Sent = sent,
            Success = Interlocked.Read(ref _success),
            Failed = failed,
            ConsecutiveFails = consecutive,
            MaxConsecutiveFails = Interlocked.Read(ref _maxConsecutiveFails),
            OutageCount = Interlocked.Read(ref _outages),
            LastRtt = Interlocked.Read(ref _lastRtt),
            AvgRtt = rttCount == 0 ? 0 : (double)Interlocked.Read(ref _rttSum) / rttCount,
            MinRtt = min == long.MaxValue ? 0 : min,
            MaxRtt = Interlocked.Read(ref _maxRtt),
            LastSuccess = lastSuccess == 0 ? null : new DateTime(lastSuccess),
            LastFail = lastFail == 0 ? null : new DateTime(lastFail),
            LastError = _lastError,
            Running = _running,
            State = ComputeState(sent, consecutive),
            LoggingSuppressed = _loggingSuppressed,
            SuppressedEntries = Interlocked.Read(ref _suppressedEntries),
            Rolling = _rolling.GetStats(now),
            Blocked = _blocked,
            GateSource = GateSourceName,
            BlockReason = _blockReason,
            BlockedBySchedule = _blockedBySchedule
        };
    }

    /// <summary>Summary for the current day, including device identity.</summary>
    public DailySummary GetDailySummary()
    {
        var summary = _daily.GetCurrent();
        Stamp(summary);
        return summary;
    }

    /// <summary>Returns a finished day once, after midnight.</summary>
    public DailySummary? TakeCompletedDay(DateTime now)
    {
        var summary = _daily.TakeCompletedDay(now);
        if (summary != null) Stamp(summary);
        return summary;
    }

    private void Stamp(DailySummary summary)
    {
        summary.DeviceName = Config.Name;
        summary.Host = Config.Host;
        summary.Mode = Config.ModeText;
        summary.Group = Config.Group;
    }

    private DeviceState ComputeState(long sent, long consecutive)
    {
        if (!Config.Enabled) return DeviceState.Disabled;
        if (!_running) return DeviceState.Stopped;
        if (_blocked) return DeviceState.Blocked;
        if (sent == 0) return DeviceState.Unknown;
        if (consecutive >= Config.FailThreshold) return DeviceState.Error;
        if (consecutive > 0) return DeviceState.Warning;
        return DeviceState.Ok;
    }

    public void ResetCounters()
    {
        Interlocked.Exchange(ref _sent, 0);
        Interlocked.Exchange(ref _success, 0);
        Interlocked.Exchange(ref _failed, 0);
        Interlocked.Exchange(ref _consecutiveFails, 0);
        Interlocked.Exchange(ref _maxConsecutiveFails, 0);
        Interlocked.Exchange(ref _outages, 0);
        Interlocked.Exchange(ref _lastRtt, 0);
        Interlocked.Exchange(ref _minRtt, long.MaxValue);
        Interlocked.Exchange(ref _maxRtt, 0);
        Interlocked.Exchange(ref _rttSum, 0);
        Interlocked.Exchange(ref _rttCount, 0);
        Interlocked.Exchange(ref _lastSuccessTicks, 0);
        Interlocked.Exchange(ref _lastFailTicks, 0);
        Interlocked.Exchange(ref _outageStartTicks, 0);
        Interlocked.Exchange(ref _suppressedEntries, 0);
        _lastError = null;
        _inOutage = false;
        _loggingSuppressed = false;
        _blocked = false;
        _blockedBySchedule = false;
        _blockReason = null;
        _firstSuccessLogged = false;
        _rolling.Reset();
        _daily.Reset();
        lock (_historyLock)
        {
            Array.Clear(_history);
            _historyIndex = 0;
        }
    }

    private static string DescribeStatus(IPStatus status) => status switch
    {
        IPStatus.TimedOut => "Timeout - no reply",
        IPStatus.DestinationHostUnreachable => "Destination host unreachable",
        IPStatus.DestinationNetworkUnreachable => "Destination network unreachable",
        IPStatus.DestinationUnreachable => "Destination unreachable",
        IPStatus.TtlExpired => "TTL expired",
        IPStatus.BadDestination => "Bad destination",
        _ => status.ToString()
    };

    public void Dispose()
    {
        try { StopAsync().GetAwaiter().GetResult(); } catch { /* ignored */ }
    }
}
