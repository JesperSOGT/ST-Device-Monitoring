using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using Microsoft.Win32;
using ST_Device_Monitoring.Controls;
using ST_Device_Monitoring.Core;
using ST_Device_Monitoring.Models;
using ST_Device_Monitoring.ViewModels;
using ST_Device_Monitoring.Views;

namespace ST_Device_Monitoring;

public partial class MainWindow : Window
{
    private const string AllGroups = "(all groups)";

    private readonly MonitorService _service;
    private readonly ObservableCollection<DeviceViewModel> _devices = new();
    private readonly ICollectionView _view;
    private readonly DispatcherTimer _timer;
    private TrayNotifier? _tray;
    private UpdateInfo? _pendingUpdate;
    private bool _initialized;
    private bool _exitRequested;

    public MainWindow()
    {
        InitializeComponent();

        AppConfig config;
        try
        {
            config = ConfigStore.Load();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message + "\n\nStarting with an empty device list.", "ST Device Monitoring",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            config = new AppConfig();
        }

        _service = new MonitorService(config);
        _service.Alert += OnDeviceAlert;
        _service.DescriptionDiscovered += OnDescriptionDiscovered;

        foreach (var monitor in _service.Monitors.OrderBy(m => m.Config.Group).ThenBy(m => m.Config.Name))
            _devices.Add(new DeviceViewModel(monitor));

        _view = CollectionViewSource.GetDefaultView(_devices);
        _view.Filter = FilterDevice;
        DeviceGrid.ItemsSource = _view;

        LogAllBox.IsChecked = config.LogAllPings;
        LogText.Text = "Log folder: " + _service.Logger.Directory;
        RefreshGroupList();

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(config.UiRefreshMs)
        };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();

        SetupTray();

        if (config.AutoStart)
            _service.StartAll();

        if (_devices.Count > 0)
            DeviceGrid.SelectedIndex = 0;

        _initialized = true;
        Refresh();

        if (config.Ui.StartMinimized && config.Ui.ShowTrayIcon)
            Loaded += (_, _) => HideToTray();

        WarnIfServiceRunning();

