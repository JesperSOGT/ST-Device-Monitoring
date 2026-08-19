using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace ST_Device_Monitoring.Models;

/// <summary>Unit for the interval between two scheduled runs.</summary>
public enum ScheduleUnit
{
    Minutes,
    Hours
}

/// <summary>Days a schedule is allowed to run on.</summary>
[Flags]
public enum ScheduleDays
{
    None = 0,
    Monday = 1,
    Tuesday = 2,
    Wednesday = 4,
    Thursday = 8,
    Friday = 16,
    Saturday = 32,
    Sunday = 64,
    Weekdays = Monday | Tuesday | Wednesday | Thursday | Friday,
    Weekend = Saturday | Sunday,
    All = Weekdays | Weekend
}

/// <summary>
/// Schedule for one group. Instead of checking the group all the time, the devices in it are
/// only checked in short runs: "every 30 minutes, check for 2 minutes, between 07:00 and 17:00
/// on weekdays". Outside a run the devices in the group are paused - they are not checked, no
/// failures are counted and no alarms are raised.
///
/// The runs are anchored to the start of the active window, so they land on predictable times
/// (07:00, 07:30, 08:00 …) and stay on the same times after a restart of the program.
/// </summary>
public sealed class GroupSchedule
{
    /// <summary>The group this schedule applies to. Matches <see cref="DeviceConfig.Group"/>.</summary>
    public string Group { get; set; } = string.Empty;

    /// <summary>False = the group is checked continuously, exactly as before.</summary>
    public bool Enabled { get; set; }

    /// <summary>Interval between two runs, counted in <see cref="Unit"/>.</summary>
    public int Every { get; set; } = 1;

    /// <summary>Minutes or hours.</summary>
    public ScheduleUnit Unit { get; set; } = ScheduleUnit.Hours;

    /// <summary>How long each run lasts, in seconds. The devices use their own interval while running.</summary>
    public int RunSeconds { get; set; } = 120;

    /// <summary>Start of the active window, "HH:mm". Equal to <see cref="ActiveTo"/> means all day.</summary>
    public string ActiveFrom { get; set; } = "00:00";

    /// <summary>End of the active window, "HH:mm". Earlier than <see cref="ActiveFrom"/> means the window crosses midnight.</summary>
    public string ActiveTo { get; set; } = "00:00";

    /// <summary>Days the window is allowed to start on.</summary>
    public ScheduleDays Days { get; set; } = ScheduleDays.All;

    public const int MaxRunSeconds = 24 * 60 * 60;

    /// <summary>Time between two runs.</summary>
    [JsonIgnore]
    public TimeSpan Period => Unit == ScheduleUnit.Hours
        ? TimeSpan.FromHours(Math.Max(1, Every))
        : TimeSpan.FromMinutes(Math.Max(1, Every));

    /// <summary>How long one run lasts.</summary>
    [JsonIgnore]
    public TimeSpan RunLength => TimeSpan.FromSeconds(Math.Clamp(RunSeconds, 1, MaxRunSeconds));

    /// <summary>Start of the active window as a time of day.</summary>
    [JsonIgnore]
    public TimeSpan From => TryParseTime(ActiveFrom, out var t) ? t : TimeSpan.Zero;

    /// <summary>End of the active window as a time of day.</summary>
    [JsonIgnore]
    public TimeSpan To => TryParseTime(ActiveTo, out var t) ? t : TimeSpan.Zero;

    /// <summary>True when the window covers the whole day (from and to are the same).</summary>
    [JsonIgnore]
    public bool AllDay => From == To;

    /// <summary>Length of the active window. 24 hours when it covers the whole day.</summary>
    [JsonIgnore]
    public TimeSpan WindowLength
    {
        get
        {
            if (AllDay) return TimeSpan.FromHours(24);
            var length = To - From;
            return length > TimeSpan.Zero ? length : length + TimeSpan.FromHours(24);
        }
    }

    public GroupSchedule Clone() => (GroupSchedule)MemberwiseClone();

    /// <summary>Returns null when the schedule is valid, otherwise an error message.</summary>
    public string? Validate()
    {
        if (!Enabled) return null;
        if (string.IsNullOrWhiteSpace(Group)) return "The schedule has no group.";
        if (Every < 1) return "Run every: must be at least 1.";
        if (Unit == ScheduleUnit.Minutes && Every > 1440) return "Run every: at most 1440 minutes (24 hours).";
        if (Unit == ScheduleUnit.Hours && Every > 168) return "Run every: at most 168 hours (7 days).";
        if (RunSeconds < 1) return "Each run must last at least 1 second.";
        if (RunSeconds > MaxRunSeconds) return "Each run must last at most 24 hours.";
        if (!TryParseTime(ActiveFrom, out _)) return $"\"{ActiveFrom}\" is not a valid time. Use HH:mm, for example 07:00.";
        if (!TryParseTime(ActiveTo, out _)) return $"\"{ActiveTo}\" is not a valid time. Use HH:mm, for example 17:00.";
        if (Days == ScheduleDays.None) return "Pick at least one day.";
        if (RunLength >= Period)
            return $"Each run lasts {DescribeSeconds(RunSeconds)}, which is as long as or longer than the " +
                   $"interval of {DescribePeriodExact()} - the group would never pause. " +
                   "Shorten the run or make the interval longer.";
        if (RunLength > WindowLength)
            return "Each run is longer than the active time window.";
        return null;
    }

