namespace AppLauncher.Models;

public sealed class PluginLibraryEntry
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string DirectoryPath { get; set; } = string.Empty;
    public string ContentFile { get; set; } = string.Empty;
    public int DefaultRows { get; set; } = 2;
    public int DefaultColumns { get; set; } = 2;
    public bool IsEnabled { get; set; } = true;
    public bool IsValid { get; set; }
    public bool IsInstalled { get; set; }
}
