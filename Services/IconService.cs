using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AppLauncher.Models;

namespace AppLauncher.Services;

public static class IconService
{
    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiLargeIcon = 0x000000000;
    private const uint ShgfiUseFileAttributes = 0x000000010;
    private const uint FileAttributeNormal = 0x00000080;

    private static readonly ConcurrentDictionary<string, ImageSource> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource GetIcon(LauncherItem item)
    {
        if (item.HasCustomIcon)
        {
            var customSource = ResolveIconSource(item.IconPath);
            var customKey = "custom:" + customSource;
            return Cache.GetOrAdd(customKey, _ =>
                LoadImageFile(customSource)
                ?? ExtractHighResolutionShellImage(customSource)
                ?? ExtractLegacyShellIcon(customSource)
                ?? (item.IsFolder ? CreateFolderIcon() : CreateFallbackIcon(item.Name)));
        }

        if (item.IsFolder)
            return Cache.GetOrAdd("__folder__", _ => CreateFolderIcon());

        var source = ResolveIconSource(item.Path);
        var key = "app:" + (string.IsNullOrWhiteSpace(source) ? item.Name : source);
        return Cache.GetOrAdd(key, _ =>
            ExtractHighResolutionShellImage(source)
            ?? ExtractLegacyShellIcon(source)
            ?? CreateFallbackIcon(item.Name));
    }

