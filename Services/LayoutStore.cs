using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AppLauncher.Models;

namespace AppLauncher.Services;

public static class LayoutStore
{
    private static readonly string AppDirectory = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AppLauncher");

    public static readonly string DataPath = System.IO.Path.Combine(AppDirectory, "layout.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static LauncherSettings Load()
    {
        try
        {
            if (File.Exists(DataPath))
            {
                var json = File.ReadAllText(DataPath);
                var loaded = JsonSerializer.Deserialize<LauncherSettings>(json, JsonOptions);
                if (loaded is not null)
                    return loaded;
            }
        }
        catch
        {
            TryBackupBrokenFile();
        }

        return CreateFirstRunLayout();
    }

    public static void Save(LauncherSettings settings)
    {
        Directory.CreateDirectory(AppDirectory);
        var temporaryPath = DataPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporaryPath, DataPath, true);
    }

    private static LauncherSettings CreateFirstRunLayout()
    {
        var settings = new LauncherSettings();
        settings.Items.Add(new LauncherItem
        {
            Name = "Проводник",
            Path = "explorer.exe"
        });
        settings.Items.Add(new LauncherItem
        {
            Name = "Параметры",
            Path = "ms-settings:"
        });
        return settings;
    }

    private static void TryBackupBrokenFile()
    {
        try
        {
            if (!File.Exists(DataPath))
                return;
            var backup = DataPath + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            File.Copy(DataPath, backup, true);
        }
        catch
        {
            // Ошибка резервного копирования не должна мешать запуску.
        }
    }
}
