using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using ST_Device_Monitoring.Core;

namespace ST_Device_Monitoring.ViewModels;

/// <summary>Device state -> strong colour (status column).</summary>
public sealed class StateToBrushConverter : IValueConverter
{
    public static readonly SolidColorBrush Ok = Freeze(Color.FromRgb(0x2E, 0x7D, 0x32));
    public static readonly SolidColorBrush Warning = Freeze(Color.FromRgb(0xF9, 0xA8, 0x25));
    public static readonly SolidColorBrush Error = Freeze(Color.FromRgb(0xC6, 0x28, 0x28));
    public static readonly SolidColorBrush Idle = Freeze(Color.FromRgb(0x75, 0x75, 0x75));
    public static readonly SolidColorBrush Blocked = Freeze(Color.FromRgb(0x45, 0x6C, 0x8C));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is DeviceState state
            ? state switch
            {
                DeviceState.Ok => Ok,
                DeviceState.Warning => Warning,
                DeviceState.Error => Error,
                DeviceState.Blocked => Blocked,
                _ => Idle
            }
            : Idle;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;

    private static SolidColorBrush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

/// <summary>Device state -> soft row background.</summary>
public sealed class StateToRowBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Ok = Freeze(Color.FromRgb(0xF1, 0xF8, 0xF2));
    private static readonly SolidColorBrush Warning = Freeze(Color.FromRgb(0xFF, 0xF6, 0xE0));
    private static readonly SolidColorBrush Error = Freeze(Color.FromRgb(0xFD, 0xE7, 0xE7));
    private static readonly SolidColorBrush Idle = Freeze(Color.FromRgb(0xF5, 0xF5, 0xF5));
    private static readonly SolidColorBrush Blocked = Freeze(Color.FromRgb(0xEC, 0xF1, 0xF6));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is DeviceState state
            ? state switch
            {
                DeviceState.Ok => Ok,
                DeviceState.Warning => Warning,
                DeviceState.Error => Error,
                DeviceState.Blocked => Blocked,
                _ => Idle
            }
            : Idle;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;

    private static SolidColorBrush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

/// <summary>True (logging suppressed) -> orange text, otherwise grey.</summary>
public sealed class SuppressedToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Suppressed = Freeze(Color.FromRgb(0xE6, 0x51, 0x00));
    private static readonly SolidColorBrush Normal = Freeze(Color.FromRgb(0x60, 0x60, 0x60));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Suppressed : Normal;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;

    private static SolidColorBrush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
