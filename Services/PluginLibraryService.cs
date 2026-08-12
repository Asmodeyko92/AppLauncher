using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using AppLauncher.Models;

namespace AppLauncher.Services;

public static class PluginLibraryService
{
    public static readonly string LibraryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AppLauncher",
        "Plugins");

    public static void Refresh(ObservableCollection<PluginLibraryEntry> library)
    {
        Directory.CreateDirectory(LibraryPath);
        var enabledById = library
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Id))
            .GroupBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last().IsEnabled, StringComparer.OrdinalIgnoreCase);
        var discovered = Directory.EnumerateFiles(LibraryPath, "plugin.json", SearchOption.AllDirectories)
            .Select(path => ReadManifest(path, enabledById))
            .Where(entry => entry is not null)
            .Cast<PluginLibraryEntry>()
            .OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        library.Clear();
        foreach (var entry in discovered)
            library.Add(entry);
    }

    private static PluginLibraryEntry? ReadManifest(string manifestPath, IReadOnlyDictionary<string, bool> enabledById)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(manifestPath));
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id) || string.IsNullOrWhiteSpace(manifest.Name))
                return null;

            return new PluginLibraryEntry
            {
                Id = manifest.Id.Trim(),
                Name = manifest.Name.Trim(),
                Version = string.IsNullOrWhiteSpace(manifest.Version) ? "0.1.0" : manifest.Version.Trim(),
                Description = manifest.Description?.Trim() ?? "Плагин AppLauncher",
                DirectoryPath = Path.GetDirectoryName(manifestPath)!,
                IsEnabled = !enabledById.TryGetValue(manifest.Id, out var enabled) || enabled,
                IsValid = true
            };
        }
        catch (JsonException)
        {
            return new PluginLibraryEntry
            {
                Id = Path.GetDirectoryName(manifestPath) ?? manifestPath,
                Name = Path.GetFileName(Path.GetDirectoryName(manifestPath)) ?? "Некорректный плагин",
                Version = "—",
                Description = "Не удалось прочитать plugin.json",
                DirectoryPath = Path.GetDirectoryName(manifestPath)!,
                IsEnabled = false,
                IsValid = false
            };
        }
    }

    private sealed class PluginManifest
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? Version { get; set; }
        public string? Description { get; set; }
    }
}
