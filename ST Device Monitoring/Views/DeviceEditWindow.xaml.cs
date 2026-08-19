using System.Globalization;
using System.Windows;
using ST_Device_Monitoring.Core;
using ST_Device_Monitoring.Models;

namespace ST_Device_Monitoring.Views;

public partial class DeviceEditWindow : Window
{
    private readonly IReadOnlyCollection<DeviceConfig> _others;

    public DeviceConfig Device { get; }

    /// <param name="others">All other devices - used to warn about duplicate endpoints.</param>
    /// <param name="groups">Existing group names for the drop-down.</param>
    public DeviceEditWindow(DeviceConfig device, bool isNew,
        IReadOnlyCollection<DeviceConfig> others, IEnumerable<string> groups)
    {
        InitializeComponent();
        Device = device;
        _others = others;
        Title = isNew ? "Add device" : "Edit device";

        foreach (var group in groups.Where(g => !string.IsNullOrWhiteSpace(g)).Distinct().OrderBy(g => g))
            GroupBox.Items.Add(group);

        NameBox.Text = device.Name;
        HostBox.Text = device.Host;
        GroupBox.Text = device.Group;
        GroupMasterBox.IsChecked = device.IsGroupMaster;
        IcmpBox.IsChecked = device.Mode == CheckMode.Icmp;
        TcpBox.IsChecked = device.Mode == CheckMode.TcpPort;
        SnmpBox.IsChecked = device.Mode == CheckMode.Snmp;
        PortBox.Text = device.Port.ToString(CultureInfo.InvariantCulture);
        CommunityBox.Text = string.IsNullOrWhiteSpace(device.Community) ? "public" : device.Community;
        DescriptionBox.Text = device.Description;
        IntervalBox.Text = device.IntervalMs.ToString(CultureInfo.InvariantCulture);
        DownIntervalBox.Text = device.DownIntervalMs.ToString(CultureInfo.InvariantCulture);
        TimeoutBox.Text = device.TimeoutMs.ToString(CultureInfo.InvariantCulture);
        ThresholdBox.Text = device.FailThreshold.ToString(CultureInfo.InvariantCulture);
        MaxLoggedBox.Text = device.MaxLoggedFailures.ToString(CultureInfo.InvariantCulture);
        EnabledBox.IsChecked = device.Enabled;

        UpdateModeUi();
        Loaded += (_, _) => NameBox.Focus();
        ValidateHost();
    }

    private CheckMode SelectedMode => SnmpBox.IsChecked == true ? CheckMode.Snmp
        : TcpBox.IsChecked == true ? CheckMode.TcpPort
        : CheckMode.Icmp;

    private void Mode_Changed(object sender, RoutedEventArgs e) => UpdateModeUi();

    private void UpdateModeUi()
    {
        if (PortBox == null || ModeHint == null) return;

        var mode = SelectedMode;
        PortBox.IsEnabled = mode != CheckMode.Icmp;
        CommunityBox.IsEnabled = mode == CheckMode.Snmp;
        CommunityLabel.Opacity = mode == CheckMode.Snmp ? 1.0 : 0.5;

        switch (mode)
        {
            case CheckMode.TcpPort:
                if (PortBox.Text.Trim() is "161" or "") PortBox.Text = "502";
                ModeHint.Text = "Opens a TCP connection to the port - use it for devices that block ICMP " +
                                "(502 Modbus, 102 S7, 80 HTTP, 443 HTTPS).";
                break;
            case CheckMode.Snmp:
                if (PortBox.Text.Trim() is "502" or "") PortBox.Text = "161";
                ModeHint.Text = "SNMP v2c GET of sysUpTime. The device description (sysDescr) is read " +
                                "automatically the first time the check succeeds.";
                break;
            default:
                ModeHint.Text = "Classic ICMP echo (ping). Needs no port.";
                break;
        }
    }

    /// <summary>
    /// Live check of the address field: anything typed as digits and dots is validated as a
    /// strict IPv4 address, so 192.168.1.300 or 192.168.1 is reported while typing.
    /// </summary>
    private void Host_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => ValidateHost();

    private bool ValidateHost()
    {
        if (HostHint == null || HostBox == null) return true;

        var text = HostBox.Text.Trim();
        if (text.Length == 0)
        {
            HostHint.Text = string.Empty;
            HostBox.ClearValue(BorderBrushProperty);
            return false;
        }

        var error = NetworkValidation.ValidateHost(text);
        if (error != null)
        {
            HostHint.Foreground = System.Windows.Media.Brushes.Firebrick;
            HostHint.Text = error;
            HostBox.BorderBrush = System.Windows.Media.Brushes.Firebrick;
            return false;
        }

        HostHint.Foreground = System.Windows.Media.Brushes.Gray;
        HostHint.Text = NetworkValidation.LooksLikeIPv4(text) ? "Valid IPv4 address." : "Hostname - resolved via DNS.";
        HostBox.ClearValue(BorderBrushProperty);
        return true;
    }

