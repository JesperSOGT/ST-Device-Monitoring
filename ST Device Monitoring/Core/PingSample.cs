namespace ST_Device_Monitoring.Core;

/// <summary>Result of a single ping. Used for the history graph and for logging.</summary>
public readonly struct PingSample
{
    public DateTime Timestamp { get; init; }
    public bool Success { get; init; }
    public long RoundtripMs { get; init; }
    /// <summary>IPStatus as text, e.g. "Success", "TimedOut", "DestinationHostUnreachable".</summary>
    public string Status { get; init; }
    public string? Error { get; init; }
    /// <summary>False = empty slot in the history (no measurement yet).</summary>
    public bool HasValue { get; init; }
}

/// <summary>Snapshot of the counters for a device. Copied to the UI thread.</summary>
public readonly struct DeviceStats
{
    public long Sent { get; init; }
    public long Success { get; init; }
    public long Failed { get; init; }
    public long ConsecutiveFails { get; init; }
    public long MaxConsecutiveFails { get; init; }
    public long OutageCount { get; init; }
    public long LastRtt { get; init; }
    public double AvgRtt { get; init; }
    public long MinRtt { get; init; }
    public long MaxRtt { get; init; }
    public DateTime? LastSuccess { get; init; }
    public DateTime? LastFail { get; init; }
    public string? LastError { get; init; }
    public bool Running { get; init; }
    public DeviceState State { get; init; }

    /// <summary>True when failure logging is currently suppressed for this device.</summary>
    public bool LoggingSuppressed { get; init; }
    /// <summary>Number of failed checks not written to the log during the current outage.</summary>
    public long SuppressedEntries { get; init; }
    /// <summary>Loss and jitter over the last 60 seconds - reacts immediately, unlike the totals.</summary>
    public RollingStats Rolling { get; init; }

    /// <summary>True when checking is paused because the group master is down.</summary>
    public bool Blocked { get; init; }

    /// <summary>Name of the group master that controls this device, if any.</summary>
    public string? GateSource { get; init; }

    public double FailPercent => Sent == 0 ? 0 : Failed * 100.0 / Sent;
}

public enum DeviceState
{
    Disabled,
    Stopped,
    Unknown,
    Ok,
    Warning,
    Error,
    /// <summary>Paused because the group master (uplink) is down - the device is not checked.</summary>
    Blocked
}
