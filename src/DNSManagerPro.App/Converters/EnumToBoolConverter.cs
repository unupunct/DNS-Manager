using System.Globalization;
using System.Windows.Data;

namespace DNSManagerPro.App.Converters;

/// <summary>Two-way bridge between an enum property and a RadioButton's IsChecked (ConverterParameter = enum value).</summary>
public sealed class EnumToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is not null && value.Equals(parameter);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? parameter : Binding.DoNothing;
}
