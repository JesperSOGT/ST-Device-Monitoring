using System.Globalization;
using System.IO;
using System.Text;

namespace ST_Device_Monitoring.Core;

/// <summary>One row in summary_yyyyMMdd.csv.</summary>
public sealed class DailySummary
{
    public DateTime Date { get; set; }
    public string DeviceName { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public string Mode { get; set; } = string.Empty;
    public string Group { get; set; } = string.Empty;
    public long Checks { get; set; }
    public long Failures { get; set; }
    public long Outages { get; set; }
    public TimeSpan LongestOutage { get; set; }
    public TimeSpan TotalDowntime { get; set; }
    public double AvgRtt { get; set; }
    public long MaxRtt { get; set; }

    public double UptimePercent => Checks == 0 ? 0 : (Checks - Failures) * 100.0 / Checks;
}

/// <summary>
/// Accumulates per-day figures for one device. When the date changes, the finished day is
/// handed over so it can be written to summary_yyyyMMdd.csv.
/// </summary>
public sealed class DailyAccumulator
{
    private readonly object _lock = new();
    private DateTime _date = DateTime.MinValue;
    private long _checks, _failures, _outages, _rttSum, _rttCount, _maxRtt;
    private TimeSpan _longestOutage, _totalDowntime;
    private DailySummary? _completed;

    public void AddCheck(DateTime timestamp, bool success, long rtt)
    {
        lock (_lock)
        {
            RollIfNeeded(timestamp.Date);
            _checks++;
            if (!success) { _failures++; return; }
            _rttSum += rtt;
            _rttCount++;
            if (rtt > _maxRtt) _maxRtt = rtt;
        }
    }

    public void AddOutage(DateTime endTimestamp, TimeSpan duration)
    {
        lock (_lock)
        {
            RollIfNeeded(endTimestamp.Date);
            _outages++;
            _totalDowntime += duration;
            if (duration > _longestOutage) _longestOutage = duration;
        }
    }

    private void RollIfNeeded(DateTime date)
    {
        if (_date == DateTime.MinValue)
        {
            _date = date;
            return;
        }
        if (date == _date) return;

        _completed = BuildLocked();
        _date = date;
        _checks = _failures = _outages = _rttSum = _rttCount = _maxRtt = 0;
        _longestOutage = TimeSpan.Zero;
        _totalDowntime = TimeSpan.Zero;
    }

    /// <summary>Returns a finished day exactly once (after midnight), otherwise null.</summary>
    public DailySummary? TakeCompletedDay(DateTime now)
    {
        lock (_lock)
        {
            RollIfNeeded(now.Date);
            var done = _completed;
            _completed = null;
            return done;
        }
    }

    /// <summary>Snapshot of the day so far (used by "Write report now").</summary>
    public DailySummary GetCurrent()
    {
        lock (_lock) return BuildLocked();
    }

    private DailySummary BuildLocked() => new()
    {
        Date = _date == DateTime.MinValue ? DateTime.Today : _date,
        Checks = _checks,
        Failures = _failures,
        Outages = _outages,
        LongestOutage = _longestOutage,
        TotalDowntime = _totalDowntime,
        AvgRtt = _rttCount == 0 ? 0 : (double)_rttSum / _rttCount,
        MaxRtt = _maxRtt
    };

    public void Reset()
    {
        lock (_lock)
        {
            _checks = _failures = _outages = _rttSum = _rttCount = _maxRtt = 0;
            _longestOutage = TimeSpan.Zero;
            _totalDowntime = TimeSpan.Zero;
            _completed = null;
        }
    }
}

/// <summary>Writes summary_yyyyMMdd.csv (one row per device).</summary>
public static class SummaryWriter
{
    public static string Write(string directory, DateTime date, IEnumerable<DailySummary> rows, string separator = ";")
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"summary_{date:yyyyMMdd}.csv");

        var sb = new StringBuilder();
        sb.AppendLine($"sep={separator}");
        sb.AppendLine(string.Join(separator,
            "Date", "Device", "Host", "Mode", "Group", "Checks", "Failures", "UptimePercent",
            "Outages", "LongestOutage", "TotalDowntime", "AvgResponseMs", "MaxResponseMs"));

        foreach (var r in rows.OrderBy(r => r.Group).ThenBy(r => r.DeviceName))
        {
            sb.AppendLine(string.Join(separator,
                r.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Escape(r.DeviceName, separator),
                Escape(r.Host, separator),
                Escape(r.Mode, separator),
                Escape(r.Group, separator),
                r.Checks.ToString(CultureInfo.InvariantCulture),
                r.Failures.ToString(CultureInfo.InvariantCulture),
                r.UptimePercent.ToString("0.000", CultureInfo.InvariantCulture),
                r.Outages.ToString(CultureInfo.InvariantCulture),
                Format(r.LongestOutage),
                Format(r.TotalDowntime),
                r.AvgRtt.ToString("0.0", CultureInfo.InvariantCulture),
                r.MaxRtt.ToString(CultureInfo.InvariantCulture)));
        }

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        return path;
    }

    private static string Format(TimeSpan span) =>
        $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}";

    private static string Escape(string value, string separator)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var needsQuotes = value.Contains(separator, StringComparison.Ordinal)
                          || value.Contains('"') || value.Contains('\n') || value.Contains('\r');
        return needsQuotes ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}
