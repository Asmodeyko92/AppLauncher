using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace AppLauncher.Converters;

public sealed class HexColorBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = value as string;
        if (string.IsNullOrWhiteSpace(text))
            text = parameter as string ?? "#242730";

        try
        {
            var color = (Color)ColorConverter.ConvertFromString(text)!;
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
        catch
        {
            return new SolidColorBrush(Color.FromRgb(36, 39, 48));
        }
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
