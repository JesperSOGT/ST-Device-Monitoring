using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Windows;
using ST_Device_Monitoring.Core;
using ST_Device_Monitoring.Models;

namespace ST_Device_Monitoring.Views;

/// <summary>One discovered host in the grid.</summary>
public sealed class DiscoverRow : INotifyPropertyChanged
{
    private bool _selected;
    private string _name = string.Empty;
    private string _mac = string.Empty;
    private string _info = string.Empty;
    private string _protocolText = string.Empty;
    private string _adapter = string.Empty;
    private string _vendor = string.Empty;

    public string Ip { get; init; } = string.Empty;
    public bool InLocalSubnet { get; set; }

    /// <summary>When the device first showed up during this run.</summary>
    public string FirstSeen { get; init; } = DateTime.Now.ToString("HH:mm:ss");

    public string SubnetText => InLocalSubnet ? "Local" : "Other subnet - cannot be pinged";

    public bool Selected
    {
        get => _selected;
        set { _selected = value; OnPropertyChanged(); }
    }

    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); }
    }

    public string Mac
    {
        get => _mac;
        set { _mac = value; OnPropertyChanged(); }
    }

    public string Info
    {
        get => _info;
        set { _info = value; OnPropertyChanged(); }
    }

    public string ProtocolText
    {
        get => _protocolText;
        set { _protocolText = value; OnPropertyChanged(); }
    }

    /// <summary>Manufacturer resolved from the MAC address (like Wireshark does).</summary>
    public string Vendor
    {
        get => _vendor;
        set { _vendor = value; OnPropertyChanged(); }
    }

    /// <summary>The network adapter on this machine that the device was seen on.</summary>
    public string Adapter
    {
        get => _adapter;
        set { _adapter = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public partial class DiscoverWindow : Window
{
    private readonly ObservableCollection<DiscoverRow> _rows = new();
    private readonly Dictionary<string, DiscoverRow> _byIp = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private NetworkDiscovery? _discovery;

    /// <summary>Devices the user chose to add.</summary>
    public List<DeviceConfig> SelectedDevices { get; } = new();

    public DiscoverWindow()
    {
        InitializeComponent();
        ResultGrid.ItemsSource = _rows;

        MacVendorLookup.EnsureLoaded();
        VendorSourceText.Text = MacVendorLookup.SourceFile == null
            ? "Manufacturer names: no OUI list found - put Wireshark's \"manuf\" file (or IEEE oui.txt) next to the program to fill the Vendor column."
            : $"Manufacturer names from {MacVendorLookup.SourceFile} ({MacVendorLookup.Count:N0} entries)";

        var subnets = NetworkDiscovery.GetLocalSubnets();
        AdapterText.Text = subnets.Count == 0
            ? "No active IPv4 adapter found."
            : string.Join("\n", subnets.Select(s => $"· {s.AdapterName}: {s.Address} / {s.Mask}"));
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        var seconds = Math.Clamp(ParseInt(DurationBox.Text, 30), 5, 600);

        _rows.Clear();
        _byIp.Clear();
        AddButton.IsEnabled = false;
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        HintText.Text = string.Empty;

        _discovery = new NetworkDiscovery();
        _discovery.HostUpdated += OnHostUpdated;
        _cts = new CancellationTokenSource();

        var progress = new Progress<int>(left => StatusText.Text =
            $"Listening… {left} s left · {_rows.Count} device(s) found");

        try
        {
            await _discovery.RunAsync(TimeSpan.FromSeconds(seconds), progress, _cts.Token);
            StatusText.Text = $"Done - {_rows.Count} device(s) found.";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = $"Stopped - {_rows.Count} device(s) found.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Discover devices", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            if (_discovery != null) _discovery.HostUpdated -= OnHostUpdated;
            _discovery?.Dispose();
            _discovery = null;
            _cts?.Dispose();
            _cts = null;

            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            AddButton.IsEnabled = _rows.Count > 0;
            UpdateHint();
        }
    }

    private void OnHostUpdated(DiscoveredHost host)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!_byIp.TryGetValue(host.Ip, out var row))
            {
                row = new DiscoverRow
                {
                    Ip = host.Ip,
                    InLocalSubnet = host.InLocalSubnet,
                    Name = host.Ip,
                    Selected = false,
                    FirstSeen = host.FirstSeen.ToString("HH:mm:ss")
                };
                _byIp[host.Ip] = row;
                _rows.Add(row);
            }

            row.Mac = host.Mac;
            row.Vendor = MacVendorLookup.Lookup(host.Mac);
            row.Adapter = host.Adapter;
            row.Info = host.Info;
            row.ProtocolText = host.ProtocolText;
        });
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    /// <summary>Fetches the IEEE manufacturer list so the Manufacturer column works without Wireshark.</summary>
    private async void DownloadOui_Click(object sender, RoutedEventArgs e)
    {
        DownloadOuiButton.IsEnabled = false;
        VendorSourceText.Text = "Downloading the manufacturer list from IEEE…";

        var (ok, message) = await MacVendorLookup.DownloadAsync();

        DownloadOuiButton.IsEnabled = true;
        VendorSourceText.Text = message;

        if (!ok) return;

        foreach (var row in _rows) row.Vendor = MacVendorLookup.Lookup(row.Mac);
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows) row.Selected = true;
    }

    private void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows) row.Selected = false;
    }

    /// <summary>
    /// A device on another subnet cannot be checked before this machine has an address in that
    /// subnet - the command to add a temporary one is offered here.
    /// </summary>
    private void UpdateHint()
    {
        var foreign = _rows.FirstOrDefault(r => !r.InLocalSubnet);
        if (foreign == null)
        {
            HintText.Text = string.Empty;
            return;
        }

        HintText.Text =
            $"Devices were found on another subnet (for example {foreign.Ip}). They cannot be checked until this " +
            "machine has an address in that subnet. Add a temporary one from an administrator command prompt:\n" +
            BuildNetshCommand(foreign.Ip) +
            "\n(remove it again with: netsh interface ipv4 delete address \"Ethernet\" <the address you added>)";
    }

    private static string BuildNetshCommand(string ip)
    {
        if (!IPAddress.TryParse(ip, out var address)) return string.Empty;
        var bytes = address.GetAddressBytes();
        return $"netsh interface ipv4 add address \"Ethernet\" {bytes[0]}.{bytes[1]}.{bytes[2]}.250 255.255.255.0";
    }

    private DiscoverRow? Selected => ResultGrid.SelectedItem as DiscoverRow;

    /// <summary>
    /// Gives this machine an address in the selected device's subnet. After that the device can be
    /// reached - typically to open its web page and set the address you actually want on it.
    /// </summary>
    private void MatchSubnet_Click(object sender, RoutedEventArgs e)
    {
        var row = Selected;
        if (row == null)
        {
            MessageBox.Show("Select a device in the list first.", "Discover devices",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!IPAddress.TryParse(row.Ip, out var deviceIp)) return;

        var adapter = row.Adapter;
        if (string.IsNullOrWhiteSpace(adapter))
        {
            MessageBox.Show("The network adapter for this device is not known, so the address cannot be set automatically.",
                "Discover devices", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var (address, mask) = NetworkAdapterConfig.SuggestAddressFor(deviceIp);
        var question = $"Give \"{adapter}\" the address {address} / {mask}, so it is in the same subnet as {row.Ip}?\n\n" +
                       NetworkAdapterConfig.BuildStaticCommand(adapter, address, mask) +
                       "\n\nWindows will ask for administrator rights. Use 'Restore DHCP' to put the adapter back.";
        if (MessageBox.Show(question, "Match subnet", MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes) return;

        var (ok, message) = NetworkAdapterConfig.SetStaticAddress(adapter, address, mask);
        StatusText.Text = message;

        if (ok)
        {
            RefreshAdapterText();
            if (MessageBox.Show($"{message}\n\nOpen http://{row.Ip} now?", "Match subnet",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                NetworkAdapterConfig.OpenWebInterface(row.Ip);
        }
        else
        {
            MessageBox.Show(message, "Match subnet", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenWeb_Click(object sender, RoutedEventArgs e)
    {
        var row = Selected;
        if (row == null) return;
        NetworkAdapterConfig.OpenWebInterface(row.Ip);
    }

    private void RestoreDhcp_Click(object sender, RoutedEventArgs e)
    {
        var adapter = Selected?.Adapter;
        if (string.IsNullOrWhiteSpace(adapter))
        {
            MessageBox.Show("Select a device first - its adapter is the one that is put back on automatic.",
                "Discover devices", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show($"Put \"{adapter}\" back on automatic addressing (DHCP)?", "Restore DHCP",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        var (_, message) = NetworkAdapterConfig.SetDhcp(adapter);
        StatusText.Text = message;
        RefreshAdapterText();
    }

    /// <summary>Runs the built-in DHCP server for the selected device.</summary>
    private void Dhcp_Click(object sender, RoutedEventArgs e)
    {
        var row = Selected;
        var dialog = new DhcpWindow(row?.Adapter, row?.Mac, row?.Ip) { Owner = this };
        dialog.ShowDialog();

        RefreshAdapterText();

        if (dialog.DeviceToAdd == null) return;

        SelectedDevices.Clear();
        SelectedDevices.Add(dialog.DeviceToAdd);
        DialogResult = true;
    }

    private void RefreshAdapterText()
    {
        var subnets = NetworkDiscovery.GetLocalSubnets();
        AdapterText.Text = subnets.Count == 0
            ? "No active IPv4 adapter found."
            : string.Join("\n", subnets.Select(s => $"· {s.AdapterName}: {s.Address} / {s.Mask}"));
    }

    private void CopyCommand_Click(object sender, RoutedEventArgs e)
    {
        var row = ResultGrid.SelectedItem as DiscoverRow ?? _rows.FirstOrDefault(r => !r.InLocalSubnet);
        if (row == null) return;

        try
        {
            Clipboard.SetText(BuildNetshCommand(row.Ip));
            StatusText.Text = "Command copied to the clipboard.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Discover devices", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        SelectedDevices.Clear();

        foreach (var row in _rows.Where(r => r.Selected))
        {
            SelectedDevices.Add(new DeviceConfig
            {
                Name = string.IsNullOrWhiteSpace(row.Name) ? row.Ip : row.Name.Trim(),
                Host = row.Ip,
                Description = string.IsNullOrWhiteSpace(row.Vendor)
                    ? row.Info
                    : string.IsNullOrWhiteSpace(row.Info) ? row.Vendor : $"{row.Vendor} - {row.Info}",
                MacAddress = row.Mac,
                Mode = CheckMode.Icmp,
                IntervalMs = 1000,
                DownIntervalMs = 1000,
                TimeoutMs = 1000,
                FailThreshold = 3,
                MaxLoggedFailures = 5,
                Enabled = row.InLocalSubnet   // a device on another subnet is added but left disabled
            });
        }

        if (SelectedDevices.Count == 0)
        {
            MessageBox.Show("No devices are selected.", "Discover devices",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
    }

    private static int ParseInt(string text, int fallback)
        => int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    protected override void OnClosed(EventArgs e)
    {
        _cts?.Cancel();
        _discovery?.Dispose();
        base.OnClosed(e);
    }
}
