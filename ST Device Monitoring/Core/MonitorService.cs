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

        foreach (var device in config.Devices)
            Attach(new DeviceMonitor(device, Logger));

        RebuildGroupGates();

        // Checks once a minute whether a day has finished and needs a summary file.
        _summaryTimer = new Timer(_ => WriteCompletedDays(), null,
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public IReadOnlyCollection<DeviceMonitor> Monitors => _monitors.Values.ToList();

    /// <summary>
    /// Links every group to its master. Devices in a group that has a master are only checked
    /// while that master answers - when the uplink is down the devices behind it are paused
    /// instead of all failing at once.
    /// Call this whenever devices are added, removed or edited.
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

        foreach (var monitor in _monitors.Values)
        {
            var group = monitor.Config.Group?.Trim() ?? string.Empty;

            if (group.Length == 0 || monitor.Config.IsGroupMaster ||
                !masters.TryGetValue(group, out var master) || ReferenceEquals(master, monitor))
            {
                monitor.CanCheck = null;
                monitor.GateSourceName = null;
                continue;
            }

            var gateMaster = master;
            monitor.GateSourceName = gateMaster.Config.Name;
            // Open gate when the master is up, unknown, stopped or disabled - only a confirmed
            // outage on the master pauses the rest of the group.
            monitor.CanCheck = () => !gateMaster.IsDown;
        }
    }

    /// <summary>The master of a group, if one is defined.</summary>
    public DeviceMonitor? GetGroupMaster(string group)
    {
        if (string.IsNullOrWhiteSpace(group)) return null;
        return _monitors.Values.FirstOrDefault(m =>
            m.Config.IsGroupMaster &&
            string.Equals(m.Config.Group?.Trim(), group.Trim(), StringComparison.OrdinalIgnoreCase));
    }

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
