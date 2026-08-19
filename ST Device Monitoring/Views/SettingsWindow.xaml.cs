using System.Globalization;
using System.Windows;
using Microsoft.Win32;
using ST_Device_Monitoring.Controls;
using ST_Device_Monitoring.Core;
using ST_Device_Monitoring.Models;

namespace ST_Device_Monitoring.Views;

public partial class SettingsWindow : Window
{
    private readonly AppConfig _config;
    private readonly AlertDispatcher _alerts;

    /// <summary>True when the log folder or retention changed and a restart is recommended.</summary>
    public bool RestartRecommended { get; private set; }

    public SettingsWindow(AppConfig config, AlertDispatcher alerts)
    {
        InitializeComponent();
        _config = config;
        _alerts = alerts;

        var a = config.Alerts;
        var u = config.Ui;

        // Run mode
        var serviceState = WindowsServiceInstaller.Query();
        ModeServiceBox.IsChecked = serviceState is ServiceState.Running or ServiceState.Stopped;
        ModeNormalBox.IsChecked = !ModeServiceBox.IsChecked;
        UpdateServiceState();

        TrayIconBox.IsChecked = u.ShowTrayIcon;
        MinimizeToTrayBox.IsChecked = u.MinimizeToTray;
        CloseToTrayBox.IsChecked = u.CloseToTray;
        StartMinimizedBox.IsChecked = u.StartMinimized;
        StartWithWindowsBox.IsChecked = StartupRegistration.IsRegistered();
        AutoStartBox.IsChecked = config.AutoStart;

        // Notifications
        BalloonBox.IsChecked = a.BalloonEnabled;
        SoundBox.IsChecked = a.SoundEnabled;
        SoundFileBox.Text = a.SoundFile;
        NotifyRecoveryBox.IsChecked = a.NotifyOnRecovery;
        ThrottleBox.Text = a.ThrottleSeconds.ToString(CultureInfo.InvariantCulture);

        EmailBox.IsChecked = a.EmailEnabled;
        SmtpHostBox.Text = a.SmtpHost;
        SmtpPortBox.Text = a.SmtpPort.ToString(CultureInfo.InvariantCulture);
        SmtpUserBox.Text = a.SmtpUser;
        SmtpPasswordBox.Password = DpapiProtector.Unprotect(a.SmtpPasswordProtected);
        MailFromBox.Text = a.MailFrom;
        MailToBox.Text = a.MailTo;
        SmtpSslBox.IsChecked = a.SmtpUseSsl;

        WebhookBox.IsChecked = a.WebhookEnabled;
        WebhookUrlBox.Text = a.WebhookUrl;

        // Logging
        LogDirBox.Text = config.LogDirectory;
        RetentionBox.Text = config.LogRetentionDays.ToString(CultureInfo.InvariantCulture);
        UiRefreshBox.Text = config.UiRefreshMs.ToString(CultureInfo.InvariantCulture);
        LogAllBox.IsChecked = config.LogAllPings;
        DailySummaryBox.IsChecked = config.WriteDailySummary;

        MaxSizeBox.Text = config.MaxLogFileSizeMB.ToString(CultureInfo.InvariantCulture);
        RingTrimBox.Text = config.RingTrimPercent.ToString(CultureInfo.InvariantCulture);
        RotationNoneBox.IsChecked = config.LogRotation == LogRotationMode.None;
        RotationZipBox.IsChecked = config.LogRotation == LogRotationMode.RotateAndZip;
        RotationRingBox.IsChecked = config.LogRotation == LogRotationMode.RingBuffer;
    }

    // ---------- Service ----------

    private void UpdateServiceState()
    {
        var state = WindowsServiceInstaller.Query();
        ServiceStateText.Text = "Service status: " + WindowsServiceInstaller.StateText(state);
        ServiceHintText.Text = state == ServiceState.Running
            ? "Note: the service and this window both monitor and both write to the log folder. " +
              "Stop the monitoring in the window (or the service) to avoid duplicate log entries."
            : string.Empty;
    }

    private void InstallService_Click(object sender, RoutedEventArgs e)
    {
        var (ok, message) = WindowsServiceInstaller.Install();
        StatusText.Text = message;
        if (ok) ModeServiceBox.IsChecked = true;
        UpdateServiceState();
    }

    private void UninstallService_Click(object sender, RoutedEventArgs e)
    {
        var (ok, message) = WindowsServiceInstaller.Uninstall();
        StatusText.Text = message;
        if (ok) ModeNormalBox.IsChecked = true;
        UpdateServiceState();
    }

