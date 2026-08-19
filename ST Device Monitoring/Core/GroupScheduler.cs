using ST_Device_Monitoring.Models;

namespace ST_Device_Monitoring.Core;

/// <summary>Where a group stands right now with respect to its schedule.</summary>
public readonly struct ScheduleState
{
    /// <summary>False when the group has no schedule at all - then it is always checked.</summary>
    public bool HasSchedule { get; init; }

    /// <summary>True when the devices in the group may be checked right now.</summary>
    public bool Open { get; init; }

    /// <summary>Why the group is paused. Only set when <see cref="Open"/> is false.</summary>
    public string? Reason { get; init; }

    /// <summary>When the next run starts. Null while a run is in progress.</summary>
    public DateTime? NextRun { get; init; }

    /// <summary>When the current run ends. Null while the group is paused.</summary>
    public DateTime? RunEnds { get; init; }

    /// <summary>A group without a schedule - checked continuously, as before.</summary>
    public static ScheduleState NoSchedule => new() { Open = true };
}

/// <summary>
/// Runs the per-group schedules. A schedule turns continuous monitoring into short runs -
/// "every 30 minutes, check for 2 minutes, 07:00-17:00 on weekdays" - which is what you want for
/// sites that only need a periodic health check rather than millisecond monitoring.
///
/// The states are recalculated once a second on a background timer and published as an immutable
/// snapshot, so the many device check loops only read a dictionary and never do date arithmetic.
/// </summary>
public sealed class GroupScheduler : IDisposable
{
    private readonly object _lock = new();
    private readonly Timer _timer;
    private List<GroupSchedule> _schedules = new();
    private volatile Dictionary<string, ScheduleState> _states =
        new(StringComparer.OrdinalIgnoreCase);

    public GroupScheduler(IEnumerable<GroupSchedule>? schedules)
    {
        Reload(schedules);
        _timer = new Timer(_ => Recompute(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    /// <summary>Applies an edited set of schedules. Running check loops pick it up within a second.</summary>
    public void Reload(IEnumerable<GroupSchedule>? schedules)
    {
        lock (_lock)
        {
            _schedules = (schedules ?? Enumerable.Empty<GroupSchedule>())
                .Where(s => !string.IsNullOrWhiteSpace(s.Group))
                .Select(s => s.Clone())
                .ToList();
        }
        Recompute();
    }

    /// <summary>The schedule defined for a group, or null.</summary>
    public GroupSchedule? Find(string? group)
    {
        if (string.IsNullOrWhiteSpace(group)) return null;
        lock (_lock)
            return _schedules.FirstOrDefault(s =>
                string.Equals(s.Group.Trim(), group.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True when the group has a schedule that is switched on.</summary>
    public bool IsScheduled(string? group) => Find(group) is { Enabled: true };

    /// <summary>Current state of a group. Groups without a schedule are always open.</summary>
    public ScheduleState GetState(string? group)
    {
        if (string.IsNullOrWhiteSpace(group)) return ScheduleState.NoSchedule;
        return _states.TryGetValue(group.Trim(), out var state) ? state : ScheduleState.NoSchedule;
    }

    private void Recompute()
    {
        try
        {
            List<GroupSchedule> schedules;
            lock (_lock) schedules = _schedules;

            var now = DateTime.Now;
            var map = new Dictionary<string, ScheduleState>(StringComparer.OrdinalIgnoreCase);
            foreach (var schedule in schedules)
                map[schedule.Group.Trim()] = Evaluate(schedule, now);

            _states = map;
        }
        catch
        {
            // A failing schedule calculation must never stop monitoring - the previous snapshot stays.
        }
    }

    /// <summary>
    /// Works out whether a group should be checking at the given moment.
    /// The runs are anchored to the start of the active window, so they always land on the same
    /// clock times and survive a restart of the program.
    /// </summary>
    public static ScheduleState Evaluate(GroupSchedule schedule, DateTime now)
    {
        // A schedule that is switched off - or one that does not make sense - never pauses anything.
        if (!schedule.Enabled || schedule.Validate() != null)
            return ScheduleState.NoSchedule;

        var period = schedule.Period;
        var run = schedule.RunLength;
        var windowLength = schedule.WindowLength;

        var windowStart = FindOpenWindow(schedule, now, windowLength);

        if (windowStart == null)
        {
            var next = NextWindowStart(schedule, now);
            return new ScheduleState
            {
                HasSchedule = true,
                Open = false,
                NextRun = next,
                Reason = $"Outside the schedule for \"{schedule.Group}\" - next run {Describe(next, now)}"
            };
        }

        var windowEnd = windowStart.Value + windowLength;
        var elapsed = now - windowStart.Value;
        var index = elapsed.Ticks / period.Ticks;

        var runStart = windowStart.Value + TimeSpan.FromTicks(index * period.Ticks);
        var runEnd = runStart + run;
        if (runEnd > windowEnd) runEnd = windowEnd;

        if (now < runEnd)
            return new ScheduleState
            {
                HasSchedule = true,
                Open = true,
                RunEnds = runEnd
            };

        var nextRun = windowStart.Value + TimeSpan.FromTicks((index + 1) * period.Ticks);
        if (nextRun >= windowEnd)
            nextRun = NextWindowStart(schedule, windowEnd);

        return new ScheduleState
        {
            HasSchedule = true,
            Open = false,
            NextRun = nextRun,
            Reason = $"Scheduled run finished for \"{schedule.Group}\" - next run {Describe(nextRun, now)}"
        };
    }

    /// <summary>The active window containing <paramref name="now"/>, or null when there is none.</summary>
    private static DateTime? FindOpenWindow(GroupSchedule schedule, DateTime now, TimeSpan windowLength)
    {
        var from = schedule.From;

        // A window that crosses midnight may have started yesterday.
        for (int back = 0; back <= 1; back++)
        {
            var start = now.Date.AddDays(-back) + from;
            if (now < start || now >= start + windowLength) continue;
            if (!GroupSchedule.Includes(schedule.Days, start.DayOfWeek)) continue;
            return start;
        }
        return null;
    }

    /// <summary>The first window start at or after <paramref name="after"/> that falls on a selected day.</summary>
    private static DateTime NextWindowStart(GroupSchedule schedule, DateTime after)
    {
        var from = schedule.From;
        for (int day = 0; day <= 8; day++)
        {
            var candidate = after.Date.AddDays(day) + from;
            if (candidate < after) continue;
            if (!GroupSchedule.Includes(schedule.Days, candidate.DayOfWeek)) continue;
            return candidate;
        }
        return after.Date.AddDays(1) + from;
    }

    /// <summary>"at 14:30", "tomorrow at 07:00", "Mon 25-08 at 07:00".</summary>
    public static string Describe(DateTime when, DateTime now)
    {
        var time = when.ToString("HH:mm");
        var days = (when.Date - now.Date).Days;
        return days switch
        {
            0 => $"at {time}",
            1 => $"tomorrow at {time}",
            _ => $"{when:ddd dd-MM} at {time}"
        };
    }

    public void Dispose() => _timer.Dispose();
}
