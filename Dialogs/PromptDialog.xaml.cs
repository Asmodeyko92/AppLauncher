using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using AppLauncher.Services;

namespace AppLauncher.Dialogs;

public partial class PromptDialog : Window
{
    private readonly bool _darkMode;

    private PromptDialog(Window owner, string title, string message, string initialValue)
    {
        InitializeComponent();
        Owner = owner;
        _darkMode = ApplyOwnerTheme(owner);
        SourceInitialized += (_, _) => NativeWindowService.ApplyModernWindowStyle(
            new WindowInteropHelper(this).Handle,
            _darkMode);
        TitleText.Text = title;
        MessageText.Text = message;
        ValueTextBox.Text = initialValue;
        Loaded += (_, _) =>
        {
            ValueTextBox.Focus();
            ValueTextBox.SelectAll();
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                DialogResult = false;
                Close();
            }
        };
    }

    public string Value => ValueTextBox.Text.Trim();

    public static string? Show(Window owner, string title, string message, string initialValue = "")
    {
        var dialog = new PromptDialog(owner, title, message, initialValue);
        return dialog.ShowDialog() == true ? dialog.Value : null;
    }

    private bool ApplyOwnerTheme(Window owner)
    {
        Resources["DialogSurfaceBrush"] = GetOwnerBrush(owner, "PanelBrush", "#181B24");
        Resources["DialogTextBrush"] = GetOwnerBrush(owner, "TextBrush", "#FFFFFF");
        Resources["DialogMutedTextBrush"] = GetOwnerBrush(owner, "MutedTextBrush", "#A8FFFFFF");
        Resources["DialogBorderBrush"] = GetOwnerBrush(owner, "ColorSwatchBorderBrush", "#30FFFFFF");
        Resources["DialogControlBrush"] = GetOwnerBrush(owner, "TileBrush", "#15FFFFFF");
        Resources["DialogHoverBrush"] = GetOwnerBrush(owner, "HoverBrush", "#17FFFFFF");
        Resources["DialogAccentBrush"] = GetOwnerBrush(owner, "AccentSolidBrush", "#6E62FF");

        if (Resources["DialogSurfaceBrush"] is not SolidColorBrush surface)
            return true;
        var luminance = (0.2126 * surface.Color.R + 0.7152 * surface.Color.G + 0.0722 * surface.Color.B) / 255;
        return luminance < 0.5;
    }

    private static Brush GetOwnerBrush(Window owner, string key, string fallback)
    {
        if (owner.Resources[key] is Brush brush)
            return brush;
        var fallbackBrush = (SolidColorBrush)new BrushConverter().ConvertFromString(fallback)!;
        fallbackBrush.Freeze();
        return fallbackBrush;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ValueTextBox.Text))
            return;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
