using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using ST_Device_Monitoring.Core;
using ST_Device_Monitoring.Models;

namespace ST_Device_Monitoring.Views;

/// <summary>
/// Shows what the newest release on GitHub is, and installs it on request.
///
/// Nothing on disk is touched until the user presses "Download and install", and even then the
/// file is verified before the exe is swapped. If the window is closed the program keeps running
/// the version it already had.
/// </summary>
public partial class UpdateWindow : Window
{
    private readonly AppConfig _config;
    private UpdateInfo? _info;
    private CancellationTokenSource? _cts;
    private bool _busy;

    /// <summary>True when the update was started and the application has to shut down now.</summary>
    public bool RestartRequested { get; private set; }

    /// <summary>True when the user changed a setting that has to be saved.</summary>
    public bool SettingsChanged { get; private set; }

    public UpdateWindow(AppConfig config, UpdateInfo? alreadyFound = null)
    {
        InitializeComponent();
        _config = config;
        _info = alreadyFound;

        SourceText.Text = $"Source: github.com/{config.Updates.RepositoryOwner}/{config.Updates.RepositoryName}" +
                          (config.Updates.IncludePreReleases ? "  ·  pre-releases included" : string.Empty) +
                          $"  ·  this build: {DescribeVariant()}";

        if (_info != null)
        {
            Show(_info);
        }
        else
        {
            Loaded += async (_, _) => await CheckAsync();
        }

        Closing += (_, _) => _cts?.Cancel();
    }

    private static string DescribeVariant() => UpdateChecker.IsFrameworkDependentBuild
        ? "needs the .NET 8 Desktop Runtime"
        : "runs on its own";

    // ---------- Checking ----------

    private async void Check_Click(object sender, RoutedEventArgs e) => await CheckAsync();

    private async Task CheckAsync()
    {
        if (_busy) return;
        SetBusy(true);

        HeadlineText.Text = "Looking for a new version…";
        VersionText.Text = string.Empty;
        ErrorText.Text = string.Empty;
        PlanText.Text = string.Empty;
        NotesBox.Text = string.Empty;
        AssetBox.Items.Clear();

        _cts?.Cancel();
        _cts = new CancellationTokenSource();

        var (info, error) = await UpdateChecker.CheckAsync(
            _config.Updates.RepositoryOwner, _config.Updates.RepositoryName,
            _config.Updates.IncludePreReleases, _cts.Token).ConfigureAwait(true);

        _config.Updates.LastChecked = DateTime.Now;
        SettingsChanged = true;

        SetBusy(false);

        if (error != null)
        {
            HeadlineText.Text = "The check could not be completed";
            ErrorText.Text = error;
            return;
        }

        if (info == null) return;   // cancelled

        _info = info;
        Show(info);
    }

    private void Show(UpdateInfo info)
    {
        VersionText.Text = $"Installed: v{AppInfo.Version} (built {AppInfo.BuildDate})   ·   " +
                           $"Newest on GitHub: {info.Tag}" +
                           (info.Published != null ? $", published {info.Published:dd-MM-yyyy}" : string.Empty) +
                           (info.IsPreRelease ? "   ·   pre-release" : string.Empty);

        NotesBox.Text = string.IsNullOrWhiteSpace(info.Notes)
            ? "(the release has no description)"
            : info.Notes.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);

        OpenPageButton.IsEnabled = !string.IsNullOrWhiteSpace(info.HtmlUrl);

        AssetBox.Items.Clear();
        foreach (var asset in info.Assets)
            AssetBox.Items.Add(new AssetChoice(asset));

        AssetBox.IsEnabled = info.Assets.Count > 0;
        if (info.Recommended != null)
            AssetBox.SelectedIndex = info.Assets.ToList().FindIndex(a => ReferenceEquals(a, info.Recommended));
        else if (AssetBox.Items.Count > 0)
            AssetBox.SelectedIndex = 0;

        if (!info.IsNewer)
        {
            HeadlineText.Text = info.Version != null && info.Version < UpdateChecker.CurrentVersion
                ? "This copy is newer than the newest release"
                : "ST Device Monitoring is up to date";
            InstallButton.IsEnabled = AssetBox.Items.Count > 0;
            InstallButton.Content = "Reinstall this version";
            SkipButton.IsEnabled = false;
            PlanText.Text = string.Empty;
            return;
        }