        _ = CheckForUpdatesAtStartupAsync();
    }

    // ---------- Updates ----------

    /// <summary>
    /// Asks GitHub once, a few seconds after start, whether a newer release exists. It never
    /// installs anything and never interrupts the monitoring - it only lights up the note in the
    /// status bar. Any failure is ignored on purpose: a machine without internet must not be
    /// nagged every time the program opens.
    /// </summary>
    private async Task CheckForUpdatesAtStartupAsync()
    {
        var settings = _service.Config.Updates;
        if (!settings.CheckOnStartup) return;

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5));

            var (info, _) = await UpdateChecker.CheckAsync(
                settings.RepositoryOwner, settings.RepositoryName, settings.IncludePreReleases);

            settings.LastChecked = DateTime.Now;
            if (info == null || !info.IsNewer) return;
            if (string.Equals(info.Tag, settings.SkipVersion, StringComparison.OrdinalIgnoreCase)) return;

            _pendingUpdate = info;
            ShowUpdateBanner(info);

            if (_service.Config.Alerts.BalloonEnabled)
                _tray?.ShowBalloon("Update available",
                    $"{AppInfo.ProductName} {info.Tag} has been published. Open the program to install it.", false);
        }
        catch
        {
            // No internet, DNS blocked, proxy - not worth telling the user about at startup.
        }
    }

    private void ShowUpdateBanner(UpdateInfo info)
    {
        UpdateBanner.Content = $"⬆  Update available: {info.Tag}";
        UpdateBanner.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Opens the update window. Nothing is downloaded or replaced until the user confirms there;
    /// when the swap starts, this window has to close so the exe is released.
    /// </summary>
    private void Update_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new UpdateWindow(_service.Config, _pendingUpdate) { Owner = this };
        dialog.ShowDialog();

        if (dialog.SettingsChanged) Save();

        if (dialog.RestartRequested)
        {
            // The update script is waiting for this program to release its own exe file.
            _exitRequested = true;
            Close();
            return;
        }

        var settings = _service.Config.Updates;
        if (_pendingUpdate != null &&
            string.Equals(_pendingUpdate.Tag, settings.SkipVersion, StringComparison.OrdinalIgnoreCase))
        {
            _pendingUpdate = null;
            UpdateBanner.Visibility = Visibility.Collapsed;
        }
    }

    // ---------- Tray ----------

    private void SetupTray()
    {
        if (!_service.Config.Ui.ShowTrayIcon)
        {
            _tray?.Dispose();
            _tray = null;
            return;
        }

        if (_tray != null)
        {
            _tray.Visible = true;
            return;
        }

        _tray = new TrayNotifier { Visible = true };
        _tray.ShowRequested += RestoreFromTray;
        _tray.StartRequested += () => { _service.StartAll(); Refresh(); };
        _tray.StopRequested += async () => { await _service.StopAllAsync(); Refresh(); };
        _tray.ExitRequested += () => { _exitRequested = true; Close(); };
    }

    private void HideToTray()
    {
        if (_tray == null) return;
        Hide();
        _tray.ShowBalloon(AppInfo.ProductName, "Monitoring keeps running in the background.", false);
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    protected override void OnStateChanged(EventArgs e)
    {
        if (WindowState == WindowState.Minimized && _service.Config.Ui.MinimizeToTray && _tray != null)
            Hide();
        base.OnStateChanged(e);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exitRequested && _service.Config.Ui.CloseToTray && _tray != null)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }
        base.OnClosing(e);
    }

    /// <summary>Alerts arrive from a check thread - marshal them onto the UI thread.</summary>
    private void OnDeviceAlert(DeviceAlert alert)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var settings = _service.Config.Alerts;
            if (alert.IsDown)
            {
                TrayNotifier.PlayAlertSound(settings);
                if (settings.BalloonEnabled)
                    _tray?.ShowBalloon($"DOWN: {alert.Device.Name}",
                        $"{alert.Device.Host} ({alert.Device.ModeText}) - {alert.Message}", true);
            }
            else if (settings.NotifyOnRecovery && settings.BalloonEnabled)
            {
                _tray?.ShowBalloon($"UP: {alert.Device.Name}",
                    $"{alert.Device.Host} - {alert.Message}", false);
            }
        });
    }

    /// <summary>An SNMP description was read on a check thread - show and save it.</summary>
    private void OnDescriptionDiscovered(DeviceMonitor monitor)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var vm = _devices.FirstOrDefault(d => d.Id == monitor.Config.Id);
            vm?.ConfigChanged();
            Save();
        });
    }

    private void WarnIfServiceRunning()
    {
        if (WindowsServiceInstaller.Query() != ServiceState.Running) return;

        MessageBox.Show(
            "The ST Device Monitoring service is running and is already monitoring in the background.\n\n" +
            "If you also start monitoring in this window, both will write to the same log folder. " +
            "Stop the service under Settings -> Run mode if you only want one of them.",
            "ST Device Monitoring", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ---------- Filtering ----------

    private bool FilterDevice(object item)
    {
        if (item is not DeviceViewModel vm) return false;

        if (OnlyFailuresBox.IsChecked == true &&
            vm.State is not (DeviceState.Error or DeviceState.Warning))
            return false;

        if (GroupBox.SelectedItem is string group && group != AllGroups &&
            !string.Equals(vm.Group, group, StringComparison.OrdinalIgnoreCase))
            return false;

        var search = SearchBox.Text.Trim();
        if (search.Length == 0) return true;

        return vm.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
               || vm.Host.Contains(search, StringComparison.OrdinalIgnoreCase)
               || vm.Group.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        _view.Refresh();
        UpdateFilterCount();
    }

    private void Search_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => Filter_Changed(sender, e);

    private void Group_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        => Filter_Changed(sender, e);

    private void ClearFilter_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = string.Empty;
        OnlyFailuresBox.IsChecked = false;
        GroupBox.SelectedIndex = 0;
        _view.Refresh();
        UpdateFilterCount();
    }

    private void RefreshGroupList()
    {
        var previous = GroupBox.SelectedItem as string;
        GroupBox.Items.Clear();
        GroupBox.Items.Add(AllGroups);
        foreach (var group in _devices.Select(d => d.Group)
                     .Where(g => !string.IsNullOrWhiteSpace(g))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => g))
            GroupBox.Items.Add(group);

        GroupBox.SelectedItem = previous != null && GroupBox.Items.Contains(previous) ? previous : AllGroups;
    }

    private void UpdateFilterCount()
    {
        var shown = _view.Cast<object>().Count();
        FilterCountText.Text = shown == _devices.Count
            ? $"{_devices.Count} devices"
            : $"{shown} of {_devices.Count} devices";
    }

    // ---------- UI refresh ----------

    private void Refresh()
    {
        long totalSent = 0, totalFails = 0, totalOutages = 0;
        int ok = 0, error = 0, suppressed = 0, blocked = 0;

        foreach (var vm in _devices)
        {
            vm.Refresh();
            totalSent += vm.Sent;
            totalFails += vm.Failed;
            totalOutages += vm.OutageCount;
            if (vm.State == DeviceState.Ok) ok++;
            else if (vm.State == DeviceState.Error) error++;
            else if (vm.State == DeviceState.Blocked) blocked++;
            if (vm.LoggingSuppressed) suppressed++;
        }

        TileTotal.Text = _devices.Count.ToString("N0");
        TileOk.Text = ok.ToString("N0");
        TileError.Text = error.ToString("N0");
        TileFails.Text = totalFails.ToString("N0");
        TileSent.Text = totalSent.ToString("N0");
        TileOutages.Text = totalOutages.ToString("N0");
        TileSuppressed.Text = suppressed.ToString("N0");
        TileBlocked.Text = blocked.ToString("N0");

        // The "only failures" view has to follow the live state.
        if (OnlyFailuresBox.IsChecked == true) _view.Refresh();
        UpdateFilterCount();

        var monitoring = _service.IsMonitoring || _service.AnyRunning;
        RunButtonText.Text = monitoring ? "■  Stop" : "▶  Start";
        RunButtonText.Foreground = monitoring
            ? System.Windows.Media.Brushes.Firebrick
            : System.Windows.Media.Brushes.DarkGreen;
        RunButton.ToolTip = monitoring
            ? "Monitoring is running - press to stop"
            : "Monitoring is stopped - press to start";

        SummaryText.Text = monitoring
            ? $"Monitoring · {_devices.Count(d => d.IsRunning)} device(s) active"
            : "Stopped";

        var dropped = _service.Logger.DroppedRecords;
        if (dropped > 0)
            SummaryText.Text += $" · {dropped:N0} log entries dropped (disk cannot keep up)";
        if (_service.Logger.LastWriteError is { } logErr)
            SummaryText.Text += $" · Log error: {logErr}";
        if (_service.Alerts.LastError is { } alertErr)
            SummaryText.Text += $" · Alert error: {alertErr}";

        ClockText.Text = $"{AppInfo.VersionLine}  ·  {DateTime.Now:dd-MM-yyyy HH:mm:ss}";

        _tray?.UpdateStatus(monitoring, _devices.Count, error);

        UpdateDetail();
    }

    private void UpdateDetail()
    {
        if (DeviceGrid.SelectedItem is not DeviceViewModel vm)
        {
            DetailHeader.Text = "Select a device to see its history";
            DetailSub.Text = "";
            DetailError.Text = "";
            History.Samples = null;
            return;
        }

        DetailHeader.Text = $"{vm.Header}  ·  {vm.ModeText}" +
                            (string.IsNullOrWhiteSpace(vm.Group) ? "" : $"  ·  {vm.Group}") +
                            (string.IsNullOrWhiteSpace(vm.Description) ? "" : $"  ·  {vm.Description}");
        DetailSub.Text = $"Interval {vm.IntervalText} · timeout {vm.TimeoutMs} ms · " +
                         $"{vm.Sent:N0} checks · {vm.Failed:N0} failures ({vm.FailPercentText}) · " +
                         $"last 60 s: {vm.Loss60Text} loss, {vm.Jitter60Text} jitter · " +
                         $"avg {vm.AvgRttText} · min/max {vm.MinMaxRttText} · outages: {vm.OutageCount:N0} · " +
                         $"logging: {vm.LoggingText}" +
                         (vm.Monitor.Schedule == null ? "" : $" · schedule: {vm.ScheduleText}");
        DetailError.Text = string.IsNullOrEmpty(vm.LastError) ? "" : "Last error: " + vm.LastError;
        History.SlowThreshold = Math.Max(20, vm.TimeoutMs / 2);
        History.Samples = vm.History;
    }

    // ---------- Buttons ----------

    /// <summary>
    /// One button for both: the program starts stopped, the first press starts monitoring and
    /// the next press stops it again.
    /// </summary>
    private async void RunToggle_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_service.IsMonitoring || _service.AnyRunning)
                await _service.StopAllAsync();
            else
                _service.StartAll();
        }
        catch (Exception ex) { ShowError(ex); }

        Refresh();
    }

    private void Help_Click(object sender, RoutedEventArgs e)
        => new HelpWindow { Owner = this }.ShowDialog();

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.F1) return;
        e.Handled = true;
        new HelpWindow { Owner = this }.ShowDialog();
    }

    /// <summary>
    /// Adds a device. Devices that are already being checked are never stopped or restarted -
    /// only the new device gets its own check loop.
    /// </summary>
    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var device = new DeviceConfig { IntervalMs = 1000, DownIntervalMs = 1000, TimeoutMs = 1000, Enabled = true };
        var dialog = new DeviceEditWindow(device, true, CurrentConfigs(), CurrentGroups()) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        // MonitorService.Add starts the new device by itself when monitoring is active.
        var monitor = _service.Add(device);
        var vm = new DeviceViewModel(monitor);
        _devices.Add(vm);
        Save();
        _service.RebuildGroupGates();
        RefreshGroupList();

        DeviceGrid.SelectedItem = vm;
        Refresh();
    }

    private async void Edit_Click(object sender, RoutedEventArgs e) => await EditSelectedAsync();

    private async void Grid_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => await EditSelectedAsync();

    /// <summary>Only the edited device is restarted; all other check loops keep running.</summary>
    private async Task EditSelectedAsync()
    {
        if (DeviceGrid.SelectedItem is not DeviceViewModel vm) return;

        var copy = vm.Config.Clone();
        var dialog = new DeviceEditWindow(copy, false, CurrentConfigs(), CurrentGroups()) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        var index = _service.Config.Devices.FindIndex(d => d.Id == copy.Id);
        if (index >= 0) _service.Config.Devices[index] = copy;

        try { await vm.Monitor.UpdateConfigAsync(copy); }
        catch (Exception ex) { ShowError(ex); }

        vm.ConfigChanged();
        Save();
        _service.RebuildGroupGates();
        RefreshGroupList();
        Refresh();
    }

    private async void Delete_Click(object sender, RoutedEventArgs e) => await DeleteSelectedAsync();

    /// <summary>Del deletes the selected row(s) straight from the list.</summary>
    private async void Grid_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Delete) return;
        e.Handled = true;
        await DeleteSelectedAsync();
    }

    /// <summary>
    /// Deletes every selected device. Only the deleted devices are stopped - the other
    /// check loops keep running.
    /// </summary>
    private async Task DeleteSelectedAsync()
    {
        var selected = DeviceGrid.SelectedItems.OfType<DeviceViewModel>().ToList();
        if (selected.Count == 0) return;

        var question = selected.Count == 1
            ? $"Delete \"{selected[0].Name}\" ({selected[0].Host})?"
            : $"Delete {selected.Count} devices?\n\n" +
              string.Join("\n", selected.Take(10).Select(d => $"· {d.Name} ({d.Host})")) +
              (selected.Count > 10 ? $"\n… and {selected.Count - 10} more" : string.Empty);

        var answer = MessageBox.Show(question, "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        foreach (var vm in selected)
        {
            try { await _service.RemoveAsync(vm.Id); }
            catch (Exception ex) { ShowError(ex); }
            _devices.Remove(vm);
        }

        Save();
        _service.RebuildGroupGates();
        RefreshGroupList();
        Refresh();
    }

    /// <summary>Enables/disables every selected device.</summary>
    private async void ToggleEnabled_Click(object sender, RoutedEventArgs e)
    {
        var selected = DeviceGrid.SelectedItems.OfType<DeviceViewModel>().ToList();
        if (selected.Count == 0) return;

        foreach (var vm in selected)
        {
            var copy = vm.Config.Clone();
            copy.Enabled = !copy.Enabled;

            var index = _service.Config.Devices.FindIndex(d => d.Id == copy.Id);
            if (index >= 0) _service.Config.Devices[index] = copy;

            try
            {
                await vm.Monitor.UpdateConfigAsync(copy);
                if (copy.Enabled && _service.IsMonitoring) vm.Monitor.Start();
            }
            catch (Exception ex) { ShowError(ex); }

            vm.ConfigChanged();
        }

        Save();
        Refresh();
    }

    private void ScanRange_Click(object sender, RoutedEventArgs e)
    {
        var (from, to) = SuggestRange();
        var dialog = new RangeScanWindow(from, to) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        var existing = new HashSet<string>(_service.Config.Devices.Select(d => d.Endpoint),
            StringComparer.OrdinalIgnoreCase);
        var added = 0;
        var skipped = 0;

        foreach (var device in dialog.SelectedDevices)
        {
            if (!existing.Add(device.Endpoint)) { skipped++; continue; }
            var monitor = _service.Add(device);
            _devices.Add(new DeviceViewModel(monitor));
            added++;
        }

        Save();
        _service.RebuildGroupGates();
        RefreshGroupList();
        Refresh();

        MessageBox.Show($"{added} device(s) added." + (skipped > 0 ? $" {skipped} skipped (already in the list)." : ""),
            "Scan IP range", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>Suggests a range based on the first device, otherwise 192.168.1.x.</summary>
    private (string from, string to) SuggestRange()
    {
        var host = _devices.FirstOrDefault()?.Host;
        if (!string.IsNullOrWhiteSpace(host) && System.Net.IPAddress.TryParse(host, out var address))
        {
            var bytes = address.GetAddressBytes();
            if (bytes.Length == 4)
                return ($"{bytes[0]}.{bytes[1]}.{bytes[2]}.1", $"{bytes[0]}.{bytes[1]}.{bytes[2]}.254");
        }
        return ("192.168.1.1", "192.168.1.254");
    }

    /// <summary>
    /// Finds devices without knowing their address - including devices on another subnet, which
    /// cannot be pinged from this machine.
    /// </summary>
    private void Discover_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new DiscoverWindow { Owner = this };
        if (dialog.ShowDialog() != true) return;

        var existing = new HashSet<string>(_service.Config.Devices.Select(d => d.Endpoint),
            StringComparer.OrdinalIgnoreCase);
        var added = 0;
        var skipped = 0;

        foreach (var device in dialog.SelectedDevices)
        {
            if (!existing.Add(device.Endpoint)) { skipped++; continue; }
            var monitor = _service.Add(device);
            _devices.Add(new DeviceViewModel(monitor));
            added++;
        }

        Save();
        _service.RebuildGroupGates();
        RefreshGroupList();
        Refresh();

        MessageBox.Show(
            $"{added} device(s) added." +
            (skipped > 0 ? $" {skipped} skipped (already in the list)." : "") +
            "\n\nDevices found on another subnet are added disabled - they cannot be checked until " +
            "this machine has an address in that subnet.",
            "Discover devices", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import devices",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var result = DeviceImportExport.Import(dialog.FileName, _service.Config.Devices);
            foreach (var device in result.Devices)
            {
                var monitor = _service.Add(device);
                _devices.Add(new DeviceViewModel(monitor));
            }

            Save();
            _service.RebuildGroupGates();
            RefreshGroupList();
            Refresh();

            var message = $"{result.Devices.Count} device(s) imported.";
            if (result.SkippedDuplicates > 0) message += $"\n{result.SkippedDuplicates} skipped (already in the list).";
            if (result.Errors.Count > 0)
                message += "\n\nProblems:\n" + string.Join("\n", result.Errors.Take(10));

            MessageBox.Show(message, "Import", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export devices",
            Filter = "CSV files (*.csv)|*.csv",
            FileName = "devices.csv"
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            DeviceImportExport.Export(dialog.FileName, _service.Config.Devices);
            MessageBox.Show($"{_service.Config.Devices.Count} device(s) exported to\n{dialog.FileName}",
                "Export", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void WriteReport_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = _service.WriteSummaryNow();
            var answer = MessageBox.Show($"Report written to\n{path}\n\nOpen it now?", "Daily report",
                MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (answer == MessageBoxResult.Yes) OpenPath(path);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_service.Config, _service.Alerts) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        if (dialog.RestartForUpdate)
        {
            // An update was started from the settings window - release the exe now.
            Save();
            _exitRequested = true;
            Close();
            return;
        }

        _service.Logger.LogAllPings = _service.Config.LogAllPings;
        _service.Logger.Rotation = _service.Config.LogRotation;
        _service.Logger.MaxFileSizeBytes = Math.Max(0, (long)_service.Config.MaxLogFileSizeMB) * 1024 * 1024;
        _service.Logger.RingTrimPercent = _service.Config.RingTrimPercent;
        LogAllBox.IsChecked = _service.Config.LogAllPings;
        _timer.Interval = TimeSpan.FromMilliseconds(_service.Config.UiRefreshMs);
        SetupTray();
        Save();

        if (dialog.RestartRecommended)
            MessageBox.Show("The log folder has been changed. Restart the application for it to take effect.",
                "ST Device Monitoring", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>
    /// Group schedules. Applying them never stops or restarts a check loop - the schedule only
    /// opens and closes the gate in front of the devices in the group.
    /// </summary>
    private void Schedules_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ScheduleWindow(_service.Groups, _service.Config.Schedules, _service.CountInGroup)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true) return;

        _service.Config.Schedules = dialog.Schedules;
        _service.ApplySchedules();
        Save();

        foreach (var vm in _devices) vm.ConfigChanged();
        Refresh();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _service.ResetAll();
        foreach (var vm in _devices) vm.ConfigChanged();
        Refresh();
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
        => OpenPath(_service.Logger.Directory);

    private void OpenErrorLog_Click(object sender, RoutedEventArgs e)
    {
        var path = _service.Logger.CurrentErrorLogPath;
        if (!File.Exists(path))
        {
            MessageBox.Show("There is no error log for today (no failures recorded yet).",
                "ST Device Monitoring", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        OpenPath(path);
    }

    private static void OpenPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not open " + path + "\n" + ex.Message, "ST Device Monitoring",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void LogAll_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        var value = LogAllBox.IsChecked == true;
        _service.Config.LogAllPings = value;
        _service.Logger.LogAllPings = value;
        Save();
    }

    private void About_Click(object sender, RoutedEventArgs e)
        => new AboutWindow { Owner = this }.ShowDialog();

    private void Grid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        => UpdateDetail();

    private IReadOnlyCollection<DeviceConfig> CurrentConfigs() => _service.Config.Devices.ToList();

    private IEnumerable<string> CurrentGroups() => _devices.Select(d => d.Group);

    private void Save()
    {
        try
        {
            _service.Save();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not save the configuration:\n" + ex.Message,
                "ST Device Monitoring", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static void ShowError(Exception ex)
        => MessageBox.Show(ex.Message, "ST Device Monitoring", MessageBoxButton.OK, MessageBoxImage.Warning);

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        _service.Alert -= OnDeviceAlert;
        _service.DescriptionDiscovered -= OnDescriptionDiscovered;
        _tray?.Dispose();
        try
        {
            Task.Run(async () => await _service.DisposeAsync()).GetAwaiter().GetResult();
        }
        catch { /* ignored during shutdown */ }
        base.OnClosed(e);
        Application.Current.Shutdown();
    }
}
