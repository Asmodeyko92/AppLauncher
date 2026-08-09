using AppLauncher.Models;

namespace AppLauncher.Services;

public static class WidgetFolderService
{
    private static readonly Dictionary<Guid, DateTime> LastScanUtc = new();
    private static readonly object ScanLock = new();
    private static readonly TimeSpan MinimumScanInterval = TimeSpan.FromSeconds(1.5);

    public static bool Refresh(LauncherItem widget, bool force = false)
    {
        if (!widget.IsWidgetLauncherGrid || string.IsNullOrWhiteSpace(widget.WidgetFolderPath))
            return false;

        // Measure/Arrange и анимации могут несколько раз подряд вызвать обновление
        // одного и того же виджета. Не блокируем UI повторным обходом диска.
        var now = DateTime.UtcNow;
        lock (ScanLock)
        {
            if (!force && LastScanUtc.TryGetValue(widget.Id, out var lastScan)
                && now - lastScan < MinimumScanInterval)
                return false;
            LastScanUtc[widget.Id] = now;
        }

        var folderPath = Environment.ExpandEnvironmentVariables(widget.WidgetFolderPath);
        if (!Directory.Exists(folderPath))
            return false;

        List<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(folderPath)
                .Where(IsVisibleEntry)
                .OrderBy(path => Directory.Exists(path) ? 0 : 1)
                .ThenBy(path => Path.GetFileName(path), StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch
        {
            return false;
        }

        if (widget.Children.Count == entries.Count
            && widget.Children.Select(item => Normalize(item.Path))
                .SequenceEqual(entries.Select(Normalize), StringComparer.OrdinalIgnoreCase))
            return false;

        var existing = widget.Children
            .Where(item => !string.IsNullOrWhiteSpace(item.Path))
            .GroupBy(item => Normalize(item.Path), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var refreshed = new List<LauncherItem>(entries.Count);
        foreach (var path in entries)
        {
            var key = Normalize(path);
            if (existing.TryGetValue(key, out var item))
            {
                item.Name = GetDisplayName(path);
                item.Path = path;
                item.Kind = LauncherItemKind.Application;
                refreshed.Add(item);
                continue;
            }

            refreshed.Add(new LauncherItem
            {
                Kind = LauncherItemKind.Application,
                Name = GetDisplayName(path),
                Path = path
            });
        }

        widget.Children.Clear();
        foreach (var item in refreshed)
            widget.Children.Add(item);
        return true;
    }

    private static bool IsVisibleEntry(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string GetDisplayName(string path)
    {
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (Directory.Exists(path))
            return name;
        var withoutExtension = Path.GetFileNameWithoutExtension(name);
        return string.IsNullOrWhiteSpace(withoutExtension) ? name : withoutExtension;
    }

    private static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.Trim();
        }
    }
}