    public static void Invalidate(LauncherItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.IconPath))
            Cache.TryRemove("custom:" + ResolveIconSource(item.IconPath), out _);
        if (!string.IsNullOrWhiteSpace(item.Path))
            Cache.TryRemove("app:" + ResolveIconSource(item.Path), out _);
    }

    public static void Invalidate(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        Cache.TryRemove("app:" + ResolveIconSource(path), out _);
        Cache.TryRemove("custom:" + ResolveIconSource(path), out _);
    }

    private static string ResolveIconSource(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        var expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (File.Exists(expanded) || Directory.Exists(expanded))
            return expanded;

        if (expanded.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase))
        {
            var systemSettings = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "ImmersiveControlPanel",
                "SystemSettings.exe");
            if (File.Exists(systemSettings))
                return systemSettings;
        }

        if (!System.IO.Path.IsPathRooted(expanded))
        {
            var windowsCandidate = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), expanded);
            if (File.Exists(windowsCandidate))
                return windowsCandidate;

            var buffer = new StringBuilder(2048);
            if (SearchPath(null, expanded, null, buffer.Capacity, buffer, out _) > 0)
                return buffer.ToString();
        }

        return expanded;
    }

    private static ImageSource? LoadImageFile(string path)
    {
        if (!File.Exists(path))
            return null;

        var extension = System.IO.Path.GetExtension(path);
        if (!extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".gif", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".ico", StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (!extension.Equals(".ico", StringComparison.OrdinalIgnoreCase))
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 256;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }

            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            var frame = decoder.Frames
                .OrderByDescending(candidate => candidate.PixelWidth * candidate.PixelHeight)
                .FirstOrDefault();
            if (frame is null)
                return null;
            if (frame.CanFreeze)
                frame.Freeze();
            return frame;
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource? ExtractHighResolutionShellImage(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || (!File.Exists(path) && !Directory.Exists(path)))
            return null;

        IShellItemImageFactory? factory = null;
        IntPtr bitmapHandle = IntPtr.Zero;
        try
        {
            var interfaceId = typeof(IShellItemImageFactory).GUID;
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref interfaceId, out factory);
            var result = factory.GetImage(
                new NativeSize { Width = 96, Height = 96 },
                ShellItemImageFlags.IconOnly | ShellItemImageFlags.BiggerSizeOk | ShellItemImageFlags.ScaleUp,
                out bitmapHandle);
            if (result != 0 || bitmapHandle == IntPtr.Zero)
                return null;

            var source = Imaging.CreateBitmapSourceFromHBitmap(
                bitmapHandle,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (bitmapHandle != IntPtr.Zero)
                DeleteObject(bitmapHandle);
            if (factory is not null && Marshal.IsComObject(factory))
                Marshal.FinalReleaseComObject(factory);
        }
    }

    private static ImageSource? ExtractLegacyShellIcon(string path)
    {
        try
        {
            var flags = ShgfiIcon | ShgfiLargeIcon;
            uint attributes = 0;

            if (!File.Exists(path) && !Directory.Exists(path))
            {
                flags |= ShgfiUseFileAttributes;
                attributes = FileAttributeNormal;
            }

            var result = SHGetFileInfo(
                path,
                attributes,
                out var info,
                (uint)Marshal.SizeOf<ShFileInfo>(),
                flags);
            if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
                return null;

            try
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(
                    info.hIcon,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                DestroyIcon(info.hIcon);
            }
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource CreateFolderIcon()
    {
        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
        {
            var shadowBrush = new SolidColorBrush(Color.FromArgb(70, 8, 10, 18));
            shadowBrush.Freeze();
            context.DrawRoundedRectangle(shadowBrush, null, new Rect(7, 25, 52, 34), 9, 9);

            var tabBrush = new LinearGradientBrush(
                Color.FromRgb(255, 222, 118),
                Color.FromRgb(247, 173, 45),
                new Point(0, 0),
                new Point(0, 1));
            tabBrush.Freeze();

            var tabGeometry = new StreamGeometry();
            using (var geometry = tabGeometry.Open())
            {
                geometry.BeginFigure(new Point(7, 18), true, true);
                geometry.LineTo(new Point(25, 18), true, false);
                geometry.LineTo(new Point(32, 24), true, false);
                geometry.LineTo(new Point(55, 24), true, false);
                geometry.LineTo(new Point(57, 34), true, false);
                geometry.LineTo(new Point(7, 34), true, false);
            }
            tabGeometry.Freeze();
            context.DrawGeometry(tabBrush, null, tabGeometry);

            var bodyBrush = new LinearGradientBrush(
                Color.FromRgb(255, 205, 75),
                Color.FromRgb(242, 151, 30),
                new Point(0, 0),
                new Point(0.9, 1));
            bodyBrush.Freeze();
            var bodyGeometry = new StreamGeometry();
            using (var geometry = bodyGeometry.Open())
            {
                geometry.BeginFigure(new Point(5, 27), true, true);
                geometry.LineTo(new Point(59, 27), true, false);
                geometry.LineTo(new Point(54, 57), true, false);
                geometry.LineTo(new Point(8, 57), true, false);
            }
            bodyGeometry.Freeze();
            context.DrawGeometry(bodyBrush, null, bodyGeometry);

            var panelBrush = new SolidColorBrush(Color.FromArgb(70, 122, 65, 12));
            panelBrush.Freeze();
            context.DrawRoundedRectangle(panelBrush, null, new Rect(15, 36, 33, 12), 4, 4);

            var lineBrush = new SolidColorBrush(Color.FromArgb(210, 255, 248, 220));
            lineBrush.Freeze();
            context.DrawRoundedRectangle(lineBrush, null, new Rect(20, 39, 23, 2), 1, 1);
            context.DrawRoundedRectangle(lineBrush, null, new Rect(20, 43, 17, 2), 1, 1);

            var highlightBrush = new SolidColorBrush(Color.FromArgb(95, 255, 255, 255));
            highlightBrush.Freeze();
            context.DrawRoundedRectangle(highlightBrush, null, new Rect(11, 29, 32, 2), 1, 1);
        }

        drawing.Freeze();
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }

    private static ImageSource CreateFallbackIcon(string name)
    {
        var firstLetter = string.IsNullOrWhiteSpace(name) ? "?" : name.Trim()[0].ToString().ToUpperInvariant();
        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
        {
            var background = new LinearGradientBrush(
                Color.FromRgb(93, 103, 255),
                Color.FromRgb(55, 128, 255),
                new Point(0, 0),
                new Point(1, 1));
            background.Freeze();
            context.DrawRoundedRectangle(background, null, new Rect(4, 4, 56, 56), 14, 14);

            var formattedText = new FormattedText(
                firstLetter,
                System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI Variable Display Semibold"),
                28,
                Brushes.White,
                1.0);
            context.DrawText(formattedText, new Point(32 - formattedText.Width / 2, 31 - formattedText.Height / 2));
        }
        drawing.Freeze();
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }

    [Flags]
    private enum ShellItemImageFlags : uint
    {
        BiggerSizeOk = 0x00000001,
        IconOnly = 0x00000004,
        ScaleUp = 0x00000100
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int Width;
        public int Height;
    }

    [ComImport]
    [Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, ShellItemImageFlags flags, out IntPtr bitmapHandle);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string path,
        IntPtr bindContext,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory imageFactory);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string path,
        uint fileAttributes,
        out ShFileInfo fileInfo,
        uint fileInfoSize,
        uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint SearchPath(
        string? path,
        string fileName,
        string? extension,
        int bufferLength,
        [Out] StringBuilder buffer,
        out IntPtr filePart);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr iconHandle);
}
