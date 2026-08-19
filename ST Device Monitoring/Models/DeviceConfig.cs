using System.Text.Json.Serialization;

namespace ST_Device_Monitoring.Models;

/// <summary>How a device is checked.</summary>
public enum CheckMode
{
    /// <summary>ICMP echo (classic ping).</summary>
    Icmp,
    /// <summary>TCP connect to <see cref="DeviceConfig.Port"/> - for devices that block ICMP.</summary>
    TcpPort,
    /// <summary>SNMP GET (v2c) - also reads the device description from sysDescr.</summary>
    Snmp
}

/// <summary>
/// Configuration for a single monitored device. Serialized to devices.json.
/// </summary>
public sealed class DeviceConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Free text name, e.g. "PLC Quay 3".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>IP address or hostname.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>Optional group/site, used for filtering, e.g. "Quay 3" or "Cabinet A".</summary>
    public string Group { get; set; } = string.Empty;

    /// <summary>
    /// Marks this device as the group's master (typically the uplink switch or router).
    /// The other devices in the same group are only checked while the master answers - when the
    /// master is down they are paused instead of producing a storm of failures and alarms.
    /// Only one device per group can be the master.
    /// </summary>
    public bool IsGroupMaster { get; set; }

    /// <summary>ICMP ping, TCP connect or SNMP GET.</summary>
    public CheckMode Mode { get; set; } = CheckMode.Icmp;

    /// <summary>
    /// Port used when <see cref="Mode"/> is <see cref="CheckMode.TcpPort"/> (e.g. 502 Modbus,
    /// 102 S7, 80 HTTP) or <see cref="CheckMode.Snmp"/> (usually 161).
    /// </summary>
    public int Port { get; set; } = 502;

    /// <summary>SNMP v2c community, used when <see cref="Mode"/> is <see cref="CheckMode.Snmp"/>.</summary>
    public string Community { get; set; } = "public";

    /// <summary>
    /// Free text description. Filled automatically from SNMP sysDescr the first time an
    /// SNMP check succeeds, and can be edited by hand.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// MAC address, looked up from the Windows ARP table the first time the device answers.
    /// Only devices on the same subnet have an ARP entry - a device behind a router stays empty.
    /// </summary>
    public string MacAddress { get; set; } = string.Empty;

    /// <summary>Check interval in milliseconds while the device is up (minimum 20 ms).</summary>
    public int IntervalMs { get; set; } = 1000;

    /// <summary>
    /// Interval used while the device is down (after <see cref="FailThreshold"/> consecutive
    /// failures). Keeps a fast 100 ms device from hammering the network while it is offline.
    /// 0 = keep using <see cref="IntervalMs"/>.
    /// </summary>
    public int DownIntervalMs { get; set; } = 1000;

    /// <summary>Timeout per check in milliseconds.</summary>
    public int TimeoutMs { get; set; } = 1000;

    /// <summary>Number of consecutive failures before the device is flagged as "FAIL".</summary>
    public int FailThreshold { get; set; } = 1;

    /// <summary>
    /// Number of consecutive failures that are written to the log. When the device keeps
    /// failing beyond this count, logging of further failures is suppressed - checking
    /// continues, and a RECOVERED entry is written when the device comes back online.
    /// 0 = log every failure (no suppression).
    /// </summary>
    public int MaxLoggedFailures { get; set; } = 5;

    /// <summary>Enabled/disabled without removing the device from the list.</summary>
    public bool Enabled { get; set; } = true;

    [JsonIgnore]
    public string Display => string.IsNullOrWhiteSpace(Name) ? Host : $"{Name} ({Host})";

    /// <summary>"ICMP", "TCP 502" or "SNMP 161".</summary>
    [JsonIgnore]
    public string ModeText => Mode switch
    {
        CheckMode.TcpPort => $"TCP {Port}",
        CheckMode.Snmp => $"SNMP {Port}",
        _ => "ICMP"
    };

    /// <summary>Identifies the endpoint - used for duplicate detection.</summary>
    [JsonIgnore]
    public string Endpoint => Mode == CheckMode.Icmp
        ? Host.Trim().ToLowerInvariant()
        : $"{Host.Trim().ToLowerInvariant()}:{Port}/{Mode}";

    public DeviceConfig Clone() => (DeviceConfig)MemberwiseClone();

    /// <summary>Returns null if the configuration is valid, otherwise an error message.</summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Name)) return "Name is required.";

        var hostError = Core.NetworkValidation.ValidateHost(Host);
        if (hostError != null) return hostError;

        if (Mode != CheckMode.Icmp && (Port < 1 || Port > 65535)) return "Port must be between 1 and 65535.";
        if (Mode == CheckMode.Snmp && string.IsNullOrWhiteSpace(Community)) return "SNMP community is required.";
        if (IntervalMs < 20) return "Interval must be at least 20 ms.";
        if (IntervalMs > 3_600_000) return "Interval must not exceed 3,600,000 ms (1 hour).";
        if (DownIntervalMs < 0) return "Down interval must be 0 or higher (0 = same as interval).";
        if (DownIntervalMs > 3_600_000) return "Down interval must not exceed 3,600,000 ms (1 hour).";
        if (TimeoutMs < 20) return "Timeout must be at least 20 ms.";
        if (TimeoutMs > 60_000) return "Timeout must not exceed 60,000 ms.";
        if (FailThreshold < 1) return "Fail threshold must be at least 1.";
        if (MaxLoggedFailures < 0) return "Max logged failures must be 0 or higher (0 = log all).";
        if (IsGroupMaster && string.IsNullOrWhiteSpace(Group))
            return "A group master needs a group - the other devices in that group follow it.";
        return null;
    }

    /// <summary>Non-blocking warning, or null. Shown as a confirmation before saving.</summary>
    public string? GetWarning()
    {
        if (TimeoutMs > IntervalMs)
            return $"Timeout ({TimeoutMs} ms) is longer than the interval ({IntervalMs} ms). " +
                   "The device will be checked more slowly than the interval whenever it is slow or down.";
        return null;
    }
}

