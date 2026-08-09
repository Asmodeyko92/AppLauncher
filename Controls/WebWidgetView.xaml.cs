using System.ComponentModel;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media.Imaging;
using AppLauncher.Models;
using Microsoft.Web.WebView2.Core;

namespace AppLauncher.Controls;

public partial class WebWidgetView : UserControl
{
    private static readonly Lazy<Task<CoreWebView2Environment>> SharedEnvironment = new(CreateEnvironmentAsync);
    private static readonly ConcurrentDictionary<string, BitmapSource> SnapshotCache = new();
    private static readonly ConcurrentDictionary<string, string> TextCache = new();
    private readonly DispatcherTimer _navigateTimer;
    private LauncherItem? _item;
    private bool _initialized;
    private bool _disposed;
    private int _navigationGeneration;
    private int _captureGeneration;
    private string? _lastRenderedKey;

    public event EventHandler? ViewportRendered;

    public bool IsViewportCurrent(LauncherItem item)
        => string.Equals(_lastRenderedKey, GetSnapshotKey(item), StringComparison.Ordinal);

    public WebWidgetView()
    {
        InitializeComponent();
        Browser.DefaultBackgroundColor = System.Drawing.Color.Transparent;
        // Небольшой debounce оставляем для редактора, но 450 мс визуально
        // ощущались как отдельная задержка после смены страницы.
        _navigateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(110) };
        _navigateTimer.Tick += (_, _) =>
        {
            _navigateTimer.Stop();
            _ = NavigateAsync();
        };
        DataContextChanged += WebWidgetView_DataContextChanged;
        Loaded += (_, _) =>
        {
            AttachItem(DataContext as LauncherItem);
            ScheduleNavigation();
        };
        Unloaded += WebWidgetView_Unloaded;
    }

    private static Task<CoreWebView2Environment> CreateEnvironmentAsync()
    {
        var dataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AppLauncher",
            "WebView2");
        Directory.CreateDirectory(dataFolder);
        return CoreWebView2Environment.CreateAsync(userDataFolder: dataFolder);
    }

    private void WebWidgetView_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        => AttachItem(e.NewValue as LauncherItem);

    private void AttachItem(LauncherItem? item)
    {
        if (ReferenceEquals(_item, item) || _disposed)
            return;

        if (_item is not null)
            _item.PropertyChanged -= Item_PropertyChanged;
        _item = item;
        if (_item is not null)
            _item.PropertyChanged += Item_PropertyChanged;

        SnapshotImage.Opacity = _item?.WidgetContentOpacity ?? 1;
        LocalImage.Opacity = _item?.WidgetContentOpacity ?? 1;
        LocalTextSurface.Opacity = _item?.WidgetContentOpacity ?? 1;
        UpdateSnapshotViewportTransform();
        if (IsLoaded)
            ScheduleNavigation();
    }

    private void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_item is null)
            return;

        if (e.PropertyName is nameof(LauncherItem.WidgetUrl)
            or nameof(LauncherItem.WidgetContentPath)
            or nameof(LauncherItem.WidgetContentType))
        {
            ScheduleNavigation();
            return;
        }

        if (e.PropertyName == nameof(LauncherItem.WidgetContentOpacity))
        {
            SnapshotImage.Opacity = _item.WidgetContentOpacity;
            LocalImage.Opacity = _item.WidgetContentOpacity;
            LocalTextSurface.Opacity = _item.WidgetContentOpacity;
        }

        if (e.PropertyName is nameof(LauncherItem.WidgetScale)
            or nameof(LauncherItem.WidgetOffsetX)
            or nameof(LauncherItem.WidgetOffsetY)
            or nameof(LauncherItem.WidgetCssSelector))
        {
            if (_item.WidgetContentType == WidgetContentKind.WebPage)
            {
                _ = ApplyViewportAsync();
            }
            else
            {
                UpdateSnapshotViewportTransform();
                _lastRenderedKey = GetSnapshotKey(_item);
                Dispatcher.BeginInvoke(DispatcherPriority.Render,
                    new Action(() => ViewportRendered?.Invoke(this, EventArgs.Empty)));
            }
        }
    }

    private void ScheduleNavigation()
    {
        _navigateTimer.Stop();
        _lastRenderedKey = null;
        Browser.Visibility = Visibility.Collapsed;
        SnapshotImage.Visibility = Visibility.Collapsed;
        LocalImage.Visibility = Visibility.Collapsed;
        LocalTextSurface.Visibility = Visibility.Collapsed;
        StatusSurface.Visibility = Visibility.Collapsed;

        if (_item?.HasWidgetDocumentContent != true)
            return;

        if (_item.WidgetContentType == WidgetContentKind.TextFile)
        {
            if (TryShowCachedText())
                return;
            _ = LoadTextFileAsync(_item.WidgetContentPath);
            return;
        }

        if (_item.WidgetContentType == WidgetContentKind.ImageFile)
        {
            if (TryShowCachedSnapshot())
                return;
            _ = LoadImageFileAsync(_item.WidgetContentPath);
            return;
        }

        Browser.Visibility = Visibility.Visible;
        if (TryShowCachedSnapshot())
        {
            Browser.Visibility = Visibility.Collapsed;
            StatusSurface.Visibility = Visibility.Collapsed;
        }
        else
        {
            SnapshotImage.Visibility = Visibility.Collapsed;
            ShowStatus("Загрузка страницы…", "\uE774");
        }
        _navigateTimer.Start();
    }

    private async Task EnsureInitializedAsync()
    {
        if (_initialized || _disposed)
            return;

        var environment = await SharedEnvironment.Value;
        await Browser.EnsureCoreWebView2Async(environment);
        if (_disposed)
            return;

        Browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        Browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
        Browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
        Browser.CoreWebView2.Settings.IsZoomControlEnabled = false;
        Browser.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;
        Browser.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled = false;
        Browser.CoreWebView2.NewWindowRequested += (_, args) => args.Handled = true;
        Browser.CoreWebView2.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
        Browser.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;
        _initialized = true;
    }

    private async Task NavigateAsync()
    {
        if (_item?.HasWidgetDocumentContent != true || _disposed)
            return;

        var generation = ++_navigationGeneration;
        try
        {
            await EnsureInitializedAsync();
            if (_disposed || generation != _navigationGeneration || _item is null)
                return;

            if (!TryGetNavigationUri(_item, out var uri))
            {
                Browser.Visibility = Visibility.Collapsed;
                ShowStatus(
                    _item.WidgetContentType == WidgetContentKind.PdfFile
                        ? "PDF-файл не найден"
                        : "Проверьте адрес страницы",
                    "\uEA39");
                return;
            }

            Browser.Source = uri;
        }
        catch (Exception ex)
        {
            Browser.Visibility = Visibility.Collapsed;
            ShowStatus(
                ex is WebView2RuntimeNotFoundException
                    ? "Установите Microsoft Edge WebView2 Runtime"
                    : "Не удалось открыть страницу",
                "\uEA39");
        }
    }

    private async void CoreWebView2_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            Browser.Visibility = Visibility.Collapsed;
            ShowStatus("Страница не загрузилась", "\uEA39");
            return;
        }

        StatusSurface.Visibility = Visibility.Collapsed;
        if (_item?.WidgetContentType == WidgetContentKind.WebPage)
            await ApplyViewportAsync();
        else
            await CaptureSnapshotAsync();
    }

    private async Task ApplyViewportAsync()
    {
        if (!_initialized || _disposed || _item is null || Browser.CoreWebView2 is null)
            return;

        try
        {
            var result = await Browser.CoreWebView2.ExecuteScriptAsync(BuildViewportScript(_item));
            if (result == "\"selector-not-found\"")
            {
                Browser.Visibility = Visibility.Collapsed;
                ShowStatus("Указанный блок не найден", "\uE721");
            }
            else
            {
                StatusSurface.Visibility = Visibility.Collapsed;
                await CaptureSnapshotAsync();
            }
        }
        catch
        {
            // Страница могла начать новую навигацию между проверкой и вызовом.
        }
    }

    private async Task CaptureSnapshotAsync()
    {
        if (_item is null || Browser.CoreWebView2 is null || _disposed)
            return;

        var generation = ++_captureGeneration;
        try
        {
            await Task.Delay(180);
            using var stream = new MemoryStream();
            await Browser.CoreWebView2.CapturePreviewAsync(
                CoreWebView2CapturePreviewImageFormat.Png,
                stream);
            if (_disposed || generation != _captureGeneration || _item is null)
                return;

            stream.Position = 0;
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();

            StoreSnapshot(_item, bitmap);
            SnapshotImage.Source = bitmap;
            SnapshotImage.Opacity = _item.WidgetContentOpacity;
            UpdateSnapshotViewportTransform();
            SnapshotImage.Visibility = Visibility.Visible;
            Browser.Visibility = Visibility.Collapsed;
            StatusSurface.Visibility = Visibility.Collapsed;
            _lastRenderedKey = GetSnapshotKey(_item);
            ViewportRendered?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // Снимок — оптимизация анимации; сама страница остаётся рабочей и без него.
        }
    }

    private bool TryShowCachedSnapshot()
    {
        if (_item is null || !SnapshotCache.TryGetValue(GetSnapshotKey(_item), out var bitmap))
            return false;

        SnapshotImage.Source = bitmap;
        SnapshotImage.Opacity = _item.WidgetContentOpacity;
        UpdateSnapshotViewportTransform();
        SnapshotImage.Visibility = Visibility.Visible;
        Browser.Visibility = Visibility.Collapsed;
        _lastRenderedKey = GetSnapshotKey(_item);
        ViewportRendered?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private bool TryShowCachedText()
    {
        if (_item is null || !TextCache.TryGetValue(GetSnapshotKey(_item), out var text))
            return false;

        LocalTextBlock.Text = text;
        LocalTextSurface.Opacity = _item.WidgetContentOpacity;
        LocalTextSurface.Visibility = Visibility.Visible;
        StatusSurface.Visibility = Visibility.Collapsed;
        _lastRenderedKey = GetSnapshotKey(_item);
        ViewportRendered?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private static void StoreSnapshot(LauncherItem item, BitmapSource bitmap)
    {
        var currentKey = GetSnapshotKey(item);
        var itemPrefix = item.Id + "|";
        foreach (var oldKey in SnapshotCache.Keys.Where(key => key.StartsWith(itemPrefix, StringComparison.Ordinal)
                                                               && !string.Equals(key, currentKey, StringComparison.Ordinal)))
            SnapshotCache.TryRemove(oldKey, out _);
        SnapshotCache[currentKey] = bitmap;
    }

    private static void StoreText(LauncherItem item, string text)
    {
        var currentKey = GetSnapshotKey(item);
        var itemPrefix = item.Id + "|";
        foreach (var oldKey in TextCache.Keys.Where(key => key.StartsWith(itemPrefix, StringComparison.Ordinal)
                                                           && !string.Equals(key, currentKey, StringComparison.Ordinal)))
            TextCache.TryRemove(oldKey, out _);
        TextCache[currentKey] = text;
    }

    private void UpdateSnapshotViewportTransform()
    {
        var useOuterTransform = _item is not null
                                && _item.WidgetContentType != WidgetContentKind.WebPage;
        SnapshotScaleTransform.ScaleX = useOuterTransform ? _item!.WidgetScale : 1;
        SnapshotScaleTransform.ScaleY = useOuterTransform ? _item!.WidgetScale : 1;
        SnapshotOffsetTransform.X = useOuterTransform ? _item!.WidgetOffsetX : 0;
        SnapshotOffsetTransform.Y = useOuterTransform ? _item!.WidgetOffsetY : 0;
    }

    private static string GetSnapshotKey(LauncherItem item)
        => string.Join("|",
            item.Id,
            item.WidgetContentType,
            item.WidgetUrl,
            item.WidgetContentPath,
            GetFileVersion(item.WidgetContentPath),
            item.WidgetCssSelector,
            item.WidgetScale.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
            item.WidgetOffsetX.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
            item.WidgetOffsetY.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));

    private static string BuildViewportScript(LauncherItem item)
    {
        var selector = JsonSerializer.Serialize(item.WidgetCssSelector ?? string.Empty);
        var scale = item.WidgetScale.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        var offsetX = item.WidgetOffsetX.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        var offsetY = item.WidgetOffsetY.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        return $$"""
            (() => {
                const selector = {{selector}};
                const scale = {{scale}};
                const offsetX = {{offsetX}};
                const offsetY = {{offsetY}};
                const apply = () => {
                    const root = document.documentElement;
                    const body = document.body;
                    if (!root || !body) return 'not-ready';
                    body.style.transform = 'none';
                    body.style.transformOrigin = '0 0';
                    body.style.width = '';
                    void body.offsetHeight;
                    let target = body;
                    if (selector) {
                        try { target = document.querySelector(selector); }
                        catch { return 'selector-not-found'; }
                        if (!target) return 'selector-not-found';
                    }
                    const rect = target === body
                        ? { left: 0, top: 0 }
                        : target.getBoundingClientRect();
                    root.style.overflow = 'hidden';
                    body.style.overflow = 'hidden';
                    body.style.transformOrigin = '0 0';
                    body.style.transform = `translate(${offsetX - rect.left * scale}px, ${offsetY - rect.top * scale}px) scale(${scale})`;
                    body.style.width = `${100 / scale}%`;
                    return 'ok';
                };
                const result = apply();
                window.setTimeout(apply, 500);
                window.setTimeout(apply, 1500);
                return result;
            })();
            """;
    }

    private static bool TryGetNavigationUri(LauncherItem item, out Uri uri)
    {
        if (item.WidgetContentType != WidgetContentKind.WebPage)
        {
            var path = ExpandPath(item.WidgetContentPath);
            if (!File.Exists(path))
            {
                uri = null!;
                return false;
            }

            uri = new Uri(Path.GetFullPath(path), UriKind.Absolute);
            return true;
        }

        var text = item.WidgetUrl.Trim();
        if (!text.Contains("://", StringComparison.Ordinal))
            text = "https://" + text;
        return Uri.TryCreate(text, UriKind.Absolute, out uri!)
               && uri.Scheme is "http" or "https";
    }

    private async Task LoadTextFileAsync(string value)
    {
        var generation = ++_navigationGeneration;
        var path = ExpandPath(value);
        if (!File.Exists(path))
        {
            ShowStatus("Текстовый файл не найден", "\uEA39");
            return;
        }

        try
        {
            const int maxCharacters = 250_000;
            using var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true);
            var buffer = new char[maxCharacters + 1];
            var length = await reader.ReadBlockAsync(buffer, 0, buffer.Length);
            if (_disposed || generation != _navigationGeneration)
                return;

            var text = new string(buffer, 0, Math.Min(length, maxCharacters))
                       + (length > maxCharacters ? "\n\n… файл показан не полностью" : string.Empty);
            LocalTextBlock.Text = text;
            if (_item is not null)
                StoreText(_item, text);
            LocalTextSurface.Opacity = _item?.WidgetContentOpacity ?? 1;
            LocalTextSurface.Visibility = Visibility.Visible;
            StatusSurface.Visibility = Visibility.Collapsed;
            if (_item is not null)
                _lastRenderedKey = GetSnapshotKey(_item);
            ViewportRendered?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            ShowStatus("Не удалось прочитать текстовый файл", "\uEA39");
        }
    }

    private async Task LoadImageFileAsync(string value)
    {
        var generation = ++_navigationGeneration;
        var path = ExpandPath(value);
        if (!File.Exists(path))
        {
            ShowStatus("Изображение не найдено", "\uEA39");
            return;
        }

        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            if (_disposed || generation != _navigationGeneration)
                return;

            if (_item is not null)
                StoreSnapshot(_item, bitmap);
            LocalImage.Source = bitmap;
            LocalImage.Opacity = _item?.WidgetContentOpacity ?? 1;
            LocalImage.Visibility = Visibility.Visible;
            StatusSurface.Visibility = Visibility.Collapsed;
            if (_item is not null)
                _lastRenderedKey = GetSnapshotKey(_item);
            ViewportRendered?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // SVG, WebP и другие форматы, которые WPF не декодирует напрямую,
            // отдаём встроенному браузеру.
            Browser.Visibility = Visibility.Visible;
            _navigateTimer.Start();
        }
    }

    private static string ExpandPath(string value)
        => Environment.ExpandEnvironmentVariables(value?.Trim() ?? string.Empty);

    private static string GetFileVersion(string value)
    {
        try
        {
            var path = ExpandPath(value);
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks.ToString() : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private void ShowStatus(string message, string glyph)
    {
        StatusText.Text = message;
        StatusGlyph.Text = glyph;
        StatusSurface.Visibility = Visibility.Visible;
    }

    private void WebWidgetView_Unloaded(object sender, RoutedEventArgs e)
    {
        _navigateTimer.Stop();
        if (_item is not null)
            _item.PropertyChanged -= Item_PropertyChanged;
        _item = null;
        _disposed = true;
        try { Browser.Dispose(); } catch { }
    }
}