    /// <summary>Warning that does not block saving, or null.</summary>
    public string? GetWarning()
    {
        if (!Enabled) return null;
        if (Period > WindowLength)
            return $"The interval of {DescribePeriodExact()} is longer than the active window " +
                   $"{FormatTime(From)}-{FormatTime(To)}, so the group only runs once per window.";
        return null;
    }

    /// <summary>"every 30 min · runs 2 min · 07:00-17:00 · Mon-Fri"</summary>
    [JsonIgnore]
    public string Summary
    {
        get
        {
            if (!Enabled) return "Continuous";

            var text = new StringBuilder();
            text.Append("every ").Append(DescribePeriod());
            text.Append(" · runs ").Append(DescribeSeconds(RunSeconds));
            if (!AllDay) text.Append(" · ").Append(FormatTime(From)).Append('-').Append(FormatTime(To));
            if (Days != ScheduleDays.All) text.Append(" · ").Append(DescribeDays(Days));
            return text.ToString();
        }
    }

    /// <summary>Reads well after the word "every": "hour", "2 hours", "30 min".</summary>
    public string DescribePeriod() => Unit == ScheduleUnit.Hours
        ? Every == 1 ? "hour" : $"{Every} hours"
        : Every == 1 ? "minute" : $"{Every} min";

    /// <summary>Reads well on its own: "1 hour", "2 hours", "30 minutes".</summary>
    public string DescribePeriodExact() => Unit == ScheduleUnit.Hours
        ? Every == 1 ? "1 hour" : $"{Every} hours"
        : Every == 1 ? "1 minute" : $"{Every} minutes";

    /// <summary>"90 s", "2 min", "1 h 30 min".</summary>
    public static string DescribeSeconds(int seconds)
    {
        if (seconds < 60) return $"{seconds} s";
        if (seconds % 3600 == 0) return $"{seconds / 3600} h";
        if (seconds < 3600) return seconds % 60 == 0 ? $"{seconds / 60} min" : $"{seconds / 60} min {seconds % 60} s";
        return $"{seconds / 3600} h {(seconds % 3600) / 60} min";
    }

    /// <summary>"Mon-Fri", "Sat, Sun", "every day".</summary>
    public static string DescribeDays(ScheduleDays days)
    {
        if (days == ScheduleDays.All) return "every day";
        if (days == ScheduleDays.Weekdays) return "Mon-Fri";
        if (days == ScheduleDays.Weekend) return "Sat, Sun";
        if (days == ScheduleDays.None) return "never";

        var names = new List<string>();
        if (days.HasFlag(ScheduleDays.Monday)) names.Add("Mon");
        if (days.HasFlag(ScheduleDays.Tuesday)) names.Add("Tue");
        if (days.HasFlag(ScheduleDays.Wednesday)) names.Add("Wed");
        if (days.HasFlag(ScheduleDays.Thursday)) names.Add("Thu");
        if (days.HasFlag(ScheduleDays.Friday)) names.Add("Fri");
        if (days.HasFlag(ScheduleDays.Saturday)) names.Add("Sat");
        if (days.HasFlag(ScheduleDays.Sunday)) names.Add("Sun");
        return string.Join(", ", names);
    }

    /// <summary>True when the given day is selected.</summary>
    public static bool Includes(ScheduleDays days, DayOfWeek day) => (days & ToFlag(day)) != 0;

    public static ScheduleDays ToFlag(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => ScheduleDays.Monday,
        DayOfWeek.Tuesday => ScheduleDays.Tuesday,
        DayOfWeek.Wednesday => ScheduleDays.Wednesday,
        DayOfWeek.Thursday => ScheduleDays.Thursday,
        DayOfWeek.Friday => ScheduleDays.Friday,
        DayOfWeek.Saturday => ScheduleDays.Saturday,
        _ => ScheduleDays.Sunday
    };

    /// <summary>
    /// Accepts "7", "7:00", "07:00", "0700" and "07.00" - the time separator is never taken from
    /// the Windows locale, so the same devices.json works on a Danish and an English machine.
    /// </summary>
    public static bool TryParseTime(string? text, out TimeSpan time)
    {
        time = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var value = text.Trim().Replace('.', ':');
        int hours, minutes;

        var colon = value.IndexOf(':');
        if (colon < 0)
        {
            if (value.Length == 4 && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var hhmm))
            {
                hours = hhmm / 100;
                minutes = hhmm % 100;
            }
            else if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var onlyHours))
            {
                hours = onlyHours;
                minutes = 0;
            }
            else return false;
        }
        else
        {
            if (!int.TryParse(value[..colon], NumberStyles.None, CultureInfo.InvariantCulture, out hours)) return false;
            var rest = value[(colon + 1)..];
            var second = rest.IndexOf(':');
            if (second >= 0) rest = rest[..second];
            if (!int.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out minutes)) return false;
        }

        if (hours == 24 && minutes == 0) { time = TimeSpan.Zero; return true; }
        if (hours < 0 || hours > 23 || minutes < 0 || minutes > 59) return false;

        time = new TimeSpan(hours, minutes, 0);
        return true;
    }

    public static string FormatTime(TimeSpan time)
        => $"{time.Hours:D2}:{time.Minutes:D2}";
}
