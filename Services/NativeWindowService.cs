using System.Runtime.InteropServices;

namespace AppLauncher.Services;

public static class NativeWindowService
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaSystemBackdropType = 38;

    public const int HotkeyModifierAlt = 0x0001;
    public const int HotkeyModifierControl = 0x0002;
    public const int HotkeyModifierShift = 0x0004;
    public const int HotkeyModifierWindows = 0x0008;
    private const int HotkeyModifierNoRepeat = 0x4000;

    public static void ApplyModernWindowStyle(IntPtr handle, bool darkMode)
    {
        if (handle == IntPtr.Zero)
            return;

        try
        {
            var dark = darkMode ? 1 : 0;
            DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));

            var rounded = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref rounded, sizeof(int));

            // Непрозрачный DWM-фон сохраняет ClearType и чёткий текст.
            var solidBackdrop = 1; // DWMSBT_NONE
            DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref solidBackdrop, sizeof(int));
        }
        catch
        {
            // Windows 10 и старые сборки просто используют непрозрачный фон.
        }
    }

    public static bool RegisterGlobalHotkey(IntPtr handle, int id, int modifiers, int virtualKey)
        => handle != IntPtr.Zero
           && virtualKey > 0
           && RegisterHotKey(handle, id, modifiers | HotkeyModifierNoRepeat, virtualKey);

    public static void UnregisterGlobalHotkey(IntPtr handle, int id)
    {
        if (handle != IntPtr.Zero)
            UnregisterHotKey(handle, id);
    }

    public static bool IsForegroundWindowOwnedByCurrentProcess()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
            return false;

        GetWindowThreadProcessId(foreground, out var processId);
        return processId == Environment.ProcessId;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int dwAttribute,
        ref int pvAttribute,
        int cbAttribute);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int processId);
}
