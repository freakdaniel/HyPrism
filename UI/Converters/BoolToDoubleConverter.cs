using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace HyPrism.UI.Converters;

public class BoolToDoubleConverter : IValueConverter
{
    public double TrueValue { get; set; } = 1;
    public double FalseValue { get; set; } = 0;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is bool flag && flag ? TrueValue : FalseValue;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double number)
        {
            return Math.Abs(number - TrueValue) < 0.0001;
        }

        return false;
    }
}