    /// <summary>Reads sysDescr/sysName from the device and fills in description (and name if empty).</summary>
    private async void ReadSnmp_Click(object sender, RoutedEventArgs e)
    {
        var host = HostBox.Text.Trim();
        var hostError = NetworkValidation.ValidateHost(host);
        if (hostError != null)
        {
            SnmpStatusText.Foreground = System.Windows.Media.Brushes.Firebrick;
            SnmpStatusText.Text = hostError;
            return;
        }

        var port = int.TryParse(PortBox.Text.Trim(), out var p) && SelectedMode == CheckMode.Snmp
            ? p
            : SnmpClient.DefaultPort;
        var community = string.IsNullOrWhiteSpace(CommunityBox.Text) ? "public" : CommunityBox.Text.Trim();
        var timeout = int.TryParse(TimeoutBox.Text.Trim(), out var t) ? Math.Max(t, 2000) : 2000;

        ReadSnmpButton.IsEnabled = false;
        SnmpStatusText.Foreground = System.Windows.Media.Brushes.Gray;
        SnmpStatusText.Text = "Reading…";

        var (ok, description, sysName, error) = await SnmpClient
            .ReadSystemInfoAsync(host, port, community, timeout);

        ReadSnmpButton.IsEnabled = true;

        if (!ok || string.IsNullOrWhiteSpace(description))
        {
            SnmpStatusText.Foreground = System.Windows.Media.Brushes.Firebrick;
            SnmpStatusText.Text = error ?? "No SNMP answer - check the community and that SNMP is enabled.";
            return;
        }

        DescriptionBox.Text = description;
        if (string.IsNullOrWhiteSpace(NameBox.Text) && !string.IsNullOrWhiteSpace(sysName))
            NameBox.Text = sysName;

        SnmpStatusText.Foreground = System.Windows.Media.Brushes.Green;
        SnmpStatusText.Text = "Read from the device.";
    }

    private void Interval100_Click(object sender, RoutedEventArgs e) => IntervalBox.Text = "100";
    private void Interval500_Click(object sender, RoutedEventArgs e) => IntervalBox.Text = "500";
    private void Interval1000_Click(object sender, RoutedEventArgs e) => IntervalBox.Text = "1000";

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParse(IntervalBox.Text, out var interval, "Interval")) return;
        if (!TryParse(TimeoutBox.Text, out var timeout, "Timeout")) return;
        if (!TryParse(DownIntervalBox.Text, out var downInterval, "Interval while down")) return;
        if (!TryParse(ThresholdBox.Text, out var threshold, "Failures before alarm")) return;
        if (!TryParse(MaxLoggedBox.Text, out var maxLogged, "Stop logging after")) return;
        if (!TryParse(PortBox.Text, out var port, "Port")) return;

        Device.Name = NameBox.Text.Trim();
        Device.Host = HostBox.Text.Trim();
        Device.Group = GroupBox.Text.Trim();
        Device.IsGroupMaster = GroupMasterBox.IsChecked == true;
        Device.Mode = SelectedMode;
        Device.Port = port;
        Device.Community = CommunityBox.Text.Trim();
        Device.Description = DescriptionBox.Text.Trim();
        Device.IntervalMs = interval;
        Device.DownIntervalMs = downInterval;
        Device.TimeoutMs = timeout;
        Device.FailThreshold = threshold;
        Device.MaxLoggedFailures = maxLogged;
        Device.Enabled = EnabledBox.IsChecked == true;

        var error = Device.Validate();
        if (error != null)
        {
            ErrorText.Text = error;
            return;
        }

        // Duplicate check: same host (and port/mode) as another device.
        var duplicate = _others.FirstOrDefault(d =>
            d.Id != Device.Id &&
            string.Equals(d.Endpoint, Device.Endpoint, StringComparison.OrdinalIgnoreCase));
        if (duplicate != null)
        {
            ErrorText.Text = $"\"{duplicate.Name}\" already monitors {Device.Host} with the same check. " +
                             "Change the address, the port or the check type.";
            return;
        }

        // Only one master per group.
        if (Device.IsGroupMaster)
        {
            var existingMaster = _others.FirstOrDefault(d =>
                d.Id != Device.Id && d.IsGroupMaster &&
                string.Equals(d.Group?.Trim(), Device.Group, StringComparison.OrdinalIgnoreCase));
            if (existingMaster != null)
            {
                ErrorText.Text = $"\"{existingMaster.Name}\" is already the master of group " +
                                 $"\"{Device.Group}\". A group can only have one master.";
                return;
            }
        }

        // Non-blocking warning, e.g. timeout longer than the interval.
        var warning = Device.GetWarning();
        if (warning != null)
        {
            var answer = MessageBox.Show(warning + "\n\nSave anyway?", "Check the settings",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;
        }

        DialogResult = true;
    }

    private bool TryParse(string text, out int value, string fieldName)
    {
        if (int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            return true;

        ErrorText.Text = $"{fieldName} must be a whole number.";
        return false;
    }
}
