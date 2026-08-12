using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using AppLauncher.Dialogs;

namespace AppLauncher.Converters;

public sealed class WidgetBackgroundBrushConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var widgetColor = values.ElementAtOrDefault(0) as string;
        var matchesTileColor = values.ElementAtOrDefault(1) is true;
        var tileColor = values.ElementAtOrDefault(2) as string;
        var color = matchesTileColor ? tileColor : widgetColor;

        if (!ColorPickerDialog.TryNormalizeColor(color, out var normalized))
            normalized = "#242730";

        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(normalized)!);
        brush.Freeze();
        return brush;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => targetTypes.Select(_ => Binding.DoNothing).ToArray();
}
