using System.IO;
using System.Drawing;
using System.Media;
using System.Windows.Forms;
using ST_Device_Monitoring.Models;

namespace ST_Device_Monitoring.Controls;

/// <summary>
/// System tray icon with balloon notifications and a small menu. Uses the WinForms NotifyIcon,
/// which needs no external package. All events are raised on the UI thread.
/// </summary>
public sealed class TrayNotifier : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _startItem;
    private readonly ToolStripMenuItem _stopItem;

    public event Action? ShowRequested;
    public event Action? StartRequested;
    public event Action? StopRequested;
    public event Action? ExitRequested;

    public TrayNotifier()
    {
        var menu = new ContextMenuStrip();
        var showItem = new ToolStripMenuItem("Open ST Device Monitoring");
        showItem.Click += (_, _) => ShowRequested?.Invoke();
        _startItem = new ToolStripMenuItem("Start monitoring");
        _startItem.Click += (_, _) => StartRequested?.Invoke();
        _stopItem = new ToolStripMenuItem("Stop monitoring");
        _stopItem.Click += (_, _) => StopRequested?.Invoke();
        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitRequested?.Invoke();

        menu.Items.Add(showItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_startItem);
        menu.Items.Add(_stopItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Visible = false,
            Text = AppInfo.ProductName,
            ContextMenuStrip = menu
        };
        _icon.DoubleClick += (_, _) => ShowRequested?.Invoke();
        _icon.BalloonTipClicked += (_, _) => ShowRequested?.Invoke();
    }

    private static Icon LoadIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path))
            {
                var icon = Icon.ExtractAssociatedIcon(path);
                if (icon != null) return icon;
            }
        }
        catch { /* fall through */ }
        return SystemIcons.Application;
    }

    public bool Visible
    {
        get => _icon.Visible;
        set => _icon.Visible = value;
    }

    /// <summary>Tooltip text - Windows limits this to 63 characters.</summary>
    public void UpdateStatus(bool monitoring, int devices, int down)
    {
        var text = monitoring
            ? $"{AppInfo.ProductName}\n{devices} devices, {down} down"
            : $"{AppInfo.ProductName}\nStopped";
        _icon.Text = text.Length > 63 ? text[..63] : text;

        _startItem.Enabled = !monitoring;
        _stopItem.Enabled = monitoring;
    }

    public void ShowBalloon(string title, string message, bool isError)
    {
        if (!_icon.Visible) return;
        _icon.ShowBalloonTip(isError ? 10000 : 5000, title, message,
            isError ? ToolTipIcon.Error : ToolTipIcon.Info);
    }

    /// <summary>Plays the configured .wav file, or the standard Windows warning sound.</summary>
    public static void PlayAlertSound(AlertSettings settings)
    {
        try
        {
            if (!settings.SoundEnabled) return;

            if (!string.IsNullOrWhiteSpace(settings.SoundFile) && File.Exists(settings.SoundFile))
            {
                using var player = new SoundPlayer(settings.SoundFile);
                player.Play();
                return;
            }
            SystemSounds.Hand.Play();
        }
        catch { /* a sound must never break monitoring */ }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
