using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace HyPrism.UI.Converters;

/// <summary>
/// Converts progress percentage (0-100) to width value (0-500 for progress bar)
/// </summary>
public class ProgressToWidthConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double progress)
        {
            // Progress is 0-100, convert to 0-500 (width of progress bar container)
            var maxWidth = 500.0;
            var width = (progress / 100.0) * maxWidth;
            return Math.Max(0, Math.Min(maxWidth, width));
        }
        return 0.0;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
