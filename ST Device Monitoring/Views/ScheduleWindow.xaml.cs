using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ST_Device_Monitoring.Core;
using ST_Device_Monitoring.Models;

namespace ST_Device_Monitoring.Views;

/// <summary>
/// Edits the schedule of each group: how often the group is checked, how long each run lasts and
/// inside which time window and on which days it is allowed to run.
/// Nothing is applied until Save is pressed, and running check loops are never restarted - a
/// changed schedule simply opens or closes the gate in front of the devices in that group.
/// </summary>
public partial class ScheduleWindow : Window
{
    /// <summary>One line in the group list.</summary>
    private sealed class GroupRow : INotifyPropertyChanged
    {
        public string Group { get; init; } = string.Empty;
        public int DeviceCount { get; init; }
        public GroupSchedule Schedule { get; set; } = new();
        public bool ExistedBefore { get; init; }

        public string Title => DeviceCount == 1 ? $"{Group}  (1 device)" : $"{Group}  ({DeviceCount} devices)";
        public string SummaryText => Schedule.Enabled ? Schedule.Summary : "Continuous";

        public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private readonly List<GroupRow> _rows = new();
    private readonly List<GroupSchedule> _orphans = new();
    private readonly DispatcherTimer _tick;
    private GroupRow? _current;
    private bool _loading;

    /// <summary>The edited schedules. Only valid after the dialog returns true.</summary>
    public List<GroupSchedule> Schedules { get; private set; } = new();

    public ScheduleWindow(IEnumerable<string> groups, IEnumerable<GroupSchedule> current, Func<string, int> deviceCount)
    {
        InitializeComponent();

        var existing = current?.ToList() ?? new List<GroupSchedule>();
        var groupList = groups
            .Select(g => g?.Trim() ?? string.Empty)
            .Where(g => g.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        foreach (var group in groupList)
        {
            var found = existing.FirstOrDefault(s =>
                string.Equals(s.Group?.Trim(), group, StringComparison.OrdinalIgnoreCase));

            _rows.Add(new GroupRow
            {
                Group = group,
                DeviceCount = deviceCount(group),
                ExistedBefore = found != null,
                Schedule = found?.Clone() ?? new GroupSchedule { Group = group }
            });
        }

        // Schedules for groups that are not in use right now are kept untouched.
        _orphans.AddRange(existing
            .Where(s => !groupList.Any(g => string.Equals(g, s.Group?.Trim(), StringComparison.OrdinalIgnoreCase)))
            .Select(s => s.Clone()));

        GroupList.ItemsSource = _rows;

        if (_rows.Count == 0)
        {
            NoGroupsText.Text = "No groups yet. Give the devices a group name in the device dialog - " +
                                "then the group can be scheduled here.";
            EditPanel.IsEnabled = false;
        }
        else
        {
            GroupList.SelectedIndex = 0;
        }

        // Keeps the "next run" line ticking while the dialog is open.
        _tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) => UpdatePreview();
        _tick.Start();
        Closed += (_, _) => _tick.Stop();
    }

    // ---------- Selection ----------

    private void Group_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // The row that was on screen keeps whatever was typed into it.
        if (e.RemovedItems.Count > 0 && e.RemovedItems[0] is GroupRow previous)
        {
            ReadForm(previous.Schedule);
            previous.Refresh();
        }

        _current = GroupList.SelectedItem as GroupRow;
        LoadForm();
    }

