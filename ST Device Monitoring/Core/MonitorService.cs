using System.Collections.Concurrent;
using ST_Device_Monitoring.Models;

namespace ST_Device_Monitoring.Core;

/// <summary>
/// Owns all DeviceMonitor instances, the CSV logger, the alert dispatcher and the daily summary.
/// Every device runs its own task - start and stop can be done per device or for all at once.
/// </summary>
public sealed class MonitorService : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, DeviceMonitor> _monitors = new();
    private readonly Timer _summaryTimer;

    public AppConfig Config { get; }
    public CsvLogger Logger { get; }
    public AlertDispatcher Alerts { get; }

    /// <summary>Runs the per-group schedules ("every 30 min, check for 2 min, 07:00-17:00").</summary>
    public GroupScheduler Scheduler { get; }

    /// <summary>Raised (from a check thread) when any device goes down or recovers.</summary>
    public event Action<DeviceAlert>? Alert;

    /// <summary>Raised when a device description has been read via SNMP - the UI saves the config.</summary>
    public event Action<DeviceMonitor>? DescriptionDiscovered;

    public MonitorService(AppConfig config)
    {
        Config = config;
        Logger = new CsvLogger(ConfigStore.ResolveLogDirectory(config), config.LogAllPings, config.CsvSeparator)
        {
            Rotation = config.LogRotation,
            MaxFileSizeBytes = Math.Max(0, (long)config.MaxLogFileSizeMB) * 1024 * 1024,
            RingTrimPercent = config.RingTrimPercent
        };
        Logger.CleanupOldFiles(config.LogRetentionDays);
        Alerts = new AlertDispatcher(config.Alerts);
        Scheduler = new GroupScheduler(config.Schedules);

        foreach (var device in config.Devices)
            Attach(new DeviceMonitor(device, Logger));

        RebuildGroupGates();

        // Checks once a minute whether a day has finished and needs a summary file.
        _summaryTimer = new Timer(_ => WriteCompletedDays(), null,
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public IReadOnlyCollection<DeviceMonitor> Monitors => _monitors.Values.ToList();

    /// <summary>
    /// Rebuilds the gate in front of every device's check loop. Two things can hold a device back:
    ///
    /// - the group schedule: the group is only checked in short runs ("every 30 minutes, check for
    ///   2 minutes, 07:00-17:00 on weekdays"). This applies to every device in the group, the
    ///   master included.
    /// - the group master: while the uplink is down the devices behind it are paused instead of
    ///   all failing at once.
    ///
    /// Call this whenever devices or schedules are added, removed or edited.
    /// </summary>
    public void RebuildGroupGates()
    {
        var masters = new Dictionary<string, DeviceMonitor>(StringComparer.OrdinalIgnoreCase);
        foreach (var monitor in _monitors.Values)
        {
            var group = monitor.Config.Group?.Trim() ?? string.Empty;
            if (monitor.Config.IsGroupMaster && group.Length > 0 && !masters.ContainsKey(group))
                masters[group] = monitor;
        }

        var scheduler = Scheduler;

        foreach (var monitor in _monitors.Values)
        {
            var group = monitor.Config.Group?.Trim() ?? string.Empty;

            var schedule = group.Length == 0 ? null : scheduler.Find(group);
            monitor.Schedule = schedule is { Enabled: true } ? schedule : null;

            Func<GateResult>? scheduleGate = null;
            if (monitor.Schedule != null)
            {
                var scheduledGroup = group;
                scheduleGate = () =>
                {
                    var state = scheduler.GetState(scheduledGroup);
                    return state.Open
                        ? GateResult.Allow
                        : GateResult.Block(state.Reason ?? "Outside the group schedule", true);
                };
            }

            Func<GateResult>? masterGate = null;
            if (group.Length > 0 && !monitor.Config.IsGroupMaster &&
                masters.TryGetValue(group, out var master) && !ReferenceEquals(master, monitor))
            {
                var gateMaster = master;
                monitor.GateSourceName = gateMaster.Config.Name;
                // Open gate when the master is up, unknown, stopped or disabled - only a confirmed
                // outage on the master pauses the rest of the group.
                masterGate = () => gateMaster.IsDown
                    ? GateResult.Block($"Group master \"{gateMaster.Config.Name}\" is down - checking paused", false)
                    : GateResult.Allow;
            }
            else
            {
                monitor.GateSourceName = null;
            }

            // The schedule is asked first, so a device outside its run says so instead of blaming
            // a master that is not being checked either.
            monitor.CheckGate = Combine(scheduleGate, masterGate);
        }
    }

    private static Func<GateResult>? Combine(Func<GateResult>? first, Func<GateResult>? second)
    {
        if (first == null) return second;
        if (second == null) return first;
        return () =>
        {
            var result = first();
            return result.Open ? second() : result;
        };
    }

    /// <summary>
    /// Applies edited schedules without touching any running check loop. A device that is inside
    /// a run keeps running; one that falls outside is paused within a second.
    /// </summary>
    public void ApplySchedules()
    {
        Scheduler.Reload(Config.Schedules);
        RebuildGroupGates();
    }

    /// <summary>The master of a group, if one is defined.</summary>
    public DeviceMonitor? GetGroupMaster(string group)
    {
        if (string.IsNullOrWhiteSpace(group)) return null;
        return _monitors.Values.FirstOrDefault(m =>
            m.Config.IsGroupMaster &&
            string.Equals(m.Config.Group?.Trim(), group.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Every group name in use, sorted.</summary>
    public IReadOnlyList<string> Groups => Config.Devices
        .Select(d => d.Group?.Trim() ?? string.Empty)
        .Where(g => g.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(g => g, StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    /// <summary>Number of devices in a group.</summary>
    public int CountInGroup(string group) => Config.Devices.Count(d =>
        string.Equals(d.Group?.Trim(), group?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>True between "Start" and "Stop", regardless of how many devices are enabled.</summary>
    public bool IsMonitoring { get; private set; }

    private DeviceMonitor Attach(DeviceMonitor monitor)
    {
        monitor.Alert += OnDeviceAlert;
        monitor.DescriptionDiscovered += OnDescriptionDiscovered;
        _monitors[monitor.Config.Id] = monitor;
        return monitor;
    }

    private void OnDescriptionDiscovered(DeviceMonitor monitor)
    {
        try { DescriptionDiscovered?.Invoke(monitor); } catch { /* never stop monitoring */ }
    }

    private void OnDeviceAlert(DeviceAlert alert)
    {
        Alerts.Dispatch(alert);
        try { Alert?.Invoke(alert); } catch { /* a failing subscriber must not stop monitoring */ }
    }

    /// <summary>
    /// Adds a device. Existing devices are NOT stopped or restarted - their check loops keep
    /// running. If monitoring is active, the new device starts checking right away.
    /// </summary>
    public DeviceMonitor Add(DeviceConfig device)
    {
        var monitor = Attach(new DeviceMonitor(device, Logger));
        Config.Devices.Add(device);
        RebuildGroupGates();

        if (IsMonitoring && device.Enabled)
            monitor.Start();

        return monitor;
    }

    public async Task RemoveAsync(Guid id)
    {
        if (_monitors.TryRemove(id, out var monitor))
        {
            monitor.Alert -= OnDeviceAlert;
            monitor.DescriptionDiscovered -= OnDescriptionDiscovered;
            await monitor.StopAsync().ConfigureAwait(false);
            monitor.Dispose();
        }
        Config.Devices.RemoveAll(d => d.Id == id);
        RebuildGroupGates();
    }

    public DeviceMonitor? Get(Guid id) => _monitors.TryGetValue(id, out var m) ? m : null;

    /// <summary>Starts every enabled device that is not already running. Running loops are left alone.</summary>
    public void StartAll()
    {
        IsMonitoring = true;
        foreach (var m in _monitors.Values)
            if (m.Config.Enabled && !m.IsRunning) m.Start();
    }

    public async Task StopAllAsync()
    {
        IsMonitoring = false;
        await Task.WhenAll(_monitors.Values.Select(m => m.StopAsync())).ConfigureAwait(false);
    }

    public void ResetAll()
    {
        foreach (var m in _monitors.Values) m.ResetCounters();
    }

    public bool AnyRunning => _monitors.Values.Any(m => m.IsRunning);

    public void Save() => ConfigStore.Save(Config);

    // ---------- Daily summary ----------

    /// <summary>Writes summary_yyyyMMdd.csv for any day that finished since the last check.</summary>
    public void WriteCompletedDays()
    {
        if (!Config.WriteDailySummary) return;

        try
        {
            var now = DateTime.Now;
            var completed = new List<DailySummary>();
            foreach (var monitor in _monitors.Values)
            {
                var summary = monitor.TakeCompletedDay(now);
                if (summary != null) completed.Add(summary);
            }

            foreach (var group in completed.GroupBy(s => s.Date.Date))
                SummaryWriter.Write(Logger.Directory, group.Key, group, Config.CsvSeparator);
        }
        catch (Exception ex)
        {
            LastSummaryError = ex.Message;
        }
    }

    /// <summary>Writes the summary for today as it stands right now. Returns the file path.</summary>
    public string WriteSummaryNow()
    {
        var rows = _monitors.Values.Select(m => m.GetDailySummary()).ToList();
        return SummaryWriter.Write(Logger.Directory, DateTime.Today, rows, Config.CsvSeparator);
    }

    public string? LastSummaryError { get; private set; }

    public async ValueTask DisposeAsync()
    {
        await _summaryTimer.DisposeAsync().ConfigureAwait(false);
        Scheduler.Dispose();
        await StopAllAsync().ConfigureAwait(false);

        foreach (var m in _monitors.Values)
        {
            m.Alert -= OnDeviceAlert;
            m.DescriptionDiscovered -= OnDescriptionDiscovered;
            m.Dispose();
        }

        WriteCompletedDays();
        Alerts.Dispose();
        await Logger.DisposeAsync().ConfigureAwait(false);
    }
}
