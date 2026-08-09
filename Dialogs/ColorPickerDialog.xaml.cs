using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using AppLauncher.Services;

namespace AppLauncher.Dialogs;

public partial class ColorPickerDialog : Window
{
    private readonly ObservableCollection<string> _savedColors;
    private bool _updatingText;
    private bool _draggingSaturationValue;
    private bool _draggingHue;
    private string _selectedColor = "#FFFFFF";
    private double _hue;
    private double _saturation;
    private double _value = 1;

    private ColorPickerDialog(
        string title,
        string initialColor,
        ObservableCollection<string> savedColors)
    {
        _savedColors = savedColors;
        InitializeComponent();
        SourceInitialized += (_, _) => NativeWindowService.ApplyModernWindowStyle(
            new WindowInteropHelper(this).Handle,
            true);
        TitleText.Text = title;
        SetColor(initialColor);
        RefreshSavedColors();

        Loaded += (_, _) =>
        {
            UpdateSelectors();
            HexTextBox.Focus();
            HexTextBox.SelectAll();
        };
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape)
                return;
            DialogResult = false;
            Close();
        };
    }

    public static string? Show(
        Window owner,
        string title,
        string initialColor,
        ObservableCollection<string> savedColors)
    {
        var dialog = new ColorPickerDialog(title, initialColor, savedColors) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog._selectedColor : null;
    }

    private void SaturationValue_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _draggingSaturationValue = true;
        SaturationValueArea.CaptureMouse();
        UpdateSaturationValue(e.GetPosition(SaturationValueArea));
    }

    private void SaturationValue_MouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingSaturationValue && e.LeftButton == MouseButtonState.Pressed)
            UpdateSaturationValue(e.GetPosition(SaturationValueArea));
    }

    private void SaturationValue_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_draggingSaturationValue)
            return;
        UpdateSaturationValue(e.GetPosition(SaturationValueArea));
        _draggingSaturationValue = false;
        SaturationValueArea.ReleaseMouseCapture();
    }

    private void Hue_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _draggingHue = true;
        HueArea.CaptureMouse();
        UpdateHue(e.GetPosition(HueArea));
    }

    private void Hue_MouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingHue && e.LeftButton == MouseButtonState.Pressed)
            UpdateHue(e.GetPosition(HueArea));
    }

    private void Hue_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_draggingHue)
            return;
        UpdateHue(e.GetPosition(HueArea));
        _draggingHue = false;
        HueArea.ReleaseMouseCapture();
    }

    private void UpdateSaturationValue(Point point)
    {
        var width = Math.Max(1, SaturationValueArea.ActualWidth);
        var height = Math.Max(1, SaturationValueArea.ActualHeight);
        _saturation = Math.Clamp(point.X / width, 0, 1);
        _value = 1 - Math.Clamp(point.Y / height, 0, 1);
        ApplyHsvSelection();
    }

    private void UpdateHue(Point point)
    {
        var height = Math.Max(1, HueArea.ActualHeight);
        _hue = Math.Clamp(point.Y / height, 0, 1) * 359.999;
        ApplyHsvSelection();
    }

    private void PaletteColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string color })
            SetColor(color);
    }

    private void SavedColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string color })
            SetColor(color);
    }

    private void SavedColor_Remove(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Button { Tag: string color })
            return;
        var existing = _savedColors.FirstOrDefault(item =>
            string.Equals(item, color, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
            _savedColors.Remove(existing);
        RefreshSavedColors();
        e.Handled = true;
    }

    private void SaveColor_Click(object sender, RoutedEventArgs e)
    {
        var existing = _savedColors.FirstOrDefault(item =>
            string.Equals(item, _selectedColor, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
            _savedColors.Remove(existing);
        _savedColors.Insert(0, _selectedColor);
        while (_savedColors.Count > 30)
            _savedColors.RemoveAt(_savedColors.Count - 1);
        RefreshSavedColors();
    }

    private void RefreshSavedColors()
    {
        SavedColorsPanel.Children.Clear();
        if (_savedColors.Count == 0)
        {
            SavedColorsPanel.Children.Add(new TextBlock
            {
                Text = "Здесь появятся ваши любимые оттенки",
                Foreground = new SolidColorBrush(Color.FromArgb(125, 255, 255, 255)),
                FontSize = 11,
                Margin = new Thickness(2, 7, 0, 0)
            });
            return;
        }

        foreach (var color in _savedColors.ToList())
        {
            if (!TryNormalizeColor(color, out var normalized))
                continue;
            var button = new Button
            {
                Style = (Style)FindResource("ColorSwatchButton"),
                Background = CreateBrush(normalized),
                Tag = normalized,
                ToolTip = $"{normalized}\nЛКМ — выбрать, ПКМ — удалить"
            };
            button.Click += SavedColor_Click;
            button.PreviewMouseRightButtonUp += SavedColor_Remove;
            SavedColorsPanel.Children.Add(button);
        }
    }

    private void HexTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updatingText)
            return;

        if (!TryNormalizeColor(HexTextBox.Text, out var color))
        {
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        _selectedColor = color;
        var rgb = ParseColor(color);
        RgbToHsv(rgb, out _hue, out _saturation, out _value);
        UpdateVisuals(false);
        ErrorText.Visibility = Visibility.Collapsed;
    }

    private void SetColor(string value)
    {
        if (!TryNormalizeColor(value, out var color))
            color = "#FFFFFF";

        _selectedColor = color;
        RgbToHsv(ParseColor(color), out _hue, out _saturation, out _value);
        UpdateVisuals(true);
        ErrorText.Visibility = Visibility.Collapsed;
    }

    private void ApplyHsvSelection()
    {
        var color = HsvToColor(_hue, _saturation, _value);
        _selectedColor = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        UpdateVisuals(true);
        ErrorText.Visibility = Visibility.Collapsed;
    }

    private void UpdateVisuals(bool updateText)
    {
        var selected = ParseColor(_selectedColor);
        PreviewSwatch.Background = CreateBrush(selected);
        HueSurface.Fill = CreateBrush(HsvToColor(_hue, 1, 1));
        RgbText.Text = $"R {selected.R,3}   G {selected.G,3}   B {selected.B,3}";

        if (updateText)
        {
            _updatingText = true;
            HexTextBox.Text = _selectedColor;
            HexTextBox.CaretIndex = HexTextBox.Text.Length;
            _updatingText = false;
        }
        UpdateSelectors();
    }

    private void UpdateSelectors()
    {
        if (SaturationValueArea.ActualWidth > 0 && SaturationValueArea.ActualHeight > 0)
        {
            Canvas.SetLeft(SaturationValueThumb,
                Math.Clamp(_saturation * SaturationValueArea.ActualWidth - SaturationValueThumb.Width / 2,
                    -SaturationValueThumb.Width / 2,
                    SaturationValueArea.ActualWidth - SaturationValueThumb.Width / 2));
            Canvas.SetTop(SaturationValueThumb,
                Math.Clamp((1 - _value) * SaturationValueArea.ActualHeight - SaturationValueThumb.Height / 2,
                    -SaturationValueThumb.Height / 2,
                    SaturationValueArea.ActualHeight - SaturationValueThumb.Height / 2));
        }

        if (HueArea.ActualHeight > 0)
        {
            Canvas.SetTop(HueThumb,
                Math.Clamp((_hue / 360) * HueArea.ActualHeight - HueThumb.Height / 2,
                    -HueThumb.Height / 2,
                    HueArea.ActualHeight - HueThumb.Height / 2));
        }
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (!TryNormalizeColor(HexTextBox.Text, out var color))
        {
            ErrorText.Visibility = Visibility.Visible;
            HexTextBox.Focus();
            return;
        }

        _selectedColor = color;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    public static bool TryNormalizeColor(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        try
        {
            var converted = ColorConverter.ConvertFromString(value.Trim());
            if (converted is not Color color)
                return false;
            normalized = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static Color ParseColor(string value)
        => (Color)ColorConverter.ConvertFromString(value)!;

    private static SolidColorBrush CreateBrush(string value)
        => CreateBrush(ParseColor(value));

    private static SolidColorBrush CreateBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static void RgbToHsv(Color color, out double hue, out double saturation, out double value)
    {
        var r = color.R / 255d;
        var g = color.G / 255d;
        var b = color.B / 255d;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;

        hue = 0;
        if (delta > 0)
        {
            if (Math.Abs(max - r) < 0.0001)
                hue = 60 * (((g - b) / delta) % 6);
            else if (Math.Abs(max - g) < 0.0001)
                hue = 60 * (((b - r) / delta) + 2);
            else
                hue = 60 * (((r - g) / delta) + 4);
        }
        if (hue < 0)
            hue += 360;

        saturation = max <= 0 ? 0 : delta / max;
        value = max;
    }

    private static Color HsvToColor(double hue, double saturation, double value)
    {
        hue = (hue % 360 + 360) % 360;
        saturation = Math.Clamp(saturation, 0, 1);
        value = Math.Clamp(value, 0, 1);
        var chroma = value * saturation;
        var x = chroma * (1 - Math.Abs((hue / 60) % 2 - 1));
        var m = value - chroma;

        var (r, g, b) = hue switch
        {
            < 60 => (chroma, x, 0d),
            < 120 => (x, chroma, 0d),
            < 180 => (0d, chroma, x),
            < 240 => (0d, x, chroma),
            < 300 => (x, 0d, chroma),
            _ => (chroma, 0d, x)
        };

        return Color.FromRgb(
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }
}
