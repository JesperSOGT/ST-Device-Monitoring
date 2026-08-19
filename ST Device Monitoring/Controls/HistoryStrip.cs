using System.Globalization;
using System.Windows;
using System.Windows.Media;
using ST_Device_Monitoring.Core;

namespace ST_Device_Monitoring.Controls;

/// <summary>
/// Lightweight visualisation of the most recent pings: a green bar scaled by response time,
/// a full-height red bar for a failure. Drawn directly in OnRender (no visual per sample),
/// so it can be refreshed many times per second without loading the UI.
/// </summary>
public sealed class HistoryStrip : FrameworkElement
{
    private static readonly Brush OkBrush = Frozen(Color.FromRgb(0x43, 0xA0, 0x47));
    private static readonly Brush SlowBrush = Frozen(Color.FromRgb(0xF9, 0xA8, 0x25));
    private static readonly Brush FailBrush = Frozen(Color.FromRgb(0xC6, 0x28, 0x28));
    private static readonly Brush EmptyBrush = Frozen(Color.FromRgb(0xE0, 0xE0, 0xE0));
    private static readonly Brush BackBrush = Frozen(Color.FromRgb(0xFA, 0xFA, 0xFA));
    private static readonly Pen GridPen = FrozenPen(Color.FromRgb(0xD0, 0xD0, 0xD0));
    private static readonly Brush TextBrush = Frozen(Color.FromRgb(0x55, 0x55, 0x55));
    private static readonly Typeface Font = new("Segoe UI");

    public static readonly DependencyProperty SamplesProperty = DependencyProperty.Register(
        nameof(Samples), typeof(PingSample[]), typeof(HistoryStrip),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Response time in ms above which a reply is drawn as "slow" (amber).</summary>
    public static readonly DependencyProperty SlowThresholdProperty = DependencyProperty.Register(
        nameof(SlowThreshold), typeof(int), typeof(HistoryStrip),
        new FrameworkPropertyMetadata(50, FrameworkPropertyMetadataOptions.AffectsRender));

    public PingSample[]? Samples
    {
        get => (PingSample[]?)GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    public int SlowThreshold
    {
        get => (int)GetValue(SlowThresholdProperty);
        set => SetValue(SlowThresholdProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 1 || h <= 1) return;

        dc.DrawRectangle(BackBrush, null, new Rect(0, 0, w, h));

        var samples = Samples;
        if (samples == null || samples.Length == 0)
        {
            DrawText(dc, "No data yet", 4, 4);
            return;
        }

        // Scale: highest response time in the window (at least 10 ms).
        long maxRtt = 10;
        var any = false;
        foreach (var s in samples)
        {
            if (!s.HasValue) continue;
            any = true;
            if (s.Success && s.RoundtripMs > maxRtt) maxRtt = s.RoundtripMs;
        }

        var slotWidth = w / samples.Length;
        var barWidth = Math.Max(1.0, slotWidth - 1.0);
        var plotHeight = h - 14;

        for (int i = 0; i < samples.Length; i++)
        {
            var s = samples[i];
            var x = i * slotWidth;

            if (!s.HasValue)
            {
                dc.DrawRectangle(EmptyBrush, null, new Rect(x, h - 15, barWidth, 2));
                continue;
            }

            if (!s.Success)
            {
                dc.DrawRectangle(FailBrush, null, new Rect(x, 0, barWidth, plotHeight));
                continue;
            }

            var ratio = Math.Clamp(s.RoundtripMs / (double)maxRtt, 0.04, 1.0);
            var barHeight = Math.Max(2.0, ratio * plotHeight);
            var brush = s.RoundtripMs >= SlowThreshold ? SlowBrush : OkBrush;
            dc.DrawRectangle(brush, null, new Rect(x, plotHeight - barHeight, barWidth, barHeight));
        }

        dc.DrawLine(GridPen, new Point(0, plotHeight + 0.5), new Point(w, plotHeight + 0.5));

        if (any)
        {
            DrawText(dc, $"max {maxRtt} ms", 2, plotHeight + 1);
            DrawText(dc, $"last {samples.Length} pings", w - 100, plotHeight + 1);
        }
    }

    private static void DrawText(DrawingContext dc, string text, double x, double y)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            Font, 10, TextBrush, 96);
        dc.DrawText(ft, new Point(Math.Max(0, x), y));
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Pen FrozenPen(Color c)
    {
        var p = new Pen(Frozen(c), 1);
        p.Freeze();
        return p;
    }
}
