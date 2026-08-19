using System.Globalization;
using System.Net;
using System.Windows;
using ST_Device_Monitoring.Core;
using ST_Device_Monitoring.Models;

namespace ST_Device_Monitoring.Views;

public partial class DhcpWindow : Window
{
    private DhcpServer? _server;

    /// <summary>Set when a device accepted an address and should be added to the list.</summary>
    public DeviceConfig? DeviceToAdd { get; private set; }

    /// <param name="adapterName">Adapter to preselect (from the discovery list).</param>
    /// <param name="deviceMac">MAC of the device to serve, if it is known.</param>
    /// <param name="deviceIp">Address the device currently uses, used to suggest a subnet.</param>
    public DhcpWindow(string? adapterName = null, string? deviceMac = null, string? deviceIp = null)
    {
        InitializeComponent();

        foreach (var subnet in NetworkDiscovery.GetLocalSubnets())
            AdapterBox.Items.Add(subnet);

        if (adapterName != null)
        {
            var match = AdapterBox.Items.OfType<LocalSubnet>()
                .FirstOrDefault(s => string.Equals(s.AdapterName, adapterName, StringComparison.OrdinalIgnoreCase));
            if (match != null) AdapterBox.SelectedItem = match;
        }
        if (AdapterBox.SelectedItem == null && AdapterBox.Items.Count > 0) AdapterBox.SelectedIndex = 0;

        MacBox.Text = deviceMac ?? string.Empty;

        // If the device already showed an address (for example 169.254.x after giving up on DHCP),
        // suggest a subnet around it - otherwise fall back to the adapter's own subnet.
        if (!string.IsNullOrWhiteSpace(deviceIp) && IPAddress.TryParse(deviceIp, out var current))
        {
            var (address, mask) = NetworkAdapterConfig.SuggestAddressFor(current);
            ServerIpBox.Text = address.ToString();
            MaskBox.Text = mask.ToString();
            OfferIpBox.Text = SuggestOffer(address, mask).ToString();
        }

        Adapter_Changed(this, null!);
    }

    private void Adapter_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (AdapterBox.SelectedItem is not LocalSubnet subnet) return;

        if (string.IsNullOrWhiteSpace(ServerIpBox.Text))
        {
            ServerIpBox.Text = subnet.Address.ToString();
            MaskBox.Text = subnet.Mask.ToString();
        }

