using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Forge.Converters;

// Collapses a row's secondary-detail TextBlock (lock owner, download state,
// etc.) when it has nothing to say, instead of reserving its Margin's space
// for an empty string.
public sealed class EmptyStringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
