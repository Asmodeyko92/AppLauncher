using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using AppLauncher.Models;
using AppLauncher.Services;

namespace AppLauncher.Dialogs;

public partial class WidgetGalleryDialog : Window
{
    private WidgetGalleryDialog(Window owner, IEnumerable<PluginLibraryEntry> plugins)
    {
        InitializeComponent();
        Owner = owner;
        Plugins = new ObservableCollection<PluginLibraryEntry>(plugins
            .Where(plugin => plugin.IsEnabled && plugin.IsInstalled && plugin.IsValid));
        DataContext = this;
        ApplyOwnerTheme(owner);
        SourceInitialized += (_, _) => NativeWindowService.ApplyModernWindowStyle(
            new WindowInteropHelper(this).Handle,
            IsDark(owner));
    }

    public ObservableCollection<PluginLibraryEntry> Plugins { get; }
    public PluginLibraryEntry? SelectedPlugin { get; private set; }
    public bool CreateBlank { get; private set; }

    public static bool TryPick(
        Window owner,
        IEnumerable<PluginLibraryEntry> plugins,
        out PluginLibraryEntry? plugin,
        out bool createBlank)
    {
        var dialog = new WidgetGalleryDialog(owner, plugins);
        var accepted = dialog.ShowDialog() == true;
        plugin = accepted ? dialog.SelectedPlugin : null;
        createBlank = accepted && dialog.CreateBlank;
        return accepted;
    }

    private void CreateBlank_Click(object sender, RoutedEventArgs e)
    {
        CreateBlank = true;
        DialogResult = true;
    }

    private void CreatePlugin_Click(object sender, RoutedEventArgs e)
    {
        SelectedPlugin = (sender as FrameworkElement)?.DataContext as PluginLibraryEntry;
        if (SelectedPlugin is not null)
            DialogResult = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void ApplyOwnerTheme(Window owner)
    {
        Resources["GallerySurfaceBrush"] = GetBrush(owner, "PanelBrush", "#181B24");
        Resources["GalleryCardBrush"] = GetBrush(owner, "TileBrush", "#20232D");
        Resources["GalleryTextBrush"] = GetBrush(owner, "TextBrush", "#FFFFFF");
        Resources["GalleryMutedBrush"] = GetBrush(owner, "MutedTextBrush", "#A8FFFFFF");
        Resources["GalleryBorderBrush"] = GetBrush(owner, "ColorSwatchBorderBrush", "#30FFFFFF");
        Resources["GalleryAccentBrush"] = GetBrush(owner, "AccentSolidBrush", "#7569FF");
    }

    private static Brush GetBrush(Window owner, string key, string fallback)
        => owner.Resources[key] as Brush
           ?? (Brush)new BrushConverter().ConvertFromString(fallback)!;

    private static bool IsDark(Window owner)
    {
        var brush = GetBrush(owner, "PanelBrush", "#181B24") as SolidColorBrush;
        return brush is null || (0.2126 * brush.Color.R + 0.7152 * brush.Color.G + 0.0722 * brush.Color.B) / 255 < 0.5;
    }
}
