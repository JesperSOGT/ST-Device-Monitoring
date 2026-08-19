using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Windows;
using ST_Device_Monitoring.Core;
using ST_Device_Monitoring.Models;

namespace ST_Device_Monitoring.Views;

/// <summary>One row in the scan result grid.</summary>
public sealed class ScanRow : INotifyPropertyChanged
{
    private bool _selected;
    private string _name = string.Empty;

    public string Address { get; init; } = string.Empty;
    public bool Responded { get; init; }
    public long RoundtripMs { get; init; }
    public string Status { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public string RespondedText => Responded ? "Yes" : "No";
    public string ResponseText => Responded ? $"{RoundtripMs} ms" : "-";

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

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public partial class RangeScanWindow : Window
{
    private readonly List<ScanRow> _allRows = new();
    private readonly ObservableCollection<ScanRow> _shown = new();
    private CancellationTokenSource? _cts;

    /// <summary>Devices the user chose to add.</summary>
    public List<DeviceConfig> SelectedDevices { get; } = new();

    public RangeScanWindow(string suggestedFrom, string suggestedTo)
    {
        InitializeComponent();
        FromBox.Text = suggestedFrom;
        ToBox.Text = suggestedTo;
        ResultGrid.ItemsSource = _shown;
        ValidateRange();
    }

    private CheckMode SelectedMode => SnmpBox.IsChecked == true ? CheckMode.Snmp
        : TcpBox.IsChecked == true ? CheckMode.TcpPort
        : CheckMode.Icmp;

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (PortBox == null || CommunityBox == null) return;

        var mode = SelectedMode;
        PortBox.IsEnabled = mode != CheckMode.Icmp;
        CommunityBox.IsEnabled = mode == CheckMode.Snmp;

        if (mode == CheckMode.Snmp && PortBox.Text.Trim() is "502" or "") PortBox.Text = "161";
        if (mode == CheckMode.TcpPort && PortBox.Text.Trim() is "161" or "") PortBox.Text = "502";
    }

    /// <summary>Live IPv4 validation of the two range fields.</summary>
    private void Range_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => ValidateRange();

    private bool ValidateRange()
    {
        if (RangeHint == null || ScanButton == null) return false;

        var error = NetworkValidation.ValidateRange(FromBox.Text, ToBox.Text);
        if (error != null)
        {
            RangeHint.Foreground = System.Windows.Media.Brushes.Firebrick;
            RangeHint.Text = error;
            ScanButton.IsEnabled = false;
            return false;
        }

        NetworkValidation.TryParseIPv4(FromBox.Text, out var from, out _);
        NetworkValidation.TryParseIPv4(ToBox.Text, out var to, out _);
        var count = HostScanner.ToUInt(to) - HostScanner.ToUInt(from) + 1;

        RangeHint.Foreground = System.Windows.Media.Brushes.Gray;
        RangeHint.Text = $"{count:N0} address(es) will be scanned.";
        ScanButton.IsEnabled = true;
        return true;
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateRange()) return;

        NetworkValidation.TryParseIPv4(FromBox.Text, out var from, out _);
        NetworkValidation.TryParseIPv4(ToBox.Text, out var to, out _);

        var mode = SelectedMode;
        var port = ParseInt(PortBox.Text, mode == CheckMode.Snmp ? 161 : 502);
        var timeout = Math.Clamp(ParseInt(TimeoutBox.Text, 500), 50, 10_000);
        var resolve = ResolveNamesBox.IsChecked == true;
        var community = string.IsNullOrWhiteSpace(CommunityBox.Text) ? "public" : CommunityBox.Text.Trim();

        var addresses = HostScanner.Expand(from, to);
        ScanProgress.Minimum = 0;
        ScanProgress.Maximum = addresses.Count;
        ScanProgress.Value = 0;
        StatusText.Text = $"Scanning {addresses.Count} addresses…";

        ScanButton.IsEnabled = false;
        StopScanButton.IsEnabled = true;
        AddButton.IsEnabled = false;
        _allRows.Clear();
        _shown.Clear();

        _cts = new CancellationTokenSource();
        var progress = new Progress<int>(done => ScanProgress.Value = done);

        try
        {
            var results = await HostScanner.ScanAsync(from, to, mode, port, timeout, resolve,
                progress, _cts.Token, 64, community);

            var prefix = PrefixBox.Text.Trim();
            foreach (var r in results)
            {
                var name = !string.IsNullOrWhiteSpace(r.HostName)
                    ? r.HostName.Split('.')[0]
                    : string.IsNullOrEmpty(prefix) ? r.Address.ToString() : $"{prefix} {r.Address}";

                _allRows.Add(new ScanRow
                {
                    Address = r.Address.ToString(),
                    Responded = r.Responded,
                    RoundtripMs = r.RoundtripMs,
                    Status = r.Status,
                    Selected = r.Responded,
                    Name = name,
                    Description = r.Description
                });
            }

            ApplyFilter();
            var responded = _allRows.Count(r => r.Responded);
            StatusText.Text = $"{responded} of {_allRows.Count} answered.";
            AddButton.IsEnabled = responded > 0;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Scan stopped.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Scan IP range", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            ScanButton.IsEnabled = true;
            StopScanButton.IsEnabled = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void StopScan_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void OnlyResponding_Changed(object sender, RoutedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        var onlyResponding = OnlyRespondingBox.IsChecked == true;
        _shown.Clear();
        foreach (var row in _allRows)
            if (!onlyResponding || row.Responded)
                _shown.Add(row);
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _shown) row.Selected = true;
    }

    private void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _shown) row.Selected = false;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var mode = SelectedMode;
        var port = ParseInt(PortBox.Text, mode == CheckMode.Snmp ? 161 : 502);
        var interval = Math.Max(20, ParseInt(IntervalBox.Text, 1000));
        var timeout = Math.Clamp(ParseInt(TimeoutBox.Text, 500), 50, 60_000);
        var group = GroupBox.Text.Trim();
        var community = string.IsNullOrWhiteSpace(CommunityBox.Text) ? "public" : CommunityBox.Text.Trim();

        SelectedDevices.Clear();
        foreach (var row in _allRows.Where(r => r.Selected))
        {
            SelectedDevices.Add(new DeviceConfig
            {
                Name = string.IsNullOrWhiteSpace(row.Name) ? row.Address : row.Name.Trim(),
                Host = row.Address,
                Group = group,
                Description = row.Description,
                Mode = mode,
                Port = port,
                Community = community,
                IntervalMs = interval,
                DownIntervalMs = Math.Max(interval, 1000),
                TimeoutMs = timeout,
                FailThreshold = 3,
                MaxLoggedFailures = 5,
                Enabled = true
            });
        }

        if (SelectedDevices.Count == 0)
        {
            MessageBox.Show("No devices are selected.", "Scan IP range",
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
        base.OnClosed(e);
    }
}
