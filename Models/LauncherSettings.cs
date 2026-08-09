using System.Collections.ObjectModel;

namespace AppLauncher.Models;

public sealed class LauncherSettings
{
    public ObservableCollection<LauncherItem> Items { get; set; } = new();
    public bool HideAfterLaunch { get; set; } = true;
    public bool AlwaysOnTop { get; set; }
    public bool GlobalHotkeyEnabled { get; set; } = true;
    public int GlobalHotkeyModifiers { get; set; } = 0x0003;
    public int GlobalHotkeyVirtualKey { get; set; } = 0x20;
    public bool LightTheme { get; set; }
    public bool UseFolderThumbnails { get; set; }
    public double FolderThumbnailScale { get; set; } = 0.92;
    public double WindowWidth { get; set; } = 1000;
    public double WindowHeight { get; set; } = 700;
    public double WindowOpacity { get; set; } = 1.0;
    public double TileSize { get; set; } = 130;
    public double IconSize { get; set; } = 58;
    public double GridSpacing { get; set; } = 6;
    public string? WindowBackgroundColor { get; set; }
    public string? TileColor { get; set; }
    public ObservableCollection<string> SavedColors { get; set; } = new();
    public double? TileOpacity { get; set; }
    public bool TileShadowEnabled { get; set; }
    public double TileShadowOpacity { get; set; } = 0.30;
    public int VisualStyleVersion { get; set; }
}
