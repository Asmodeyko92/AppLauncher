using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using AppLauncher.Models;

namespace AppLauncher.Services;

public static class LaunchService
{
    public static void Launch(LauncherItem item)
    {
        if (item.IsFolder)
            return;

        if (string.IsNullOrWhiteSpace(item.Path))
            throw new InvalidOperationException("Для этого элемента не указан путь.");

        LaunchPath(item.Path, item.Arguments, item.RunAsAdministrator);
    }

    public static void LaunchPath(string path, string? arguments = null, bool runAsAdministrator = false)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("Не указан путь для запуска.");

        var expandedPath = Environment.ExpandEnvironmentVariables(path);
        var startInfo = new ProcessStartInfo
        {
            FileName = expandedPath,
            Arguments = arguments ?? string.Empty,
            UseShellExecute = true,
            WorkingDirectory = ResolveWorkingDirectory(expandedPath)
        };

        if (runAsAdministrator)
            startInfo.Verb = "runas";

        try
        {
            Process.Start(startInfo);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // Пользователь отменил запрос UAC — это не ошибка лаунчера.
        }
    }

    private static string ResolveWorkingDirectory(string path)
    {
        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(path);
            if (File.Exists(expanded))
                return System.IO.Path.GetDirectoryName(expanded) ?? string.Empty;
        }
        catch
        {
            // Для URI и shell-команд рабочая папка не нужна.
        }

        return string.Empty;
    }
}
