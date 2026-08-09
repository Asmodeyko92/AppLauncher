using System.Globalization;
using System.Windows.Data;
using AppLauncher.Models;
using AppLauncher.Services;

namespace AppLauncher.Converters;

public sealed class LauncherIconConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is LauncherItem item ? IconService.GetIcon(item) : null;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