    private void LoadForm()
    {
        _loading = true;
        try
        {
            if (_current == null)
            {
                GroupHeader.Text = "Select a group";
                GroupSubHeader.Text = string.Empty;
                EditPanel.IsEnabled = false;
                return;
            }

            EditPanel.IsEnabled = true;
            var s = _current.Schedule;

            GroupHeader.Text = _current.Group;
            GroupSubHeader.Text = _current.DeviceCount == 1
                ? "1 device in this group"
                : $"{_current.DeviceCount} devices in this group";

            EnabledBox.IsChecked = s.Enabled;
            EveryBox.Text = s.Every.ToString();
            UnitBox.SelectedIndex = s.Unit == ScheduleUnit.Hours ? 1 : 0;
            RunBox.Text = s.RunSeconds.ToString();
            FromBox.Text = s.ActiveFrom;
            ToBox.Text = s.ActiveTo;

            MonBox.IsChecked = s.Days.HasFlag(ScheduleDays.Monday);
            TueBox.IsChecked = s.Days.HasFlag(ScheduleDays.Tuesday);
            WedBox.IsChecked = s.Days.HasFlag(ScheduleDays.Wednesday);
            ThuBox.IsChecked = s.Days.HasFlag(ScheduleDays.Thursday);
            FriBox.IsChecked = s.Days.HasFlag(ScheduleDays.Friday);
            SatBox.IsChecked = s.Days.HasFlag(ScheduleDays.Saturday);
            SunBox.IsChecked = s.Days.HasFlag(ScheduleDays.Sunday);
        }
        finally
        {
            _loading = false;
        }

        UpdatePreview();
    }

    /// <summary>Copies what is on screen into the given schedule. Invalid text is kept as typed.</summary>
    private void ReadForm(GroupSchedule target)
    {
        target.Group = _current?.Group ?? target.Group;
        target.Enabled = EnabledBox.IsChecked == true;
        target.Every = int.TryParse(EveryBox.Text.Trim(), out var every) ? every : 0;
        target.Unit = UnitBox.SelectedIndex == 1 ? ScheduleUnit.Hours : ScheduleUnit.Minutes;
        target.RunSeconds = int.TryParse(RunBox.Text.Trim(), out var run) ? run : 0;
        target.ActiveFrom = FromBox.Text.Trim();
        target.ActiveTo = ToBox.Text.Trim();

        var days = ScheduleDays.None;
        if (MonBox.IsChecked == true) days |= ScheduleDays.Monday;
        if (TueBox.IsChecked == true) days |= ScheduleDays.Tuesday;
        if (WedBox.IsChecked == true) days |= ScheduleDays.Wednesday;
        if (ThuBox.IsChecked == true) days |= ScheduleDays.Thursday;
        if (FriBox.IsChecked == true) days |= ScheduleDays.Friday;
        if (SatBox.IsChecked == true) days |= ScheduleDays.Saturday;
        if (SunBox.IsChecked == true) days |= ScheduleDays.Sunday;
        target.Days = days;
    }

    // ---------- Edits ----------

    private void Setting_Changed(object sender, RoutedEventArgs e) => Capture();
    private void Text_Changed(object sender, TextChangedEventArgs e) => Capture();
    private void Unit_Changed(object sender, SelectionChangedEventArgs e) => Capture();

    private void Capture()
    {
        if (_loading || _current == null) return;
        ReadForm(_current.Schedule);
        _current.Refresh();
        ErrorText.Text = string.Empty;
        UpdatePreview();
    }

    private void Quick15_Click(object sender, RoutedEventArgs e) => SetEvery(15, ScheduleUnit.Minutes);
    private void Quick1h_Click(object sender, RoutedEventArgs e) => SetEvery(1, ScheduleUnit.Hours);
    private void Quick24h_Click(object sender, RoutedEventArgs e) => SetEvery(24, ScheduleUnit.Hours);

    private void SetEvery(int value, ScheduleUnit unit)
    {
        EveryBox.Text = value.ToString();
        UnitBox.SelectedIndex = unit == ScheduleUnit.Hours ? 1 : 0;
    }

    private void Run30_Click(object sender, RoutedEventArgs e) => RunBox.Text = "30";
    private void Run120_Click(object sender, RoutedEventArgs e) => RunBox.Text = "120";
    private void Run300_Click(object sender, RoutedEventArgs e) => RunBox.Text = "300";

    private void AllDay_Click(object sender, RoutedEventArgs e)
    {
        FromBox.Text = "00:00";
        ToBox.Text = "00:00";
    }

    private void WorkHours_Click(object sender, RoutedEventArgs e)
    {
        FromBox.Text = "07:00";
        ToBox.Text = "17:00";
    }

