using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace DNSManagerPro.App.Converters;

/// <summary>Maps an InfoItem tone ("good", "warn", "bad") to a theme brush.</summary>
public sealed class ToneBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = (value as string) switch
        {
            "good" => "SuccessBrush",
            "warn" => "WarningBrush",
            "bad" => "ErrorBrush",
            _ => "TextBrush",
        };
        return Application.Current.Resources[key] as Brush ?? Brushes.White;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
