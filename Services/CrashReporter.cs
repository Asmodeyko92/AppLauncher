using System.IO;
using System.Text;
using System.Windows;

namespace AppLauncher.Services;

public static class CrashReporter
{
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AppLauncher");

    public static string LogPath => Path.Combine(LogDirectory, "crash.log");

    public static void ReportAndShow(Exception exception, string title)
    {
        Write(exception, title);

        try
        {
            MessageBox.Show(
                $"AppLauncher не смог запуститься.\n\n{exception.Message}\n\n" +
                $"Подробности записаны в:\n{LogPath}",
                title,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch
        {
            // Даже если интерфейс Windows недоступен, журнал уже сохранён.
        }
    }

    public static void Write(Exception exception, string context)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var report = new StringBuilder()
                .AppendLine("AppLauncher crash report")
                .AppendLine($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}")
                .AppendLine($"Version: {typeof(CrashReporter).Assembly.GetName().Version}")
                .AppendLine($"Windows: {Environment.OSVersion}")
                .AppendLine($"Runtime: {Environment.Version}")
                .AppendLine($"Context: {context}")
                .AppendLine()
                .AppendLine(exception.ToString())
                .AppendLine(new string('-', 72))
                .ToString();

            File.AppendAllText(LogPath, report, Encoding.UTF8);
        }
        catch
        {
            // Система диагностики никогда не должна вызвать второй сбой.
        }
    }
}