        HeadlineText.Text = $"A new version is available: {info.Tag}";
        InstallButton.Content = "Download and install";
        InstallButton.IsEnabled = AssetBox.Items.Count > 0;
        SkipButton.IsEnabled = true;

        if (info.Assets.Count == 0)
        {
            ErrorText.Text = "The release has no exe file attached, so it cannot be installed from here. " +
                             "Use \"Open release page\" instead.";
            InstallButton.IsEnabled = false;
        }

        var plan = UpdateInstaller.CreatePlan();
        PlanText.Text = plan.Description;
    }

    private sealed class AssetChoice
    {
        public AssetChoice(UpdateAsset asset) => Asset = asset;
        public UpdateAsset Asset { get; }
        public override string ToString() =>
            $"{Asset.Name}  ({Asset.SizeText}" +
            (Asset.IsFrameworkDependent ? ", needs the .NET 8 Desktop Runtime" : ", runs on its own") + ")";
    }

    private void Asset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_busy) return;
        InstallButton.IsEnabled = AssetBox.SelectedItem is AssetChoice;
    }

    // ---------- Installing ----------

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _info == null) return;
        if (AssetBox.SelectedItem is not AssetChoice choice) return;

        var plan = UpdateInstaller.CreatePlan();
        var question = $"Install {_info.Tag}?\n\n{plan.Description}\n\n" +
                       "Monitoring stops while the program restarts.";
        if (MessageBox.Show(question, "Update", MessageBoxButton.OKCancel, MessageBoxImage.Question)
            != MessageBoxResult.OK)
            return;

        SetBusy(true);
        ErrorText.Text = string.Empty;
        DownloadProgress.Visibility = Visibility.Visible;
        DownloadProgress.Value = 0;
        ProgressText.Text = $"Downloading {choice.Asset.Name} ({choice.Asset.SizeText})…";

        _cts?.Cancel();
        _cts = new CancellationTokenSource();

        string file;
        try
        {
            var progress = new Progress<double>(p =>
            {
                DownloadProgress.Value = p;
                ProgressText.Text = $"Downloading {choice.Asset.Name} - {p * 100:0} %";
            });

            file = await UpdateChecker.DownloadAsync(choice.Asset, progress, _cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            SetBusy(false);
            DownloadProgress.Visibility = Visibility.Collapsed;
            ProgressText.Text = "The download was cancelled. Nothing was changed.";
            return;
        }
        catch (Exception ex)
        {
            SetBusy(false);
            DownloadProgress.Visibility = Visibility.Collapsed;
            ProgressText.Text = string.Empty;
            ErrorText.Text = "The download failed: " + ex.GetBaseException().Message;
            return;
        }

        ProgressText.Text = "Download verified. Starting the update…";

        var (started, error) = UpdateInstaller.Launch(plan, file, _info.Tag);
        if (!started)
        {
            SetBusy(false);
            DownloadProgress.Visibility = Visibility.Collapsed;
            ErrorText.Text = error ?? "The update could not be started.";
            ProgressText.Text = "The downloaded file is still here:\n" + file;
            return;
        }

        // The script is now waiting for this program to release its own exe.
        RestartRequested = true;
        DialogResult = true;
        Close();
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        if (_info == null) return;
        _config.Updates.SkipVersion = _info.Tag;
        SettingsChanged = true;
        SkipButton.IsEnabled = false;
        ProgressText.Text = $"{_info.Tag} will not be announced again. A later version still will.";
    }

    private void OpenPage_Click(object sender, RoutedEventArgs e)
    {
        var url = string.IsNullOrWhiteSpace(_info?.HtmlUrl) ? AppInfo.ReleasesUrl : _info!.HtmlUrl;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ErrorText.Text = "Could not open the browser: " + ex.GetBaseException().Message;
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        CheckButton.IsEnabled = !busy;
        InstallButton.IsEnabled = !busy && _info is { Assets.Count: > 0 };
        AssetBox.IsEnabled = !busy && _info is { Assets.Count: > 0 };
        SkipButton.IsEnabled = !busy && _info is { IsNewer: true };
        Cursor = busy ? System.Windows.Input.Cursors.AppStarting : null;
    }
}
