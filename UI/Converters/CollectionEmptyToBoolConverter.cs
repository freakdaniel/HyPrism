using Avalonia.Data.Converters;
using System;
using System.Collections;
using System.Globalization;
using System.Linq;

namespace HyPrism.UI.Converters;

public class CollectionEmptyToBoolConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool isEmpty = true;

        if (value is ICollection collection)
        {
            isEmpty = collection.Count == 0;
        }
        else if (value is IEnumerable enumerable)
        {
            isEmpty = !enumerable.Cast<object>().Any();
        }

        var invert = string.Equals(parameter?.ToString(), "invert", StringComparison.OrdinalIgnoreCase);
        return invert ? !isEmpty : isEmpty;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