    private void EveryDay_Click(object sender, RoutedEventArgs e) => SetDays(ScheduleDays.All);
    private void Weekdays_Click(object sender, RoutedEventArgs e) => SetDays(ScheduleDays.Weekdays);

    private void SetDays(ScheduleDays days)
    {
        MonBox.IsChecked = days.HasFlag(ScheduleDays.Monday);
        TueBox.IsChecked = days.HasFlag(ScheduleDays.Tuesday);
        WedBox.IsChecked = days.HasFlag(ScheduleDays.Wednesday);
        ThuBox.IsChecked = days.HasFlag(ScheduleDays.Thursday);
        FriBox.IsChecked = days.HasFlag(ScheduleDays.Friday);
        SatBox.IsChecked = days.HasFlag(ScheduleDays.Saturday);
        SunBox.IsChecked = days.HasFlag(ScheduleDays.Sunday);
    }

    private void CopyToAll_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        ReadForm(_current.Schedule);

        var answer = MessageBox.Show(
            $"Give all {_rows.Count} group(s) the same schedule as \"{_current.Group}\"?",
            "Group schedules", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        foreach (var row in _rows)
        {
            if (ReferenceEquals(row, _current)) continue;
            var copy = _current.Schedule.Clone();
            copy.Group = row.Group;
            row.Schedule = copy;
            row.Refresh();
        }

        UpdatePreview();
    }

    // ---------- Preview ----------

    private void UpdatePreview()
    {
        if (_current == null)
        {
            PreviewText.Text = string.Empty;
            PreviewNextText.Text = string.Empty;
            PreviewWarnText.Text = string.Empty;
            return;
        }

        var schedule = _current.Schedule;
        FieldsPanel.IsEnabled = schedule.Enabled;

        if (!schedule.Enabled)
        {
            PreviewText.Text = "The group is checked continuously - every device uses its own interval, " +
                               "exactly as if no schedule existed.";
            PreviewNextText.Text = string.Empty;
            PreviewWarnText.Text = string.Empty;
            return;
        }

        var error = schedule.Validate();
        if (error != null)
        {
            PreviewText.Text = error;
            PreviewNextText.Text = string.Empty;
            PreviewWarnText.Text = string.Empty;
            return;
        }

        var runsPerWindow = (int)((schedule.WindowLength.Ticks - 1) / schedule.Period.Ticks) + 1;
        var daysPerWeek = CountDays(schedule.Days);
        PreviewText.Text = $"{schedule.Summary}   →   {runsPerWindow} run(s) per day, " +
                           $"{runsPerWindow * daysPerWeek} per week.";

        var now = DateTime.Now;
        var state = GroupScheduler.Evaluate(schedule, now);
        PreviewNextText.Text = state.Open
            ? $"Running right now - this run ends at {state.RunEnds:HH:mm:ss}."
            : $"Paused right now - next run {GroupScheduler.Describe(state.NextRun ?? now, now)}.";

        PreviewWarnText.Text = schedule.GetWarning() ?? string.Empty;
    }

    private static int CountDays(ScheduleDays days)
    {
        var count = 0;
        foreach (ScheduleDays flag in new[]
                 {
                     ScheduleDays.Monday, ScheduleDays.Tuesday, ScheduleDays.Wednesday, ScheduleDays.Thursday,
                     ScheduleDays.Friday, ScheduleDays.Saturday, ScheduleDays.Sunday
                 })
            if (days.HasFlag(flag)) count++;
        return count;
    }

    // ---------- Save ----------

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_current != null) ReadForm(_current.Schedule);

        foreach (var row in _rows)
        {
            var error = row.Schedule.Validate();
            if (error == null) continue;

            GroupList.SelectedItem = row;
            ErrorText.Text = $"{row.Group}: {error}";
            return;
        }

        // A group that has never been scheduled is not written to devices.json at all, so the
        // file stays as small as it was before.
        Schedules = _rows
            .Where(r => r.Schedule.Enabled || r.ExistedBefore)
            .Select(r => r.Schedule)
            .Concat(_orphans)
            .ToList();

        DialogResult = true;
        Close();
    }
}
