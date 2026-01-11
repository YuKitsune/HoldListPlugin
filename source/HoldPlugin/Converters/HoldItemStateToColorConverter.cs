using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using HoldPlugin.ViewModels;

namespace HoldPlugin.Converters;

[ValueConversion(typeof(HoldItemState), typeof(SolidColorBrush))]
public class HoldItemStateToColorConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not HoldItemState state)
            throw new NotSupportedException();

        return state switch
        {
            HoldItemState.Jurisdiction => Theme.JurisdictionColor,
            HoldItemState.Handover => Theme.HandoverColor,
            _ => throw new ArgumentOutOfRangeException(nameof(state))
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