    private void StartService_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = WindowsServiceInstaller.Start().message;
        UpdateServiceState();
    }

    private void StopService_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = WindowsServiceInstaller.Stop().message;
        UpdateServiceState();
    }

    // ---------- Browse ----------

    private void BrowseSound_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Wave files (*.wav)|*.wav|All files (*.*)|*.*",
            Title = "Choose alert sound"
        };
        if (dialog.ShowDialog(this) == true) SoundFileBox.Text = dialog.FileName;
    }

    private void BrowseLogFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose log folder" };
        if (dialog.ShowDialog(this) == true) LogDirBox.Text = dialog.FolderName;
    }

    // ---------- Test ----------

    private async void TestAlert_Click(object sender, RoutedEventArgs e)
    {
        ApplyAlertSettings();
        TestResultText.Text = "Sending…";
        TestResultText.Text = await _alerts.SendTestAsync();
    }

    private void ApplyAlertSettings()
    {
        var a = _config.Alerts;
        a.BalloonEnabled = BalloonBox.IsChecked == true;
        a.SoundEnabled = SoundBox.IsChecked == true;
        a.SoundFile = SoundFileBox.Text.Trim();
        a.NotifyOnRecovery = NotifyRecoveryBox.IsChecked == true;
        a.ThrottleSeconds = ParseInt(ThrottleBox.Text, a.ThrottleSeconds);

        a.EmailEnabled = EmailBox.IsChecked == true;
        a.SmtpHost = SmtpHostBox.Text.Trim();
        a.SmtpPort = ParseInt(SmtpPortBox.Text, a.SmtpPort);
        a.SmtpUser = SmtpUserBox.Text.Trim();
        a.SmtpPasswordProtected = DpapiProtector.Protect(SmtpPasswordBox.Password);
        a.MailFrom = MailFromBox.Text.Trim();
        a.MailTo = MailToBox.Text.Trim();
        a.SmtpUseSsl = SmtpSslBox.IsChecked == true;

        a.WebhookEnabled = WebhookBox.IsChecked == true;
        a.WebhookUrl = WebhookUrlBox.Text.Trim();

        _alerts.Settings = a;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ApplyAlertSettings();

        var u = _config.Ui;
        u.ShowTrayIcon = TrayIconBox.IsChecked == true;
        u.MinimizeToTray = MinimizeToTrayBox.IsChecked == true;
        u.CloseToTray = CloseToTrayBox.IsChecked == true;
        u.StartMinimized = StartMinimizedBox.IsChecked == true;
        _config.AutoStart = AutoStartBox.IsChecked == true;

        var wantStartup = StartWithWindowsBox.IsChecked == true;
        if (wantStartup != StartupRegistration.IsRegistered())
        {
            var error = StartupRegistration.Apply(wantStartup);
            if (error != null)
                MessageBox.Show("Could not change the Windows startup entry:\n" + error,
                    "ST Device Monitoring", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        u.StartWithWindows = StartupRegistration.IsRegistered();

        var newLogDir = LogDirBox.Text.Trim();
        var newRetention = ParseInt(RetentionBox.Text, _config.LogRetentionDays);
        RestartRecommended = !string.Equals(newLogDir, _config.LogDirectory, StringComparison.OrdinalIgnoreCase);

        _config.LogDirectory = string.IsNullOrWhiteSpace(newLogDir) ? "Logs" : newLogDir;
        _config.LogRetentionDays = newRetention;
        _config.UiRefreshMs = Math.Max(100, ParseInt(UiRefreshBox.Text, _config.UiRefreshMs));
        _config.LogAllPings = LogAllBox.IsChecked == true;
        _config.WriteDailySummary = DailySummaryBox.IsChecked == true;

        _config.MaxLogFileSizeMB = Math.Max(0, ParseInt(MaxSizeBox.Text, _config.MaxLogFileSizeMB));
        _config.RingTrimPercent = Math.Clamp(ParseInt(RingTrimBox.Text, _config.RingTrimPercent), 10, 90);
        _config.LogRotation = RotationZipBox.IsChecked == true ? LogRotationMode.RotateAndZip
            : RotationRingBox.IsChecked == true ? LogRotationMode.RingBuffer
            : LogRotationMode.None;

        DialogResult = true;
    }

    private static int ParseInt(string text, int fallback)
        => int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
}