        if (string.IsNullOrWhiteSpace(OfferIpBox.Text) &&
            IPAddress.TryParse(ServerIpBox.Text, out var server) &&
            IPAddress.TryParse(MaskBox.Text, out var mask))
        {
            OfferIpBox.Text = SuggestOffer(server, mask).ToString();
        }
    }

    /// <summary>Picks an address next to the server address inside the same subnet.</summary>
    private static IPAddress SuggestOffer(IPAddress serverAddress, IPAddress mask)
    {
        var bytes = serverAddress.GetAddressBytes();
        var last = bytes[3] >= 250 ? bytes[3] - 5 : bytes[3] + 5;
        if (last is <= 0 or >= 255) last = 100;
        return new IPAddress(new[] { bytes[0], bytes[1], bytes[2], (byte)last });
    }

    private void ApplyAdapterIp_Click(object sender, RoutedEventArgs e)
    {
        if (AdapterBox.SelectedItem is not LocalSubnet subnet)
        {
            MessageBox.Show("Choose a network adapter first.", "DHCP", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!IPAddress.TryParse(ServerIpBox.Text.Trim(), out var address) ||
            !IPAddress.TryParse(MaskBox.Text.Trim(), out var mask))
        {
            MessageBox.Show("Enter a valid address and subnet mask for this machine.", "DHCP",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var question = $"Set this address on \"{subnet.AdapterName}\"?\n\n" +
                       NetworkAdapterConfig.BuildStaticCommand(subnet.AdapterName, address, mask) +
                       "\n\nWindows will ask for administrator rights. The adapter can be put back on " +
                       "automatic later with the 'Restore DHCP' button in the discovery window.";
        if (MessageBox.Show(question, "Change adapter address", MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes) return;

        var (ok, message) = NetworkAdapterConfig.SetStaticAddress(subnet.AdapterName, address, mask);
        StatusText.Text = message;
        AddLog(message);

        if (!ok) return;

        // Refresh the adapter list so the new address is shown.
        var selected = subnet.AdapterName;
        AdapterBox.Items.Clear();
        foreach (var s in NetworkDiscovery.GetLocalSubnets()) AdapterBox.Items.Add(s);
        AdapterBox.SelectedItem = AdapterBox.Items.OfType<LocalSubnet>()
            .FirstOrDefault(s => string.Equals(s.AdapterName, selected, StringComparison.OrdinalIgnoreCase));
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (!IPAddress.TryParse(ServerIpBox.Text.Trim(), out var server))
        {
            MessageBox.Show("The address of this machine is not a valid IPv4 address.", "DHCP",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!IPAddress.TryParse(OfferIpBox.Text.Trim(), out var offer))
        {
            MessageBox.Show("The address for the device is not a valid IPv4 address.", "DHCP",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!IPAddress.TryParse(MaskBox.Text.Trim(), out var mask))
        {
            MessageBox.Show("The subnet mask is not valid.", "DHCP", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var settings = new DhcpServerSettings
        {
            ServerAddress = server,
            OfferedAddress = offer,
            SubnetMask = mask,
            Gateway = IPAddress.TryParse(GatewayBox.Text.Trim(), out var gateway) ? gateway : IPAddress.None,
            Dns = IPAddress.TryParse(DnsBox.Text.Trim(), out var dns) ? dns : IPAddress.None,
            LeaseMinutes = ParseInt(LeaseBox.Text, 60),
            AllowedMac = string.IsNullOrWhiteSpace(MacBox.Text) ? null : MacBox.Text.Trim().Replace(':', '-')
        };

        if (settings.AllowedMac == null &&
            MessageBox.Show(
                "No MAC lock is set, so every device on this adapter can get the address.\n\n" +
                "Only do this on a cable that goes directly to the device. Start anyway?",
                "DHCP", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        try
        {
            _server = new DhcpServer(settings);
            _server.Log += OnLog;
            _server.LeaseGranted += OnLeaseGranted;
            _server.Start();

            StartButton.IsEnabled = false;
            StopButton.IsEnabled = true;
            StatusText.Text = "Waiting for the device…";
        }
        catch (Exception ex)
        {
            _server = null;
            MessageBox.Show(
                "The DHCP server could not start:\n" + ex.Message +
                "\n\nUDP port 67 may be in use (Internet Connection Sharing, another DHCP tool) or " +
                "blocked by the Windows firewall.",
                "DHCP", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => StopServer();

    private void StopServer()
    {
        if (_server == null) return;
        _server.Log -= OnLog;
        _server.LeaseGranted -= OnLeaseGranted;
        _server.Dispose();
        _server = null;

        StartButton.IsEnabled = true;
        StopButton.IsEnabled = false;
        StatusText.Text = "Stopped.";
    }

    private void OnLog(string line) => Dispatcher.BeginInvoke(() => AddLog(line));

    private void AddLog(string line)
    {
        LogList.Items.Add(line);
        LogList.ScrollIntoView(LogList.Items[^1]);
    }

    private void OnLeaseGranted(DhcpLease lease)
    {
        Dispatcher.BeginInvoke(() =>
        {
            StatusText.Text = $"{lease.Ip} given to {lease.Mac}.";

            if (AddDeviceBox.IsChecked != true || DeviceToAdd != null) return;

            DeviceToAdd = new DeviceConfig
            {
                Name = string.IsNullOrWhiteSpace(lease.HostName) ? lease.Ip : lease.HostName,
                Host = lease.Ip,
                MacAddress = lease.Mac,
                Description = "Address assigned by the built-in DHCP server",
                Mode = CheckMode.Icmp,
                IntervalMs = 1000,
                DownIntervalMs = 1000,
                TimeoutMs = 1000,
                FailThreshold = 3,
                MaxLoggedFailures = 5,
                Enabled = true
            };

            AddLog($"The device will be added to the monitoring list as \"{DeviceToAdd.Name}\" when this window is closed.");
        });
    }

    private static int ParseInt(string text, int fallback)
        => int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    protected override void OnClosed(EventArgs e)
    {
        StopServer();
        base.OnClosed(e);
    }
}
