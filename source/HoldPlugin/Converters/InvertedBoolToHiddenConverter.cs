using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace HoldPlugin.Converters;

[ValueConversion(typeof(bool), typeof(Visibility))]
public class InvertedBoolToHiddenConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not bool boolValue)
            throw new NotSupportedException();

        return boolValue ? Visibility.Hidden : Visibility.Visible;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