/// <summary>What happens when a log file hits its size limit.</summary>
public enum LogRotationMode
{
    /// <summary>No limit - one file per day, however big it gets.</summary>
    None,
    /// <summary>Close the file, start a new one and compress the old file to .zip.</summary>
    RotateAndZip,
    /// <summary>Keep writing to the same file and drop the oldest lines when the limit is reached.</summary>
    RingBuffer
}

/// <summary>Root object of devices.json.</summary>
public sealed class AppConfig
{
    public List<DeviceConfig> Devices { get; set; } = new();

    /// <summary>Folder for the CSV logs. A relative path is resolved against the program folder.</summary>
    public string LogDirectory { get; set; } = "Logs";

    /// <summary>Log every single check including response time (otherwise failures only).</summary>
    public bool LogAllPings { get; set; } = true;

    /// <summary>How often the UI is refreshed from the counters (ms).</summary>
    public int UiRefreshMs { get; set; } = 250;

    /// <summary>CSV field separator.</summary>
    public string CsvSeparator { get; set; } = ";";

    /// <summary>Delete log files (and zip archives) older than this number of days. 0 = never delete.</summary>
    public int LogRetentionDays { get; set; } = 30;

    /// <summary>What happens when a log file reaches <see cref="MaxLogFileSizeMB"/>.</summary>
    public LogRotationMode LogRotation { get; set; } = LogRotationMode.None;

    /// <summary>Size limit per log file in megabytes. 0 = no limit.</summary>
    public int MaxLogFileSizeMB { get; set; } = 50;

    /// <summary>
    /// Ring buffer mode: how much of the oldest content is dropped when the limit is reached,
    /// in percent (10-90). A larger value means the file is trimmed less often.
    /// </summary>
    public int RingTrimPercent { get; set; } = 30;

    /// <summary>Start monitoring automatically when the application opens.</summary>
    public bool AutoStart { get; set; } = false;

    /// <summary>Write summary_yyyyMMdd.csv automatically when the day rolls over.</summary>
    public bool WriteDailySummary { get; set; } = true;

    public AlertSettings Alerts { get; set; } = new();
    public UiSettings Ui { get; set; } = new();
}

/// <summary>Notification settings.</summary>
public sealed class AlertSettings
{
    /// <summary>Show a balloon/toast from the tray icon on state changes.</summary>
    public bool BalloonEnabled { get; set; } = true;

    /// <summary>Play a sound when a device goes down.</summary>
    public bool SoundEnabled { get; set; } = true;

    /// <summary>Optional .wav file. Empty = Windows' standard "hand" sound.</summary>
    public string SoundFile { get; set; } = string.Empty;

    /// <summary>Also notify when a device recovers.</summary>
    public bool NotifyOnRecovery { get; set; } = true;

    /// <summary>Minimum seconds between two notifications for the same device.</summary>
    public int ThrottleSeconds { get; set; } = 30;

    public bool EmailEnabled { get; set; } = false;
    public string SmtpHost { get; set; } = string.Empty;
    public int SmtpPort { get; set; } = 587;
    public bool SmtpUseSsl { get; set; } = true;
    public string SmtpUser { get; set; } = string.Empty;
    /// <summary>DPAPI-protected (current user). Never stored in clear text.</summary>
    public string SmtpPasswordProtected { get; set; } = string.Empty;
    public string MailFrom { get; set; } = string.Empty;
    /// <summary>One or more recipients, separated by ; or ,.</summary>
    public string MailTo { get; set; } = string.Empty;

    public bool WebhookEnabled { get; set; } = false;
    public string WebhookUrl { get; set; } = string.Empty;
}

/// <summary>Window/tray behaviour.</summary>
public sealed class UiSettings
{
    public bool ShowTrayIcon { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;
    /// <summary>Closing the window hides it in the tray instead of exiting.</summary>
    public bool CloseToTray { get; set; } = false;
    /// <summary>Register the application under HKCU\...\Run so it starts at logon.</summary>
    public bool StartWithWindows { get; set; } = false;
    /// <summary>Start minimized to the tray.</summary>
    public bool StartMinimized { get; set; } = false;
}
