using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace HyPrism.UI.Converters;

public class BoolToThicknessConverter : IValueConverter
{
    public Thickness TrueValue { get; set; } = new(0, 0, 0, 48);
    public Thickness FalseValue { get; set; } = new(0, 0, 0, 32);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is bool flag && flag ? TrueValue : FalseValue;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return false;
    }
}
