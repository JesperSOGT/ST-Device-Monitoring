using System.ComponentModel;
using System.Runtime.CompilerServices;
using ST_Device_Monitoring.Core;
using ST_Device_Monitoring.Models;

namespace ST_Device_Monitoring.ViewModels;

/// <summary>
/// UI representation of a device. Reads a snapshot of the counters from DeviceMonitor on the
/// UI thread (via timer), so the ping threads never touch WPF objects.
/// </summary>
public sealed class DeviceViewModel : INotifyPropertyChanged
{
    private DeviceStats _stats;
    private long _lastSignature = -1;

    public DeviceViewModel(DeviceMonitor monitor)
    {
        Monitor = monitor;
        _stats = monitor.GetStats();
    }

    public DeviceMonitor Monitor { get; }
    public DeviceConfig Config => Monitor.Config;

    public Guid Id => Config.Id;
    public string Name => Config.Name;
    public string Host => Config.Host;
    public string Group => Config.Group;
    public string ModeText => Config.ModeText;
    public string Description => Config.Description;
    public string MacAddress => Config.MacAddress;

    /// <summary>Manufacturer resolved from the MAC address, like Wireshark shows it.</summary>
    public string Vendor => Core.MacVendorLookup.Lookup(Config.MacAddress);
    public bool IsGroupMaster => Config.IsGroupMaster;

    /// <summary>Group column - the master is marked so the dependency is visible in the list.</summary>
    public string GroupText => Config.IsGroupMaster && !string.IsNullOrWhiteSpace(Config.Group)
        ? $"{Config.Group}  ★ master"
        : Config.Group;
    public int IntervalMs => Config.IntervalMs;
    public int DownIntervalMs => Config.DownIntervalMs;
    public int TimeoutMs => Config.TimeoutMs;
    public int MaxLoggedFailures => Config.MaxLoggedFailures;
    public bool Enabled => Config.Enabled;

    /// <summary>Interval column text - shows the down interval too when it differs.</summary>
    public string IntervalText => Config.DownIntervalMs > 0 && Config.DownIntervalMs != Config.IntervalMs
        ? $"{Config.IntervalMs} / {Config.DownIntervalMs} ms"
        : $"{Config.IntervalMs} ms";

    /// <summary>Packet loss over the last 60 seconds.</summary>
    public string Loss60Text => _stats.Rolling.Sent == 0 ? "-" : $"{_stats.Rolling.LossPercent:0.0} %";

    /// <summary>Average jitter over the last 60 seconds.</summary>
    public string Jitter60Text => _stats.Rolling.Sent == 0 ? "-" : $"{_stats.Rolling.AvgJitter:0.0} ms";

    public double Loss60 => _stats.Rolling.LossPercent;

    public DeviceState State => _stats.State;

    public string StateText => _stats.State switch
    {
        DeviceState.Ok => "OK",
        DeviceState.Warning => "Unstable",
        DeviceState.Error => "FAIL",
        DeviceState.Stopped => "Stopped",
        DeviceState.Disabled => "Disabled",
        DeviceState.Blocked => "Paused",
        _ => "Unknown"
    };

    /// <summary>Explains a paused device in the detail panel and the error column.</summary>
    public string BlockedText => _stats.Blocked
        ? $"Paused - group master \"{_stats.GateSource}\" is down"
        : string.Empty;

    public long Sent => _stats.Sent;
    public long Failed => _stats.Failed;
    public long ConsecutiveFails => _stats.ConsecutiveFails;
    public long MaxConsecutiveFails => _stats.MaxConsecutiveFails;
    public long OutageCount => _stats.OutageCount;

    public string LastRttText => _stats.Sent == 0 ? "-"
        : _stats.State == DeviceState.Error ? "-"
        : $"{_stats.LastRtt} ms";

    public string AvgRttText => _stats.Success == 0 ? "-" : $"{_stats.AvgRtt:0.0} ms";
    public string MinMaxRttText => _stats.Success == 0 ? "-" : $"{_stats.MinRtt} / {_stats.MaxRtt} ms";
    public string FailPercentText => _stats.Sent == 0 ? "-" : $"{_stats.FailPercent:0.00} %";
    public string LastOkText => _stats.LastSuccess?.ToString("dd-MM HH:mm:ss") ?? "-";
    public string LastFailText => _stats.LastFail?.ToString("dd-MM HH:mm:ss") ?? "-";
    public string LastError => _stats.Blocked ? BlockedText : _stats.LastError ?? "";
    public bool IsRunning => _stats.Running;

    public bool LoggingSuppressed => _stats.LoggingSuppressed;
    public long SuppressedEntries => _stats.SuppressedEntries;

    /// <summary>Shown in the "Logging" column.</summary>
    public string LoggingText => _stats.LoggingSuppressed
        ? $"Paused ({_stats.SuppressedEntries:N0})"
        : Config.MaxLoggedFailures <= 0 ? "On (all)" : "On";

    public string Header => $"{Name}  ({Host})";

    public PingSample[] History => Monitor.GetHistory();

    /// <summary>Called from the UI timer. Only updates bindings when something changed.</summary>
    public void Refresh()
    {
        var stats = Monitor.GetStats();
        var signature = stats.Sent * 31 + stats.Failed * 17 + stats.LastRtt * 7
                        + (long)stats.State * 3 + stats.ConsecutiveFails
                        + (stats.LoggingSuppressed ? 1 : 0)
                        + (stats.Blocked ? 5 : 0)
                        + (long)(stats.Rolling.LossPercent * 10) * 13
                        + (long)(stats.Rolling.AvgJitter * 10) * 11;
        _stats = stats;

        if (signature == _lastSignature) return;
        _lastSignature = signature;
        OnPropertyChanged(string.Empty);
    }

    /// <summary>Called when the configuration has been edited.</summary>
    public void ConfigChanged()
    {
        _lastSignature = -1;
        OnPropertyChanged(string.Empty);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
