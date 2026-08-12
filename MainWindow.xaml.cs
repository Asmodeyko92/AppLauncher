using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AppLauncher.Controls;
using AppLauncher.Dialogs;
using AppLauncher.Models;
using AppLauncher.Services;
using Microsoft.Win32;

namespace AppLauncher;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private const string InternalDragFormat = WidgetLauncherGridView.LauncherItemDragFormat;
    private const int GlobalHotkeyId = 0x4150;
    private const int WmHotkey = 0x0312;
    private static readonly string[] SupportedExtensions =
    [
        ".exe", ".lnk", ".url", ".bat", ".cmd", ".com", ".msc", ".appref-ms"
    ];

    private readonly LauncherSettings _settings;
    private readonly DispatcherTimer _toastTimer;
    private readonly DispatcherTimer _settingsSaveTimer;
    private readonly List<LauncherItem> _availableItems = new();
    private readonly List<PageLayout> _pages = new();
    private readonly Dictionary<FrameworkElement, Transform> _reorderPreviewTransforms = new();
    private readonly TranslateTransform _pageTranslate = new();
    private readonly TranslateTransform _incomingPageTranslate = new();
    private readonly TranslateTransform _outgoingSnapshotTranslate = new();
    private readonly TranslateTransform _incomingSnapshotTranslate = new();
    private readonly Dictionary<int, BitmapSource> _pageSnapshotCache = new();
    private LauncherItem? _currentFolder;
    private LauncherItem? _flyoutFolder;
    private LauncherItem? _pressedItem;
    private LauncherItem? _activeDragItem;
    private Guid? _previewTargetId;
    private bool _previewInsertAfter;
    private int _reflowGeneration;
    private Point _dragStartPoint;
    private bool _dragStarted;
    private bool _initializing = true;
    private int _currentPage;
    private int _pageCount = 1;
    private int _pageAnimationGeneration;
    private int _pagePreloadGeneration;
    private int _pageSnapshotGeneration;
    private int _settingsAnimationGeneration;
    private int? _queuedPage;
    private bool _isPageAnimating;
    private int _preparedIncomingPage = -1;
    private int _lastPageDirection = 1;
    private bool _dragGhostActive;
    private bool _widgetLayoutChanged;
    private int _edgePageDirection;
    private Point _dragGhostTarget;
    private Point _dragGhostPosition;
    private Vector _dragGhostVelocity;
    private TimeSpan _lastDragRenderTime;
    private HwndSource? _windowSource;
    private IntPtr _windowHandle;
    private bool _globalHotkeyRegistered;
    private bool _isCapturingHotkey;
    private bool _allowApplicationExit;
    private int _deactivationGeneration;
    private int _dropPreviewGeneration;
    private DropPlacement? _dropPreviewPlacement;
    private int _dropPreviewColumnSpan;
    private int _dropPreviewRowSpan;
    private Guid? _dropPreviewSourceId;
    private int _dropPreviewHitX = int.MinValue;
    private int _dropPreviewHitY = int.MinValue;
    private int _dropPreviewPage = -1;

    public ObservableCollection<LauncherItem> DisplayItems { get; } = new();
    public ObservableCollection<LauncherItem> IncomingPageItems { get; } = new();
    public ObservableCollection<LauncherItem> FolderFlyoutItems { get; } = new();
    public double TileSize => _settings.TileSize;
    public double TileHeight => _settings.TileSize + 8;
    public double GridCellWidth => TileSize + _settings.GridSpacing;
    public double GridCellHeight => TileHeight + _settings.GridSpacing;
    public double IconSize => Math.Min(_settings.IconSize, Math.Max(36, _settings.TileSize - 42));
    public double IconPlateSize => IconSize + 14;
    public double FolderThumbnailSize => Math.Max(14, IconPlateSize / 2 * _settings.FolderThumbnailScale - 2);
    public bool UseFolderThumbnails => _settings.UseFolderThumbnails;
    public string AppVersion => typeof(MainWindow).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
        .FirstOrDefault()?.InformationalVersion ?? "1.9.13 Beta";
    public Thickness TileMargin => new(_settings.GridSpacing / 2);
    public Effect? TileShadowEffect { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly record struct DropPlacement(
        int Page, int Column, int Row, int ColumnSpan, int RowSpan);

    private sealed class PageLayout
    {
        private readonly bool[,] _occupied;

        public PageLayout(int pageIndex, int columns, int rows)
        {
            PageIndex = pageIndex;
            Columns = columns;
            Rows = rows;
            _occupied = new bool[rows, columns];
        }

        public int PageIndex { get; }
        public int Columns { get; }
        public int Rows { get; }
        public List<LauncherItem> Items { get; } = new();

        public bool TryAdd(LauncherItem item)
        {
            var (columnSpan, rowSpan) = GridPackingService.GetSpan(item);
            if (!GridPackingService.TryPlace(
                    _occupied, Columns, Rows, columnSpan, rowSpan,
                    out var column, out var row))
                return false;

            item.UpdateGridPlacement(column, row);
            Items.Add(item);
            return true;
        }

        public bool TryAddNearest(LauncherItem item, int preferredColumn, int preferredRow)
        {
            var (columnSpan, rowSpan) = GridPackingService.GetSpan(item);
            if (!GridPackingService.TryPlaceNearest(
                    _occupied, Columns, Rows, columnSpan, rowSpan,
                    preferredColumn, preferredRow,
                    out var column, out var row))
                return false;

            item.UpdateGridPlacement(column, row);
            Items.Add(item);
            return true;
        }
    }

    public MainWindow()
    {
        _settings = LayoutStore.Load();
        _settings.SavedColors ??= new ObservableCollection<string>();
        PreserveLegacyWidgetBackgrounds(_settings.Items);
        UpgradeVisualDefaults();
        InitializeComponent();
        DataContext = this;
        LauncherItems.RenderTransform = _pageTranslate;
        IncomingLauncherItems.RenderTransform = _incomingPageTranslate;
        OutgoingPageSnapshot.RenderTransform = _outgoingSnapshotTranslate;
        IncomingPageSnapshot.RenderTransform = _incomingSnapshotTranslate;
        SourceInitialized += MainWindow_SourceInitialized;

        var workArea = SystemParameters.WorkArea;
        WindowWidthSlider.Maximum = Math.Max(WindowWidthSlider.Minimum, Math.Floor(workArea.Width - 20));
        WindowHeightSlider.Maximum = Math.Max(WindowHeightSlider.Minimum, Math.Floor(workArea.Height - 20));

        _settings.WindowWidth = Math.Clamp(_settings.WindowWidth, MinWidth, WindowWidthSlider.Maximum);
        _settings.WindowHeight = Math.Clamp(_settings.WindowHeight, MinHeight, WindowHeightSlider.Maximum);
        _settings.TileSize = Math.Clamp(_settings.TileSize, TileSizeSlider.Minimum, TileSizeSlider.Maximum);
        _settings.IconSize = Math.Clamp(_settings.IconSize, IconSizeSlider.Minimum, IconSizeSlider.Maximum);
        _settings.GridSpacing = Math.Clamp(_settings.GridSpacing, GridSpacingSlider.Minimum, GridSpacingSlider.Maximum);
        _settings.FolderThumbnailScale = Math.Clamp(_settings.FolderThumbnailScale, 0.55, 1.0);
        _settings.TileShadowOpacity = Math.Clamp(_settings.TileShadowOpacity, 0.10, 0.70);
        _settings.WindowOpacity = Math.Clamp(_settings.WindowOpacity, 0.45, 1.0);

        Width = _settings.WindowWidth;
        Height = _settings.WindowHeight;

        HideAfterLaunchCheckBox.IsChecked = _settings.HideAfterLaunch;
        AlwaysOnTopCheckBox.IsChecked = _settings.AlwaysOnTop;
        GlobalHotkeyCheckBox.IsChecked = _settings.GlobalHotkeyEnabled;
        LightThemeCheckBox.IsChecked = _settings.LightTheme;
        VisualProfileCombo.SelectedValue = _settings.VisualProfile;
        FolderThumbnailsCheckBox.IsChecked = _settings.UseFolderThumbnails;
        OpenGroupsFullscreenCheckBox.IsChecked = _settings.OpenGroupsFullscreen;
        WidgetBackgroundMatchesTilesCheckBox.IsChecked = _settings.WidgetBackgroundMatchesTiles;
        FolderThumbnailSizeSlider.Value = _settings.FolderThumbnailScale * 100;
        TileShadowCheckBox.IsChecked = _settings.TileShadowEnabled;
        TileOpacitySlider.Value = EffectiveTileOpacity * 100;
        TileShadowOpacitySlider.Value = _settings.TileShadowOpacity * 100;
        IconSizeSlider.Value = _settings.IconSize;
        TileSizeSlider.Value = _settings.TileSize;
        GridSpacingSlider.Value = _settings.GridSpacing;
        WindowWidthSlider.Value = _settings.WindowWidth;
        WindowHeightSlider.Value = _settings.WindowHeight;
        WindowOpacitySlider.Value = _settings.WindowOpacity * 100;
        Opacity = _settings.WindowOpacity;
        Topmost = _settings.AlwaysOnTop;
        ApplyTheme(_settings.LightTheme);
        UpdateHotkeyUi();
        UpdateMetricLabels();

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.6) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            ToastBorder.Visibility = Visibility.Collapsed;
        };

        _settingsSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _settingsSaveTimer.Tick += (_, _) =>
        {
            _settingsSaveTimer.Stop();
            SaveLayout();
        };

        Loaded += (_, _) =>
        {
            PositionAboveTaskbar();
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => RecalculatePagination(true)));
        };

        SearchBox.TextChanged += SearchBox_TextChanged;
        RefreshItems();
        _initializing = false;
    }

    private void UpgradeVisualDefaults()
    {
        if (_settings.VisualStyleVersion >= 2)
            return;

        if (string.IsNullOrWhiteSpace(_settings.WindowBackgroundColor)
            && string.IsNullOrWhiteSpace(_settings.TileColor)
            && _settings.TileOpacity is null)
        {
            _settings.TileShadowEnabled = true;
            _settings.TileShadowOpacity = 0.25;
        }

        _settings.VisualStyleVersion = 2;
    }

    private static void PreserveLegacyWidgetBackgrounds(IEnumerable<LauncherItem> items)
    {
        foreach (var item in items)
        {
            if (item.IsWidget
                && item.WidgetBackgroundFollowsTheme
                && !string.Equals(item.WidgetBackgroundColor, "#242730", StringComparison.OrdinalIgnoreCase))
            {
                item.WidgetBackgroundFollowsTheme = false;
            }

            if (item.IsFolder || item.IsWidget)
                PreserveLegacyWidgetBackgrounds(item.Children);
        }
    }

    public void ShowLauncher()
    {
        _deactivationGeneration++;
        if (!IsVisible)
            Show();

        WindowState = WindowState.Normal;
        PositionAboveTaskbar();
        Activate();
        SearchBox.Focus();

        // Короткий импульс Topmost помогает поднять окно поверх полноэкранных окон,
        // но сохраняет пользовательскую настройку.
        Topmost = true;
        Topmost = _settings.AlwaysOnTop;
    }

    public void PrepareForApplicationExit()
    {
        _allowApplicationExit = true;
        _settings.WindowWidth = Width;
        _settings.WindowHeight = Height;
        SaveLayout();
        UnregisterGlobalHotkey();
    }

    public void HideIfUnpinnedAndInactive()
    {
        if (IsVisible && !IsActive && !_settings.AlwaysOnTop)
            HideLauncher();
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        ApplyNativeWindowStyle();
        _windowHandle = new WindowInteropHelper(this).Handle;
        _windowSource = HwndSource.FromHwnd(_windowHandle);
        _windowSource?.AddHook(WindowMessageHook);
        ApplyGlobalHotkey(false);
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmHotkey && wParam.ToInt32() == GlobalHotkeyId)
        {
            ShowLauncher();
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void PositionAboveTaskbar()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + Math.Max(10, (area.Width - ActualWidth) / 2);
        Top = area.Top + Math.Max(10, area.Height - ActualHeight - 12);
    }

    private void RefreshItems(bool resetPage = false)
    {
        UpdateFolderMembership(_settings.Items, false);
        _availableItems.Clear();
        var query = SearchBox?.Text.Trim() ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(query))
        {
            foreach (var item in FlattenApplications(_settings.Items)
                         .Where(item => Matches(item, query)))
            {
                UpdateDisplayMetrics(item);
                _availableItems.Add(item);
            }

            SectionTitle.Text = "Результаты поиска";
            BackButton.Visibility = _currentFolder is null ? Visibility.Collapsed : Visibility.Visible;
        }
        else
        {
            var source = _currentFolder?.Children ?? _settings.Items;
            foreach (var item in source)
            {
                UpdateDisplayMetrics(item);
                _availableItems.Add(item);
            }

            SectionTitle.Text = _currentFolder?.Name ?? "Все приложения";
            BackButton.Visibility = _currentFolder is null ? Visibility.Collapsed : Visibility.Visible;
        }

        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox?.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
        EmptyState.Visibility = _availableItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = string.IsNullOrWhiteSpace(query) ? "Здесь пока пусто" : "Ничего не найдено";
        ItemCountText.Text = FormatItemCount(_availableItems.Count);
        AddFolderButton.IsEnabled = _currentFolder is null && string.IsNullOrWhiteSpace(query);
        AddWidgetButton.IsEnabled = _currentFolder is null && string.IsNullOrWhiteSpace(query);

        if (resetPage)
            _currentPage = 0;
        RecalculatePagination(!resetPage);
    }

    private void RecalculatePagination(bool preserveAnchor)
    {
        StopPageAnimation();
        _pageSnapshotGeneration++;
        _pageSnapshotCache.Clear();

        foreach (var item in _availableItems)
            UpdateDisplayMetrics(item);

        var anchorId = preserveAnchor && _pages.Count > 0
            ? _pages[Math.Clamp(_currentPage, 0, _pages.Count - 1)].Items.FirstOrDefault()?.Id
            : null;
        var viewportWidth = PageViewport?.ActualWidth > 20
            ? PageViewport.ActualWidth
            : Math.Max(100, Width - 40);
        var viewportHeight = PageViewport?.ActualHeight > 20
            ? PageViewport.ActualHeight
            : Math.Max(100, Height - 205);

        var cellWidth = Math.Max(1, GridCellWidth);
        var cellHeight = Math.Max(1, GridCellHeight);
        var columns = Math.Max(1, (int)Math.Floor(viewportWidth / cellWidth));
        var rows = Math.Max(1, (int)Math.Floor(viewportHeight / cellHeight));
        var removedWidgets = BuildPages(columns, rows);
        _pageCount = Math.Max(1, _pages.Count);
        _currentPage = _availableItems.Count == 0
            ? 0
            : FindPageForItem(anchorId);

        if (removedWidgets.Count > 0)
        {
            ItemCountText.Text = FormatItemCount(_availableItems.Count);
            EmptyState.Visibility = _availableItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SaveLayout();
            var message = removedWidgets.Count == 1
                ? $"Виджет {removedWidgets[0].WidgetSizeText} удалён: он больше доступной сетки"
                : $"Удалено виджетов: {removedWidgets.Count} — они больше доступной сетки";
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => ShowToast(message)));
        }
        else if (_widgetLayoutChanged)
        {
            SaveLayout();
        }

        PopulateCurrentPage();
        UpdatePageNavigator();
        ScheduleAllPagesPreRender();
    }

    private void PopulateCurrentPage()
    {
        DisplayItems.Clear();
        foreach (var item in GetPageItems(_currentPage))
            DisplayItems.Add(item);
    }

    private void PopulateIncomingPage(int pageIndex)
    {
        IncomingPageItems.Clear();
        foreach (var item in GetPageItems(pageIndex))
            IncomingPageItems.Add(item);
    }

    private List<LauncherItem> BuildPages(int columns, int rows)
    {
        _pages.Clear();
        _widgetLayoutChanged = false;
        var removedWidgets = _availableItems
            .Where(item => item.IsWidget)
            .Where(item =>
            {
                var (columnSpan, rowSpan) = GridPackingService.GetSpan(item);
                return columnSpan > columns || rowSpan > rows;
            })
            .ToList();

        foreach (var widget in removedWidgets)
        {
            _availableItems.Remove(widget);
            _settings.Items.Remove(widget);
        }

        var widgets = _availableItems.Where(item => item.IsWidget).ToList();
        var regularItems = _availableItems.Where(item => !item.IsWidget).ToList();
        var pageCapacity = Math.Max(1, columns * rows);
        var maxUsefulPage = Math.Max(0,
            widgets.Count - 1 + (int)Math.Ceiling(regularItems.Count / (double)pageCapacity));

        // Координаты виджетов сохраняются в раскладке. Они занимают выбранные
        // пользователем области, а обычные значки текуче заполняют всё свободное
        // пространство вокруг них.
        foreach (var widget in widgets)
        {
            var preferredPage = widget.WidgetPage < 0
                ? 0
                : Math.Clamp(widget.WidgetPage, 0, maxUsefulPage);
            var preferredColumn = widget.WidgetColumn < 0 ? 0 : widget.WidgetColumn;
            var preferredRow = widget.WidgetRow < 0 ? 0 : widget.WidgetRow;
            var placed = false;

            for (var pageIndex = preferredPage; pageIndex <= maxUsefulPage + widgets.Count; pageIndex++)
            {
                var page = EnsurePage(pageIndex, columns, rows);
                var clampedColumn = Math.Clamp(
                    preferredColumn, 0, Math.Max(0, columns - widget.WidgetColumns));
                var clampedRow = Math.Clamp(
                    preferredRow, 0, Math.Max(0, rows - widget.WidgetRows));
                if (!page.TryAddNearest(widget, clampedColumn, clampedRow))
                    continue;

                _widgetLayoutChanged |= widget.WidgetPage != pageIndex
                                        || widget.WidgetColumn != widget.LayoutColumn
                                        || widget.WidgetRow != widget.LayoutRow;
                widget.WidgetPage = pageIndex;
                widget.WidgetColumn = widget.LayoutColumn;
                widget.WidgetRow = widget.LayoutRow;
                placed = true;
                break;
            }

            if (!placed)
                removedWidgets.Add(widget);
        }

        foreach (var widget in removedWidgets.Where(_availableItems.Contains).ToList())
        {
            _availableItems.Remove(widget);
            _settings.Items.Remove(widget);
        }

        foreach (var item in regularItems)
        {
            var added = _pages.Any(page => page.TryAdd(item));
            if (added)
                continue;

            EnsurePage(_pages.Count, columns, rows).TryAdd(item);
        }

        return removedWidgets;
    }

    private PageLayout EnsurePage(int pageIndex, int columns, int rows)
    {
        while (_pages.Count <= pageIndex)
            _pages.Add(new PageLayout(_pages.Count, columns, rows));
        return _pages[pageIndex];
    }

    private IReadOnlyList<LauncherItem> GetPageItems(int pageIndex)
        => _pages.Count == 0
            ? Array.Empty<LauncherItem>()
            : _pages[Math.Clamp(pageIndex, 0, _pages.Count - 1)].Items;

    private int FindPageForItem(Guid? itemId)
    {
        if (itemId.HasValue)
        {
            for (var page = 0; page < _pages.Count; page++)
                if (_pages[page].Items.Any(item => item.Id == itemId.Value))
                    return page;
        }

        return Math.Clamp(_currentPage, 0, Math.Max(0, _pages.Count - 1));
    }

    private void UpdateDisplayMetrics(LauncherItem item)
    {
        if (!item.IsWidget)
        {
            item.UpdateDisplaySize(TileSize, TileHeight);
            return;
        }

        var columns = Math.Max(1, item.WidgetColumns);
        var rows = Math.Max(1, item.WidgetRows);
        var width = columns * TileSize + (columns - 1) * _settings.GridSpacing;
        var height = rows * TileHeight + (rows - 1) * _settings.GridSpacing;
        item.UpdateDisplaySize(width, height);
    }

    private BitmapCache CreatePageCache()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        return new BitmapCache
        {
            // Полноэкранный кэш в нативном масштабе 150–200% требует десятки
            // мегабайт на каждый из двух слоёв. Для движения достаточно 1.25x:
            // после анимации WPF сразу возвращает живой, резкий интерфейс.
            RenderAtScale = Math.Min(1.25, Math.Max(dpi.DpiScaleX, dpi.DpiScaleY)),
            EnableClearType = false,
            SnapsToDevicePixels = true
        };
    }

    private void UpdatePageNavigator()
    {
        var navigationPage = _queuedPage ?? _currentPage;
        PageNavigator.Visibility = _pageCount > 1 ? Visibility.Visible : Visibility.Collapsed;
        PageNumberText.Text = $"{navigationPage + 1} / {_pageCount}";
        PreviousPageButton.IsEnabled = navigationPage > 0;
        NextPageButton.IsEnabled = navigationPage < _pageCount - 1;

        PageDotsPanel.Children.Clear();
        for (var pageIndex = 0; pageIndex < _pageCount; pageIndex++)
        {
            var dot = new Button
            {
                Style = (Style)FindResource("PageDotButton"),
                Tag = pageIndex,
                ToolTip = $"Страница {pageIndex + 1}"
            };
            if (pageIndex == navigationPage)
            {
                dot.Width = 20;
                dot.Opacity = 1;
                dot.SetResourceReference(Control.BackgroundProperty, "AccentSolidBrush");
            }
            dot.Click += PageDot_Click;
            PageDotsPanel.Children.Add(dot);
        }
    }

    private void NavigateToPage(int requestedPage)
    {
        var targetPage = Math.Clamp(requestedPage, 0, _pageCount - 1);
        if (targetPage == _currentPage)
            return;

        StopPageAnimation();
        ++_pageSnapshotGeneration;
        _lastPageDirection = targetPage > _currentPage ? 1 : -1;

        if (_pageSnapshotCache.TryGetValue(targetPage, out var snapshot))
        {
            ShowCachedPage(targetPage, snapshot);
            return;
        }

        _currentPage = targetPage;
        PopulateCurrentPage();
        SetDraggedTileOpacity();
        UpdatePageNavigator();
        ScheduleAllPagesPreRender();
    }

    private void ShowCachedPage(int targetPage, BitmapSource snapshot)
    {
        var generation = ++_pageAnimationGeneration;
        _currentPage = targetPage;
        _incomingSnapshotTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        _incomingSnapshotTranslate.X = _lastPageDirection * 22;
        IncomingPageSnapshot.Source = snapshot;
        IncomingPageSnapshot.Visibility = Visibility.Visible;
        IncomingPageSnapshot.Opacity = 0.58;
        LauncherItems.Visibility = Visibility.Hidden;
        UpdatePageNavigator();

        // The ready bitmap keeps the transition independent from live widgets.
        // A new gesture cancels this animation immediately through StopPageAnimation.
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(85);
        var slide = new DoubleAnimation(_incomingSnapshotTranslate.X, 0, duration)
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.HoldEnd
        };
        var fade = new DoubleAnimation(0.58, 1, duration)
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.HoldEnd
        };
        slide.Completed += (_, _) =>
        {
            if (generation != _pageAnimationGeneration)
                return;

            PopulateCurrentPage();
            SetDraggedTileOpacity();
            Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                if (generation != _pageAnimationGeneration)
                    return;

                _incomingSnapshotTranslate.BeginAnimation(TranslateTransform.XProperty, null);
                _incomingSnapshotTranslate.X = 0;
                IncomingPageSnapshot.BeginAnimation(OpacityProperty, null);
                IncomingPageSnapshot.Opacity = 1;
                LauncherItems.Visibility = Visibility.Visible;
                IncomingPageSnapshot.Source = null;
                IncomingPageSnapshot.Visibility = Visibility.Collapsed;
                ScheduleAllPagesPreRender();
            }));
        };
        _incomingSnapshotTranslate.BeginAnimation(TranslateTransform.XProperty, slide);
        IncomingPageSnapshot.BeginAnimation(OpacityProperty, fade);
    }

    private void NavigateWithPageSnapshots(
        int targetPage,
        BitmapSource outgoingSnapshot,
        BitmapSource incomingSnapshot)
    {
        _isPageAnimating = true;
        _queuedPage = null;
        var generation = ++_pageAnimationGeneration;
        ++_pagePreloadGeneration;
        ++_pageSnapshotGeneration;

        var direction = targetPage > _currentPage ? 1 : -1;
        _lastPageDirection = direction;
        var travel = Math.Max(160, PageViewport.ActualWidth + _settings.GridSpacing);

        _outgoingSnapshotTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        _incomingSnapshotTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        _outgoingSnapshotTranslate.X = 0;
        _incomingSnapshotTranslate.X = direction * travel;
        OutgoingPageSnapshot.Source = outgoingSnapshot;
        IncomingPageSnapshot.Source = incomingSnapshot;
        OutgoingPageSnapshot.Visibility = Visibility.Visible;
        IncomingPageSnapshot.Visibility = Visibility.Visible;

        // Живое дерево остаётся за кадром. Его замена выполняется уже после
        // визуального перехода и не задерживает первый ответ на колесо.
        LauncherItems.Visibility = Visibility.Hidden;
        IncomingLauncherItems.Visibility = Visibility.Collapsed;
        IncomingPageItems.Clear();
        _preparedIncomingPage = -1;

        _currentPage = targetPage;
        PageViewport.IsHitTestVisible = false;
        UpdatePageNavigator();

        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(20);
        var slideOut = new DoubleAnimation(0, -direction * travel, duration)
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.HoldEnd
        };
        var slideIn = new DoubleAnimation(direction * travel, 0, duration)
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.HoldEnd
        };

        slideIn.Completed += (_, _) =>
        {
            if (generation != _pageAnimationGeneration)
                return;

            _outgoingSnapshotTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            _outgoingSnapshotTranslate.X = 0;
            OutgoingPageSnapshot.Visibility = Visibility.Collapsed;
            OutgoingPageSnapshot.Source = null;
            _incomingSnapshotTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            _incomingSnapshotTranslate.X = 0;
            _isPageAnimating = false;
            PageViewport.IsHitTestVisible = true;
            UpdatePageNavigator();

            // Сначала пользователь получает готовый кадр, затем в простое WPF
            // незаметно подменяет его интерактивной страницей.
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
            {
                if (generation != _pageAnimationGeneration)
                    return;

                PopulateCurrentPage();
                SetDraggedTileOpacity();
                Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
                {
                    if (generation != _pageAnimationGeneration)
                        return;

                    LauncherItems.Visibility = Visibility.Visible;
                    IncomingPageSnapshot.Visibility = Visibility.Collapsed;
                    IncomingPageSnapshot.Source = null;
                    ScheduleAllPagesPreRender();
                }));
            }));

            if (_queuedPage is int queued && queued != _currentPage)
            {
                _queuedPage = null;
                NavigateToPage(queued);
            }
        };

        _outgoingSnapshotTranslate.BeginAnimation(TranslateTransform.XProperty, slideOut);
        _incomingSnapshotTranslate.BeginAnimation(TranslateTransform.XProperty, slideIn);
    }

    private void StopPageAnimation()
    {
        _pageAnimationGeneration++;
        _pagePreloadGeneration++;
        _isPageAnimating = false;
        _queuedPage = null;
        _preparedIncomingPage = -1;
        _pageTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        _incomingPageTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        _pageTranslate.X = 0;
        _incomingPageTranslate.X = 0;
        _outgoingSnapshotTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        _incomingSnapshotTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        _outgoingSnapshotTranslate.X = 0;
        _incomingSnapshotTranslate.X = 0;
        IncomingPageItems.Clear();
        if (LauncherItems is not null)
        {
            LauncherItems.CacheMode = null;
            LauncherItems.Visibility = Visibility.Visible;
        }
        if (IncomingLauncherItems is not null)
        {
            IncomingLauncherItems.CacheMode = null;
            IncomingLauncherItems.Visibility = Visibility.Collapsed;
        }
        if (OutgoingPageSnapshot is not null)
        {
            OutgoingPageSnapshot.Source = null;
            OutgoingPageSnapshot.Visibility = Visibility.Collapsed;
        }
        if (IncomingPageSnapshot is not null)
        {
            IncomingPageSnapshot.BeginAnimation(OpacityProperty, null);
            IncomingPageSnapshot.Opacity = 1;
            IncomingPageSnapshot.Source = null;
            IncomingPageSnapshot.Visibility = Visibility.Collapsed;
        }
        if (PageViewport is not null)
            PageViewport.IsHitTestVisible = true;
        if (PageNavigator is not null)
            PageNavigator.IsHitTestVisible = true;
    }

    private void ScheduleAllPagesPreRender()
    {
        if (!IsLoaded || _isPageAnimating || _pageCount <= 0)
            return;

        var generation = ++_pageSnapshotGeneration;
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            _ = PreRenderAllPagesAsync(generation);
        }));
    }

    private async Task PreRenderAllPagesAsync(int generation)
    {
        if (generation != _pageSnapshotGeneration || _isPageAnimating || !IsLoaded)
            return;

        // Текущая и ближайшая по направлению движения страницы получают
        // приоритет, затем спокойно готовится весь оставшийся набор.
        var pages = Enumerable.Range(0, _pageCount)
            .OrderBy(page => page == _currentPage ? -1 : Math.Abs(page - _currentPage))
            .ThenBy(page => _lastPageDirection > 0 ? page : -page)
            .ToArray();

        foreach (var page in pages)
        {
            if (generation != _pageSnapshotGeneration || _isPageAnimating || !IsLoaded)
                return;
            if (_pageSnapshotCache.ContainsKey(page))
                continue;

            BitmapSource? snapshot;
            if (page == _currentPage)
            {
                await Dispatcher.Yield(DispatcherPriority.Render);
                if (generation != _pageSnapshotGeneration || _isPageAnimating)
                    return;
                snapshot = CapturePageSnapshot(LauncherItems, _pageTranslate);
            }
            else
            {
                PopulateIncomingPage(page);
                _preparedIncomingPage = page;
                IncomingLauncherItems.CacheMode = null;
                _incomingPageTranslate.BeginAnimation(TranslateTransform.XProperty, null);
                _incomingPageTranslate.X = (page > _currentPage ? 1 : -1)
                    * Math.Max(160, PageViewport.ActualWidth + _settings.GridSpacing);
                IncomingLauncherItems.Visibility = Visibility.Visible;

                // Даём изображениям, документам и WebView2 получить собственные
                // общие снимки. Первый кадр сохраняем почти сразу, поэтому даже
                // ещё загружающаяся страница уже доступна для быстрого перехода.
                await Dispatcher.Yield(DispatcherPriority.Render);
                await Task.Delay(70);
                if (generation != _pageSnapshotGeneration || _isPageAnimating || !IsLoaded)
                    return;
                snapshot = CapturePageSnapshot(IncomingLauncherItems, _incomingPageTranslate);
                if (snapshot is not null)
                    _pageSnapshotCache[page] = snapshot;
            }

            if (snapshot is not null)
                _pageSnapshotCache[page] = snapshot;

            // Небольшой разрыв не даёт фоновому прогреву монополизировать UI-поток.
            await Task.Delay(35);
        }

        if (generation != _pageSnapshotGeneration || _isPageAnimating)
            return;

        IncomingLauncherItems.Visibility = Visibility.Collapsed;
        IncomingPageItems.Clear();
        _preparedIncomingPage = -1;
        _incomingPageTranslate.X = 0;
    }


    private BitmapSource? CapturePageSnapshot(
        FrameworkElement visual,
        TranslateTransform translate)
    {
        var width = PageViewport.ActualWidth;
        var height = PageViewport.ActualHeight;
        if (width <= 1 || height <= 1 || _pageCount <= 0)
            return null;

        // Полный кэш всех страниц ограничен примерно 160 МБ. До достижения
        // лимита кадры хранятся 1:1; при очень большом числе страниц разрешение
        // плавно уменьшается, но мгновенное перелистывание сохраняется.
        const double memoryBudget = 160d * 1024 * 1024;
        var fullSizeBytes = width * height * 4d * _pageCount;
        var scale = fullSizeBytes <= memoryBudget
            ? 1d
            : Math.Clamp(Math.Sqrt(memoryBudget / fullSizeBytes), 0.55, 1d);
        var pixelWidth = Math.Max(1, (int)Math.Ceiling(width * scale));
        var pixelHeight = Math.Max(1, (int)Math.Ceiling(height * scale));

        var oldX = translate.X;
        translate.BeginAnimation(TranslateTransform.XProperty, null);
        translate.X = 0;
        visual.UpdateLayout();
        try
        {
            var bitmap = new RenderTargetBitmap(
                pixelWidth,
                pixelHeight,
                96 * scale,
                96 * scale,
                PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }
        finally
        {
            translate.X = oldX;
        }
    }

    private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Колесо перелистывает страницы в любой точке основного окна — над
        // плитками, пустым местом, поиском или нижней панелью. При открытых
        // настройках событие остаётся свободным для вертикального ScrollViewer.
        if (SettingsPanel.Visibility == Visibility.Visible
            || FindVisualAncestor<WidgetLauncherGridView>(e.OriginalSource as DependencyObject) is not null
            || _pageCount <= 1)
            return;

        // Каждый импульс меняет целевую страницу. Это позволяет одним жестом
        // проскочить несколько страниц, пока текущий кадр ещё анимируется.
        var direction = e.Delta < 0 ? 1 : -1;
        NavigateToPage((_queuedPage ?? _currentPage) + direction);
        e.Handled = true;
    }

    private void PageViewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsLoaded)
            return;
        RecalculatePagination(true);
    }

    private void PreviousPage_Click(object sender, RoutedEventArgs e)
        => NavigateToPage((_queuedPage ?? _currentPage) - 1);

    private void NextPage_Click(object sender, RoutedEventArgs e)
        => NavigateToPage((_queuedPage ?? _currentPage) + 1);

    private void PageDot_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int pageIndex })
            NavigateToPage(pageIndex);
    }

    private static void UpdateFolderMembership(IEnumerable<LauncherItem> items, bool isInsideFolder)
    {
        foreach (var item in items)
        {
            item.IsInFolder = isInsideFolder;
            if (item.IsFolder || item.IsWidget)
                UpdateFolderMembership(item.Children, true);
        }
    }

    private static IEnumerable<LauncherItem> FlattenApplications(IEnumerable<LauncherItem> items)
    {
        foreach (var item in items)
        {
            if (item.IsApplication)
                yield return item;
            else if (item.IsFolder || item.IsWidget)
                foreach (var child in FlattenApplications(item.Children))
                    yield return child;
        }
    }

    private static bool Matches(LauncherItem item, string query)
        => item.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
           || item.Path.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static string FormatItemCount(int count)
    {
        var mod100 = count % 100;
        var mod10 = count % 10;
        var word = mod100 is >= 11 and <= 14
            ? "элементов"
            : mod10 switch
            {
                1 => "элемент",
                2 or 3 or 4 => "элемента",
                _ => "элементов"
            };
        return $"{count} {word}";
    }

    private void AddApplication_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Выберите приложения или ярлыки",
            Filter = "Приложения и ярлыки|*.exe;*.lnk;*.url;*.bat;*.cmd;*.com;*.msc;*.appref-ms|Все файлы|*.*",
            Multiselect = true,
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) != true)
            return;

        AddPaths(dialog.FileNames, _currentFolder?.Children ?? _settings.Items);
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFolder is not null || !string.IsNullOrWhiteSpace(SearchBox.Text))
            return;

        var name = PromptDialog.Show(this, "Новая группа", "Введите название группы приложений:", "Новая группа");
        if (string.IsNullOrWhiteSpace(name))
            return;

        _settings.Items.Add(new LauncherItem
        {
            Kind = LauncherItemKind.Folder,
            Name = name
        });
        SaveAndRefresh();
        NavigateToPage(_pageCount - 1);
        ShowToast("Группа создана — перетащите в неё приложения");
    }

    private void AddWidget_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFolder is not null || !string.IsNullOrWhiteSpace(SearchBox.Text))
            return;

        var (availableColumns, availableRows) = GetGridDimensions();
        if (!WidgetSizePickerDialog.TryPick(
                this,
                availableColumns,
                availableRows,
                out var rows,
                out var columns))
            return;

        if (columns > availableColumns || rows > availableRows)
        {
            ShowToast($"Виджет {rows}×{columns} не помещается — уменьшите плитки или увеличьте окно");
            return;
        }

        var widget = new LauncherItem
        {
            Kind = LauncherItemKind.Widget,
            Name = $"Виджет {rows}×{columns}",
            WidgetRows = rows,
            WidgetColumns = columns,
            WidgetPage = _currentPage,
            WidgetColumn = 0,
            WidgetRow = 0
        };
        UpdateDisplayMetrics(widget);
        _settings.Items.Add(widget);
        SaveAndRefresh();
        NavigateToPage(FindPageForItem(widget.Id));
        ShowToast($"Создана заготовка виджета {rows}×{columns}");
    }

    private (int Columns, int Rows) GetGridDimensions()
    {
        var viewportWidth = PageViewport?.ActualWidth > 20
            ? PageViewport.ActualWidth
            : Math.Max(100, Width - 40);
        var viewportHeight = PageViewport?.ActualHeight > 20
            ? PageViewport.ActualHeight
            : Math.Max(100, Height - 205);
        return (
            Math.Max(1, (int)Math.Floor(viewportWidth / Math.Max(1, GridCellWidth))),
            Math.Max(1, (int)Math.Floor(viewportHeight / Math.Max(1, GridCellHeight))));
    }

    private void AddPaths(IEnumerable<string> paths, ObservableCollection<LauncherItem> target)
    {
        var added = 0;
        foreach (var rawPath in paths)
        {
            var path = rawPath.Trim();
            if (string.IsNullOrWhiteSpace(path) || (!File.Exists(path) && !Directory.Exists(path)))
                continue;

            if (File.Exists(path) && !SupportedExtensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                continue;

            if (target.Any(item => !item.IsFolder && string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase)))
                continue;

            var name = Directory.Exists(path)
                ? new DirectoryInfo(path).Name
                : System.IO.Path.GetFileNameWithoutExtension(path);

            target.Add(new LauncherItem
            {
                Name = string.IsNullOrWhiteSpace(name) ? path : name,
                Path = path
            });
            added++;
        }

        if (added == 0)
        {
            ShowToast("Поддерживаются .exe, ярлыки и системные команды");
            return;
        }

        SaveAndRefresh();
        var visibleCollection = _currentFolder?.Children ?? _settings.Items;
        if (ReferenceEquals(target, visibleCollection) && string.IsNullOrWhiteSpace(SearchBox.Text))
            NavigateToPage(_pageCount - 1);
        ShowToast(added == 1 ? "Приложение добавлено" : $"Добавлено приложений: {added}");
    }

    private void Tile_Click(object sender, RoutedEventArgs e)
    {
        if (_dragStarted)
        {
            _dragStarted = false;
            return;
        }

        if (sender is not Button { DataContext: LauncherItem item })
            return;

        ActivateItem(item);
    }

    private void ActivateItem(LauncherItem item)
    {
        if (item.IsWidget)
        {
            if (!item.HasWidgetClickAction)
                return;

            try
            {
                LaunchService.LaunchPath(item.WidgetClickActionPath, item.WidgetClickArguments);
                if (_settings.HideAfterLaunch)
                    HideLauncher();
            }
            catch (Exception ex)
            {
                CrashReporter.Write(ex, "Ошибка действия виджета");
                ShowToast("Не удалось выполнить действие виджета");
            }
            return;
        }

        if (item.IsFolder)
        {
            if (!_settings.OpenGroupsFullscreen)
            {
                OpenFolderFlyout(item);
                return;
            }

            _currentFolder = item;
            _currentPage = 0;
            if (string.IsNullOrEmpty(SearchBox.Text))
                RefreshItems(true);
            else
                SearchBox.Clear();
            return;
        }

        try
        {
            LaunchService.Launch(item);
            item.LaunchCount++;
            SaveLayout();
            if (_settings.HideAfterLaunch)
                HideLauncher();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                $"Не удалось запустить «{item.Name}».\n\n{ex.Message}",
                "AppLauncher",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        _currentFolder = null;
        _currentPage = 0;
        if (string.IsNullOrEmpty(SearchBox.Text))
            RefreshItems(true);
        else
            SearchBox.Clear();
    }

    private void OpenFolderFlyout(LauncherItem folder)
    {
        _flyoutFolder = folder;
        FolderFlyoutItems.Clear();
        foreach (var item in folder.Children)
        {
            UpdateDisplayMetrics(item);
            FolderFlyoutItems.Add(item);
        }

        var maxWidth = Math.Max(GridCellWidth, PageViewport.ActualWidth * 0.8);
        var maxHeight = Math.Max(GridCellHeight, PageViewport.ActualHeight * 0.8);
        var maxColumns = Math.Max(1, (int)Math.Floor(maxWidth / GridCellWidth));
        var columns = Math.Min(maxColumns, Math.Max(1, (int)Math.Ceiling(Math.Sqrt(FolderFlyoutItems.Count))));
        var rows = Math.Max(1, (int)Math.Ceiling(FolderFlyoutItems.Count / (double)columns));
        FolderFlyoutPanel.Width = Math.Min(maxWidth, columns * GridCellWidth + 24);
        FolderFlyoutPanel.Height = Math.Min(maxHeight, rows * GridCellHeight + 66);
        FolderFlyoutTitle.Text = $"{folder.Name} · {FolderFlyoutItems.Count}";
        FolderFlyoutLayer.Visibility = Visibility.Visible;
    }

    private void CloseFolderFlyout()
    {
        FolderFlyoutLayer.Visibility = Visibility.Collapsed;
        FolderFlyoutItems.Clear();
        _flyoutFolder = null;
    }

    private void CloseFolderFlyout_Click(object sender, RoutedEventArgs e) => CloseFolderFlyout();

    private void FolderFlyoutTile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: LauncherItem item })
            ActivateItem(item);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshItems(true);

    private void Tile_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindVisualAncestor<WidgetLauncherGridView>(e.OriginalSource as DependencyObject) is not null)
        {
            _pressedItem = null;
            return;
        }

        if (sender is not Button { DataContext: LauncherItem item })
            return;
        _pressedItem = item;
        _dragStartPoint = e.GetPosition(this);
        _dragStarted = false;
    }

    private void WidgetChildGrid_ItemInvoked(object? sender, LauncherItemInvokedEventArgs e)
        => ActivateItem(e.Item);

    private void WidgetChildGrid_DragStateChanged(object? sender, LauncherItemDragStateEventArgs e)
    {
        if (e.IsActive)
        {
            _activeDragItem = e.Item;
            _previewTargetId = null;
            StartDragGhost(e.Item, Mouse.GetPosition(this));
            return;
        }

        StopDragGhost();
        _activeDragItem = null;
        _previewTargetId = null;
    }

    private void WidgetChildGrid_LayoutChanged(object? sender, EventArgs e)
    {
        SaveLayout();
    }

    private static T? FindVisualAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T result)
                return result;
            try
            {
                current = VisualTreeHelper.GetParent(current);
            }
            catch (InvalidOperationException)
            {
                current = (current as FrameworkContentElement)?.Parent;
            }
        }
        return null;
    }

    private void Tile_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressedItem is null || e.LeftButton != MouseButtonState.Pressed || !string.IsNullOrWhiteSpace(SearchBox.Text))
            return;

        var current = e.GetPosition(this);
        if (Math.Abs(current.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(current.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _dragStarted = true;
        var dragged = _pressedItem;
        _pressedItem = null;

        _activeDragItem = dragged;
        _previewTargetId = null;
        SetDraggedTileOpacity();
        StartDragGhost(dragged, current);

        var data = new DataObject();
        data.SetData(InternalDragFormat, dragged);
        try
        {
            DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Move);
        }
        finally
        {
            StopDragGhost();
            if (LauncherItems.ItemContainerGenerator.ContainerFromItem(dragged) is FrameworkElement draggedContainer)
                draggedContainer.Opacity = 1;
            _activeDragItem = null;
            _previewTargetId = null;
        }
    }

    private void StartDragGhost(LauncherItem item, Point startPoint)
    {
        DragGhost.DataContext = item;
        DragGhost.Visibility = Visibility.Visible;
        DragGhost.BeginAnimation(OpacityProperty, null);
        DragGhost.Opacity = 0.96;
        DragGhostScale.ScaleX = 0.86;
        DragGhostScale.ScaleY = 0.86;
        DragGhostRotate.Angle = 0;

        _dragGhostTarget = startPoint;
        _dragGhostPosition = startPoint;
        _dragGhostVelocity = new Vector();
        _lastDragRenderTime = TimeSpan.Zero;
        _dragGhostActive = true;
        UpdateDragGhostTransform();
        CompositionTarget.Rendering += DragGhost_Rendering;

        var entranceEase = new BackEase { Amplitude = 0.22, EasingMode = EasingMode.EaseOut };
        var scaleX = new DoubleAnimation(0.86, 1, TimeSpan.FromMilliseconds(170))
        {
            EasingFunction = entranceEase,
            FillBehavior = FillBehavior.Stop
        };
        var scaleY = new DoubleAnimation(0.86, 1, TimeSpan.FromMilliseconds(170))
        {
            EasingFunction = entranceEase,
            FillBehavior = FillBehavior.Stop
        };
        scaleY.Completed += (_, _) =>
        {
            DragGhostScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            DragGhostScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            DragGhostScale.ScaleX = 1;
            DragGhostScale.ScaleY = 1;
        };
        DragGhostScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleX);
        DragGhostScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);
    }

    private void StopDragGhost()
    {
        HideDropPlacementPreview();
        ResetReorderPreview();
        ResetDropPreviewHitTest();
        if (!_dragGhostActive)
            return;

        _dragGhostActive = false;
        CompositionTarget.Rendering -= DragGhost_Rendering;
        SetEdgePageDirection(0);
        DragGhostScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        DragGhostScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        DragGhost.Visibility = Visibility.Collapsed;
        DragGhost.DataContext = null;
        DragGhostScale.ScaleX = 1;
        DragGhostScale.ScaleY = 1;
        DragGhostRotate.Angle = 0;
        DragGhostTranslate.X = 0;
        DragGhostTranslate.Y = 0;
    }

    private void UpdateDragGhostTransform()
    {
        const double cursorOffset = 20;
        DragGhostTranslate.X = _dragGhostPosition.X - DragGhost.Width / 2 + cursorOffset;
        DragGhostTranslate.Y = _dragGhostPosition.Y - DragGhost.Height / 2 + cursorOffset;

        var speedRatio = Math.Clamp(_dragGhostVelocity.Length / 1350, 0, 1);
        DragGhostRotate.Angle = Math.Clamp(_dragGhostVelocity.X / 105, -9, 9);
        if (!DragGhostScale.HasAnimatedProperties)
        {
            DragGhostScale.ScaleX = 1 + speedRatio * 0.085;
            DragGhostScale.ScaleY = 1 - speedRatio * 0.045;
        }
    }

    private void DragGhost_Rendering(object? sender, EventArgs e)
    {
        if (!_dragGhostActive || e is not RenderingEventArgs rendering)
            return;

        var delta = _lastDragRenderTime == TimeSpan.Zero
            ? 1d / 60
            : Math.Clamp((rendering.RenderingTime - _lastDragRenderTime).TotalSeconds, 1d / 240, 0.05);
        _lastDragRenderTime = rendering.RenderingTime;

        var displacement = _dragGhostTarget - _dragGhostPosition;
        var acceleration = displacement * 112 - _dragGhostVelocity * 17;
        _dragGhostVelocity += acceleration * delta;
        _dragGhostPosition += _dragGhostVelocity * delta;
        UpdateDragGhostTransform();
    }

    private void Window_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(InternalDragFormat))
        {
            HideDropPlacementPreview();
            return;
        }

        if (_dragGhostActive)
        {
            var point = e.GetPosition(DragVisualLayer);
            _dragGhostTarget = point;
            UpdateEdgePaging(point);
        }

        UpdateDropPlacementPreview(e);
    }

    private void Window_PreviewDragLeave(object sender, DragEventArgs e)
    {
        if (!_dragGhostActive)
            return;

        var point = e.GetPosition(this);
        if (point.X <= 1 || point.Y <= 1 || point.X >= ActualWidth - 1 || point.Y >= ActualHeight - 1)
        {
            SetEdgePageDirection(0);
            HideDropPlacementPreview();
        }
    }

    private void UpdateDropPlacementPreview(DragEventArgs e)
    {
        if (e.Data.GetData(InternalDragFormat) is not LauncherItem source
            || !string.IsNullOrWhiteSpace(SearchBox.Text))
        {
            HideDropPlacementPreview();
            return;
        }

        var point = e.GetPosition(PageViewport);
        if (point.X < 0 || point.Y < 0
            || point.X >= PageViewport.ActualWidth
            || point.Y >= PageViewport.ActualHeight)
        {
            ResetDropPreviewHitTest();
            HideDropPlacementPreview();
            return;
        }

        // Пересчёт упаковки нужен только при переходе в другую половину ячейки,
        // а не на каждый пиксель движения мыши. Призрак продолжает идти в 60 FPS.
        var hitX = (int)Math.Floor(point.X / Math.Max(1, GridCellWidth / 2));
        var hitY = (int)Math.Floor(point.Y / Math.Max(1, GridCellHeight));
        if (_dropPreviewSourceId == source.Id
            && _dropPreviewPage == _currentPage
            && _dropPreviewHitX == hitX
            && _dropPreviewHitY == hitY)
            return;

        _dropPreviewSourceId = source.Id;
        _dropPreviewPage = _currentPage;
        _dropPreviewHitX = hitX;
        _dropPreviewHitY = hitY;

        if (!TryCalculateDropPlacement(source, e, out var placement)
            || placement.Page != _currentPage)
        {
            HideDropPlacementPreview();
            return;
        }

        ShowDropPlacementPreview(placement);
    }

    private void ResetDropPreviewHitTest()
    {
        _dropPreviewSourceId = null;
        _dropPreviewPage = -1;
        _dropPreviewHitX = int.MinValue;
        _dropPreviewHitY = int.MinValue;
    }

    private bool TryCalculateDropPlacement(
        LauncherItem source, DragEventArgs e, out DropPlacement placement)
    {
        placement = default;
        var (columns, rows) = GetGridDimensions();
        if (columns <= 0 || rows <= 0)
            return false;

        var targetCollection = _currentFolder?.Children ?? _settings.Items;
        if (source.IsWidget)
        {
            if (_currentFolder is not null || !_settings.Items.Contains(source))
                return false;

            var point = e.GetPosition(PageViewport);
            var columnSpan = Math.Max(1, source.WidgetColumns);
            var rowSpan = Math.Max(1, source.WidgetRows);
            if (columnSpan > columns || rowSpan > rows)
                return false;

            var preferredColumn = Math.Clamp(
                (int)Math.Floor(point.X / Math.Max(1, GridCellWidth)),
                0, Math.Max(0, columns - columnSpan));
            var preferredRow = Math.Clamp(
                (int)Math.Floor(point.Y / Math.Max(1, GridCellHeight)),
                0, Math.Max(0, rows - rowSpan));
            return TrySimulatePlacement(
                targetCollection.ToList(), source, columns, rows,
                _currentPage, preferredColumn, preferredRow, out placement);
        }

        if (source.IsFolder && _currentFolder is not null)
            return false;

        var reordered = targetCollection.Where(item => !ReferenceEquals(item, source)).ToList();
        var insertionIndex = GetPageDropInsertionIndex(e, targetCollection);
        var sourceIndex = targetCollection.IndexOf(source);
        if (sourceIndex >= 0 && sourceIndex < insertionIndex)
            insertionIndex--;
        reordered.Insert(Math.Clamp(insertionIndex, 0, reordered.Count), source);
        return TrySimulatePlacement(
            reordered, source, columns, rows,
            null, null, null, out placement);
    }

    private static bool TrySimulatePlacement(
        IReadOnlyList<LauncherItem> items,
        LauncherItem source,
        int columns,
        int rows,
        int? sourcePage,
        int? sourceColumn,
        int? sourceRow,
        out DropPlacement placement)
    {
        placement = default;
        var pages = new List<bool[,]>();
        var widgets = items.Where(item => item.IsWidget).ToList();
        var regularItems = items.Where(item => !item.IsWidget).ToList();
        var pageCapacity = Math.Max(1, columns * rows);
        var maxUsefulPage = Math.Max(0,
            widgets.Count - 1 + (int)Math.Ceiling(regularItems.Count / (double)pageCapacity));

        bool[,] EnsurePreviewPage(int pageIndex)
        {
            while (pages.Count <= pageIndex)
                pages.Add(new bool[rows, columns]);
            return pages[pageIndex];
        }

        foreach (var widget in widgets)
        {
            var columnSpan = Math.Max(1, widget.WidgetColumns);
            var rowSpan = Math.Max(1, widget.WidgetRows);
            if (columnSpan > columns || rowSpan > rows)
                continue;

            var isSource = ReferenceEquals(widget, source);
            var preferredPage = isSource && sourcePage.HasValue
                ? Math.Max(0, sourcePage.Value)
                : widget.WidgetPage < 0
                    ? 0
                    : Math.Clamp(widget.WidgetPage, 0, maxUsefulPage);
            var preferredColumn = isSource && sourceColumn.HasValue
                ? sourceColumn.Value
                : Math.Max(0, widget.WidgetColumn);
            var preferredRow = isSource && sourceRow.HasValue
                ? sourceRow.Value
                : Math.Max(0, widget.WidgetRow);

            for (var pageIndex = preferredPage; pageIndex <= maxUsefulPage + widgets.Count; pageIndex++)
            {
                var occupied = EnsurePreviewPage(pageIndex);
                var clampedColumn = Math.Clamp(preferredColumn, 0, Math.Max(0, columns - columnSpan));
                var clampedRow = Math.Clamp(preferredRow, 0, Math.Max(0, rows - rowSpan));
                if (!GridPackingService.TryPlaceNearest(
                        occupied, columns, rows, columnSpan, rowSpan,
                        clampedColumn, clampedRow, out var column, out var row))
                    continue;

                if (isSource)
                    placement = new DropPlacement(pageIndex, column, row, columnSpan, rowSpan);
                break;
            }
        }

        foreach (var item in regularItems)
        {
            var placed = false;
            for (var pageIndex = 0; pageIndex < pages.Count; pageIndex++)
            {
                if (!GridPackingService.TryPlace(
                        pages[pageIndex], columns, rows, 1, 1,
                        out var column, out var row))
                    continue;

                if (ReferenceEquals(item, source))
                    placement = new DropPlacement(pageIndex, column, row, 1, 1);
                placed = true;
                break;
            }

            if (placed)
                continue;

            var newPageIndex = pages.Count;
            var newPage = EnsurePreviewPage(newPageIndex);
            if (!GridPackingService.TryPlace(
                    newPage, columns, rows, 1, 1,
                    out var newColumn, out var newRow))
                continue;
            if (ReferenceEquals(item, source))
                placement = new DropPlacement(newPageIndex, newColumn, newRow, 1, 1);
        }

        return placement.ColumnSpan > 0 && placement.RowSpan > 0;
    }

    private void ShowDropPlacementPreview(DropPlacement placement)
    {
        if (_dropPreviewPlacement == placement
            && DropPlacementPreview.Visibility == Visibility.Visible)
            return;

        ++_dropPreviewGeneration;
        var targetX = placement.Column * GridCellWidth + _settings.GridSpacing / 2;
        var targetY = placement.Row * GridCellHeight + _settings.GridSpacing / 2;

        DropPlacementPreview.BeginAnimation(OpacityProperty, null);
        DropPlacementTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        DropPlacementTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        DropPlacementScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        DropPlacementScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        DropPlacementPreview.Width = placement.ColumnSpan * GridCellWidth - _settings.GridSpacing;
        DropPlacementPreview.Height = placement.RowSpan * GridCellHeight - _settings.GridSpacing;
        UpdateDropPlacementCells(placement);
        DropPlacementSizeText.Text = $"{placement.RowSpan}×{placement.ColumnSpan}";
        DropPlacementSizeBadge.Visibility = placement.ColumnSpan == 1 && placement.RowSpan == 1
            ? Visibility.Collapsed
            : Visibility.Visible;
        DropPlacementTranslate.X = targetX;
        DropPlacementTranslate.Y = targetY;
        DropPlacementPreview.Visibility = Visibility.Visible;
        DropPlacementPreview.Opacity = 1;
        _dropPreviewPlacement = placement;
    }

    private void UpdateDropPlacementCells(DropPlacement placement)
    {
        if (_dropPreviewColumnSpan == placement.ColumnSpan
            && _dropPreviewRowSpan == placement.RowSpan)
            return;

        _dropPreviewColumnSpan = placement.ColumnSpan;
        _dropPreviewRowSpan = placement.RowSpan;
        DropPlacementCells.Children.Clear();
        var accent = (Brush)FindResource("AccentSolidBrush");
        for (var row = 0; row < placement.RowSpan; row++)
        {
            for (var column = 0; column < placement.ColumnSpan; column++)
            {
                var cell = new Border
                {
                    Width = TileSize,
                    Height = TileHeight,
                    CornerRadius = new CornerRadius(9),
                    Background = accent,
                    BorderBrush = accent,
                    BorderThickness = new Thickness(1),
                    Opacity = 0.18
                };
                Canvas.SetLeft(cell, column * GridCellWidth);
                Canvas.SetTop(cell, row * GridCellHeight);
                DropPlacementCells.Children.Add(cell);
            }
        }
    }

    private void HideDropPlacementPreview()
    {
        if (DropPlacementPreview is null
            || DropPlacementPreview.Visibility != Visibility.Visible)
        {
            _dropPreviewPlacement = null;
            return;
        }

        if (_dropPreviewPlacement is null)
            return;

        ++_dropPreviewGeneration;
        _dropPreviewPlacement = null;
        DropPlacementPreview.BeginAnimation(OpacityProperty, null);
        DropPlacementPreview.Opacity = 0;
        DropPlacementPreview.Visibility = Visibility.Collapsed;
    }

    private void UpdateEdgePaging(Point point)
    {
        var basePage = _queuedPage ?? _currentPage;
        var threshold = Math.Clamp(DragVisualLayer.ActualWidth * 0.065, 58, 84);
        var direction = point.X <= threshold && basePage > 0
            ? -1
            : point.X >= DragVisualLayer.ActualWidth - threshold && basePage < _pageCount - 1
                ? 1
                : 0;
        SetEdgePageDirection(direction);
    }

    private void SetEdgePageDirection(int direction)
    {
        if (_edgePageDirection == direction)
            return;

        _edgePageDirection = direction;
        LeftEdgeCue.Visibility = direction < 0 ? Visibility.Visible : Visibility.Collapsed;
        RightEdgeCue.Visibility = direction > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (direction == 0)
            return;

        var basePage = _queuedPage ?? _currentPage;
        var targetPage = Math.Clamp(basePage + _edgePageDirection, 0, _pageCount - 1);
        if (targetPage == basePage)
        {
            SetEdgePageDirection(0);
            return;
        }

        NavigateToPage(targetPage);
    }

    private void Tile_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(InternalDragFormat) || e.Data.GetDataPresent(DataFormats.FileDrop))
            e.Effects = DragDropEffects.Move;
        else
            e.Effects = DragDropEffects.None;

        if (sender is Button { DataContext: LauncherItem target } targetButton
            && e.Data.GetData(InternalDragFormat) is LauncherItem source
            && !source.IsWidget
            && !ReferenceEquals(source, target)
            && !(target.IsFolder && source.IsApplication)
            && !target.CanAcceptWidgetChild)
        {
            var insertAfter = e.GetPosition(targetButton).X > targetButton.ActualWidth / 2;
            PreviewReorder(source, target, insertAfter);
        }
        else
        {
            ResetReorderPreview();
        }

        e.Handled = true;
    }

    private void Tile_Drop(object sender, DragEventArgs e)
    {
        if (sender is not Button { DataContext: LauncherItem target })
            return;

        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var paths = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (target.IsWidget && target.CanAcceptWidgetChild)
                target.WidgetContentType = WidgetContentKind.LauncherGrid;
            AddPaths(paths,
                target.IsFolder || target.IsWidgetLauncherGrid
                    ? target.Children
                    : FindParentCollection(target) ?? _settings.Items);
            e.Handled = true;
            return;
        }

        if (e.Data.GetData(InternalDragFormat) is not LauncherItem source)
            return;

        if (source.IsWidget)
        {
            MoveWidgetToCurrentPage(source, e);
            e.Handled = true;
            return;
        }

        if (target.CanAcceptWidgetChild)
        {
            var widgetSourceCollection = FindParentCollection(source);
            if (widgetSourceCollection is null)
                return;
            target.WidgetContentType = WidgetContentKind.LauncherGrid;
            widgetSourceCollection.Remove(source);
            target.Children.Add(source);
            SaveAndRefresh();
            ShowToast($"«{source.Name}» добавлено в виджет «{target.Name}»");
            e.Handled = true;
            return;
        }

        if (ReferenceEquals(source, target))
        {
            if (_previewTargetId.HasValue)
            {
                SaveLayout();
            }
            e.Handled = true;
            return;
        }

        var sourceCollection = FindParentCollection(source);
        if (sourceCollection is null)
            return;

        if (target.IsFolder && source.IsApplication)
        {
            if (ReferenceEquals(sourceCollection, target.Children))
                return;

            sourceCollection.Remove(source);
            target.Children.Add(source);
            SaveAndRefresh();
            ShowToast($"«{source.Name}» добавлено в «{target.Name}»");
        }
        else
        {
            var targetCollection = FindParentCollection(target);
            if (targetCollection is null || (source.IsFolder && !ReferenceEquals(targetCollection, _settings.Items)))
                return;

            var sourceIndex = sourceCollection.IndexOf(source);
            var targetIndex = targetCollection.IndexOf(target);
            var insertAfter = sender is Button targetButton
                              && e.GetPosition(targetButton).X > targetButton.ActualWidth / 2;
            sourceCollection.Remove(source);

            if (ReferenceEquals(sourceCollection, targetCollection) && sourceIndex < targetIndex)
                targetIndex--;
            if (insertAfter)
                targetIndex++;

            targetCollection.Insert(Math.Clamp(targetIndex, 0, targetCollection.Count), source);
            SaveAndRefresh();
        }

        e.Handled = true;
    }

    private void ItemsArea_DragOver(object sender, DragEventArgs e)
    {
        ResetReorderPreview();
        e.Effects = e.Data.GetDataPresent(InternalDragFormat) || e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void ItemsArea_Drop(object sender, DragEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(SearchBox.Text))
            return;

        var targetCollection = _currentFolder?.Children ?? _settings.Items;
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            AddPaths((string[])e.Data.GetData(DataFormats.FileDrop), targetCollection);
        }
        else if (e.Data.GetData(InternalDragFormat) is LauncherItem source)
        {
            MoveInternalItemToCurrentPage(source, e, targetCollection);
        }

        e.Handled = true;
    }

    private int GetPageDropInsertionIndex(DragEventArgs e, ObservableCollection<LauncherItem> collection)
    {
        var point = e.GetPosition(PageViewport);
        var cellWidth = Math.Max(1, TileSize + _settings.GridSpacing);
        var cellHeight = Math.Max(1, TileHeight + _settings.GridSpacing);
        var columns = Math.Max(1, (int)Math.Floor(PageViewport.ActualWidth / cellWidth));
        var column = Math.Clamp((int)Math.Floor(point.X / cellWidth), 0, columns - 1);
        var row = Math.Max(0, (int)Math.Floor(point.Y / cellHeight));
        var pageItems = GetPageItems(_currentPage);
        if (pageItems.Count == 0)
            return collection.Count;

        var referenceItem = pageItems
            .OrderBy(item => item.LayoutRow)
            .ThenBy(item => item.LayoutColumn)
            .FirstOrDefault(item => item.LayoutRow > row
                                    || item.LayoutRow == row && item.LayoutColumn >= column)
            ?? pageItems
                .OrderBy(item => item.LayoutRow)
                .ThenBy(item => item.LayoutColumn)
                .Last();
        var referenceIndex = collection.IndexOf(referenceItem);
        if (referenceIndex < 0)
            return collection.Count;
        var insertAfter = referenceItem.LayoutRow < row
                          || referenceItem.LayoutRow == row && referenceItem.LayoutColumn < column;
        return Math.Clamp(referenceIndex + (insertAfter ? 1 : 0), 0, collection.Count);
    }

    private void MoveInternalItemToCurrentPage(
        LauncherItem source,
        DragEventArgs e,
        ObservableCollection<LauncherItem> targetCollection)
    {
        if (source.IsWidget)
        {
            MoveWidgetToCurrentPage(source, e);
            return;
        }

        var sourceCollection = FindParentCollection(source);
        if (sourceCollection is null || (source.IsFolder && _currentFolder is not null))
            return;

        var insertionIndex = GetPageDropInsertionIndex(e, targetCollection);
        var sourceIndex = sourceCollection.IndexOf(source);
        sourceCollection.Remove(source);
        if (ReferenceEquals(sourceCollection, targetCollection) && sourceIndex < insertionIndex)
            insertionIndex--;
        targetCollection.Insert(Math.Clamp(insertionIndex, 0, targetCollection.Count), source);
        SaveAndRefresh();
    }

    private void MoveWidgetToCurrentPage(LauncherItem widget, DragEventArgs e)
    {
        if (!widget.IsWidget || !_settings.Items.Contains(widget)
            || _currentFolder is not null || !string.IsNullOrWhiteSpace(SearchBox.Text))
            return;

        var oldPositions = CaptureTilePositions();
        var point = e.GetPosition(PageViewport);
        var (columns, rows) = GetGridDimensions();
        var columnSpan = Math.Max(1, widget.WidgetColumns);
        var rowSpan = Math.Max(1, widget.WidgetRows);
        var column = Math.Clamp(
            (int)Math.Floor(point.X / Math.Max(1, GridCellWidth)),
            0,
            Math.Max(0, columns - columnSpan));
        var row = Math.Clamp(
            (int)Math.Floor(point.Y / Math.Max(1, GridCellHeight)),
            0,
            Math.Max(0, rows - rowSpan));

        var displacedItems = MoveDisplacedItemsToNextPage(widget, columns, rows);
        widget.WidgetPage = _currentPage;
        widget.WidgetColumn = column;
        widget.WidgetRow = row;
        SaveAndRefresh();
        AnimateTileReflow(oldPositions);
        NavigateToPage(FindPageForItem(widget.Id));
        ShowToast(displacedItems == 0
            ? $"«{widget.Name}» закреплён в новой позиции"
            : $"«{widget.Name}» закреплён: значков перенесено на следующую страницу — {displacedItems}");
    }

    private int MoveDisplacedItemsToNextPage(LauncherItem widget, int columns, int rows)
    {
        var pageItems = GetPageItems(_currentPage);
        var regularItems = pageItems
            .Where(item => !item.IsWidget)
            .OrderBy(item => item.LayoutRow)
            .ThenBy(item => item.LayoutColumn)
            .ToList();
        var occupiedWidgetCells = pageItems
            .Where(item => item.IsWidget && !ReferenceEquals(item, widget))
            .Sum(item =>
            {
                var (columnSpan, rowSpan) = GridPackingService.GetSpan(item);
                return columnSpan * rowSpan;
            });
        var widgetCells = Math.Max(1, widget.WidgetColumns) * Math.Max(1, widget.WidgetRows);
        var remainingSlots = Math.Max(0, columns * rows - occupiedWidgetCells - widgetCells);
        var displacedItems = regularItems
            .Skip(Math.Min(regularItems.Count, remainingSlots))
            .ToList();

        var nextPageFirstItem = _pages
            .Skip(_currentPage + 1)
            .SelectMany(page => page.Items)
            .FirstOrDefault(item => !item.IsWidget);
        var insertionIndex = nextPageFirstItem is null
            ? _settings.Items.Count
            : _settings.Items.IndexOf(nextPageFirstItem);

        foreach (var item in displacedItems)
            _settings.Items.Remove(item);
        foreach (var item in displacedItems)
            _settings.Items.Insert(Math.Min(insertionIndex++, _settings.Items.Count), item);

        return displacedItems.Count;
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
            e.Effects = DragDropEffects.Copy;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Handled)
            return;

        var targetCollection = _currentFolder?.Children ?? _settings.Items;
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            AddPaths((string[])e.Data.GetData(DataFormats.FileDrop), targetCollection);
            e.Handled = true;
        }
        else if (e.Data.GetData(InternalDragFormat) is LauncherItem source)
        {
            // Страховка для отпускания мыши прямо во время анимации страницы,
            // когда сама сетка на короткое время отключена от hit-testing.
            MoveInternalItemToCurrentPage(source, e, targetCollection);
            e.Handled = true;
        }
    }

    private void PreviewReorder(LauncherItem source, LauncherItem target, bool insertAfter)
    {
        if (_previewTargetId == target.Id && _previewInsertAfter == insertAfter)
            return;

        var sourceCollection = FindParentCollection(source);
        var targetCollection = FindParentCollection(target);
        if (sourceCollection is null || targetCollection is null || !ReferenceEquals(sourceCollection, targetCollection))
            return;

        var sourceIndex = sourceCollection.IndexOf(source);
        var targetIndex = targetCollection.IndexOf(target);
        if (sourceIndex < 0 || targetIndex < 0)
            return;

        var destinationIndex = targetIndex + (insertAfter ? 1 : 0);
        if (sourceIndex < destinationIndex)
            destinationIndex--;
        destinationIndex = Math.Clamp(destinationIndex, 0, sourceCollection.Count - 1);

        _previewTargetId = target.Id;
        _previewInsertAfter = insertAfter;
        ClearReorderPreviewTransforms();
        if (destinationIndex == sourceIndex)
            return;

        AnimateReorderPreview(source, target, insertAfter);
    }

    private void AnimateReorderPreview(LauncherItem source, LauncherItem target, bool insertAfter)
    {
        // Только трансформируем уже существующие контейнеры. Модель, страницы,
        // папки и WebView не пересоздаются до фактического Drop.
        var visibleSlots = DisplayItems.Where(item => !item.IsWidget).ToList();
        var sourceIndex = visibleSlots.IndexOf(source);
        var targetIndex = visibleSlots.IndexOf(target);
        if (sourceIndex < 0 || targetIndex < 0)
            return;

        var destinationIndex = targetIndex + (insertAfter ? 1 : 0);
        if (sourceIndex < destinationIndex)
            destinationIndex--;
        destinationIndex = Math.Clamp(destinationIndex, 0, visibleSlots.Count - 1);
        if (destinationIndex == sourceIndex)
            return;

        var first = destinationIndex < sourceIndex ? destinationIndex : sourceIndex + 1;
        var last = destinationIndex < sourceIndex ? sourceIndex - 1 : destinationIndex;
        var slotDirection = destinationIndex < sourceIndex ? 1 : -1;
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(115);

        for (var index = first; index <= last; index++)
        {
            var item = visibleSlots[index];
            var destinationSlot = visibleSlots[index + slotDirection];
            if (LauncherItems.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement container
                || LauncherItems.ItemContainerGenerator.ContainerFromItem(destinationSlot) is not FrameworkElement destination)
                continue;

            var currentPoint = container.TranslatePoint(new Point(), LauncherItems);
            var destinationPoint = destination.TranslatePoint(new Point(), LauncherItems);
            var offsetX = destinationPoint.X - currentPoint.X;
            var offsetY = destinationPoint.Y - currentPoint.Y;
            _reorderPreviewTransforms[container] = container.RenderTransform;
            var translate = new TranslateTransform { X = offsetX, Y = offsetY };
            container.RenderTransform = translate;
            translate.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(0, offsetX, duration)
                {
                    EasingFunction = easing,
                    FillBehavior = FillBehavior.Stop
                });
            translate.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(0, offsetY, duration)
                {
                    EasingFunction = easing,
                    FillBehavior = FillBehavior.Stop
                });
        }
    }

    private void ClearReorderPreviewTransforms()
    {
        foreach (var (container, originalTransform) in _reorderPreviewTransforms)
        {
            if (container.RenderTransform is TranslateTransform translate)
            {
                translate.BeginAnimation(TranslateTransform.XProperty, null);
                translate.BeginAnimation(TranslateTransform.YProperty, null);
            }
            container.RenderTransform = originalTransform;
        }
        _reorderPreviewTransforms.Clear();
    }

    private void ResetReorderPreview()
    {
        _previewTargetId = null;
        _previewInsertAfter = false;
        ClearReorderPreviewTransforms();
    }

    private Dictionary<Guid, Point> CaptureTilePositions()
    {
        LauncherItems.UpdateLayout();
        var result = new Dictionary<Guid, Point>();
        foreach (var item in DisplayItems)
        {
            if (LauncherItems.ItemContainerGenerator.ContainerFromItem(item) is FrameworkElement container)
                result[item.Id] = container.TranslatePoint(new Point(), LauncherItems);
        }
        return result;
    }

    private void AnimateTileReflow(IReadOnlyDictionary<Guid, Point> oldPositions)
    {
        var generation = ++_reflowGeneration;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (generation != _reflowGeneration)
                return;

            LauncherItems.UpdateLayout();
            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
            var duration = TimeSpan.FromMilliseconds(190);

            foreach (var item in DisplayItems)
            {
                if (!oldPositions.TryGetValue(item.Id, out var oldPoint)
                    || LauncherItems.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement container)
                    continue;

                var newPoint = container.TranslatePoint(new Point(), LauncherItems);
                var offsetX = oldPoint.X - newPoint.X;
                var offsetY = oldPoint.Y - newPoint.Y;
                if (Math.Abs(offsetX) < 0.5 && Math.Abs(offsetY) < 0.5)
                    continue;

                var translate = new TranslateTransform(offsetX, offsetY);
                container.RenderTransform = translate;
                translate.BeginAnimation(TranslateTransform.XProperty,
                    new DoubleAnimation(0, duration) { EasingFunction = easing });
                translate.BeginAnimation(TranslateTransform.YProperty,
                    new DoubleAnimation(0, duration) { EasingFunction = easing });
            }

            SetDraggedTileOpacity();
        }));
    }

    private void SetDraggedTileOpacity()
    {
        if (_activeDragItem is null)
            return;
        if (LauncherItems.ItemContainerGenerator.ContainerFromItem(_activeDragItem) is FrameworkElement container)
            container.Opacity = 0;
    }

    private ObservableCollection<LauncherItem>? FindParentCollection(LauncherItem target)
    {
        return FindParentCollection(_settings.Items, target);
    }

    private static ObservableCollection<LauncherItem>? FindParentCollection(
        ObservableCollection<LauncherItem> collection,
        LauncherItem target)
    {
        if (collection.Contains(target))
            return collection;
        foreach (var container in collection.Where(item => item.IsFolder || item.IsWidget))
        {
            var nested = FindParentCollection(container.Children, target);
            if (nested is not null)
                return nested;
        }
        return null;
    }

    private void RenameItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: LauncherItem item })
            return;

        try
        {
            var title = item.IsFolder
                ? "Переименовать группу"
                : item.IsWidget ? "Переименовать виджет" : "Переименовать приложение";
            var name = PromptDialog.Show(this, title, "Введите новое название:", item.Name);
            if (string.IsNullOrWhiteSpace(name))
                return;
            item.Name = name;
            IconService.Invalidate(item);
            SaveAndRefresh();
        }
        catch (Exception ex)
        {
            CrashReporter.Write(ex, "Ошибка диалога переименования");
            ShowToast("Не удалось открыть переименование — подробности в crash.log");
        }
    }

    private void EditWidget_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem)
            return;

        var item = menuItem.DataContext as LauncherItem
                   ?? ((menuItem.Parent as ContextMenu)?.PlacementTarget as FrameworkElement)?.DataContext as LauncherItem;
        if (item?.IsWidget != true)
            return;

        OpenWidgetEditor(item);
    }

    private void OpenWidgetEditor(LauncherItem item)
    {
        try
        {
            if (!WidgetEditorDialog.Show(this, item, _settings.SavedColors))
                return;

            SaveAndRefresh();
            ShowToast(item.HasWidgetContent
                ? "Содержимое виджета сохранено"
                : "Настройки виджета сохранены");
        }
        catch (Exception ex)
        {
            CrashReporter.Write(ex, "Ошибка редактора виджета");
            ShowToast("Не удалось открыть редактор — подробности в crash.log");
        }
    }

    private void OpenContextItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: LauncherItem item })
            ActivateItem(item);
    }

    private void ChooseItemIcon_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: LauncherItem item } || item.IsWidget)
            return;

        var dialog = new OpenFileDialog
        {
            Title = item.IsFolder
                ? $"Выберите значок для группы «{item.Name}»"
                : $"Выберите значок для «{item.Name}»",
            Filter = "Значки и изображения|*.ico;*.png;*.jpg;*.jpeg;*.bmp;*.gif|Программы и ярлыки|*.exe;*.dll;*.lnk|Все поддерживаемые файлы|*.ico;*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.exe;*.dll;*.lnk",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
            return;

        IconService.Invalidate(item);
        item.IconPath = dialog.FileName;
        SaveAndRefresh();
        ShowToast($"Значок «{item.Name}» обновлён");
    }

    private void ResetItemIcon_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: LauncherItem item } || !item.HasCustomIcon)
            return;

        IconService.Invalidate(item);
        item.IconPath = string.Empty;
        SaveAndRefresh();
        ShowToast("Возвращён стандартный значок");
    }

    private void OpenFileLocation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: LauncherItem item } || !item.IsApplication)
            return;

        var path = Environment.ExpandEnvironmentVariables(item.Path);
        if (Directory.Exists(path))
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            return;
        }

        if (File.Exists(path))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true
            });
            return;
        }

        ShowToast("Для этой системной команды расположение файла недоступно");
    }

    private void RunAsAdmin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: LauncherItem item } && item.IsApplication)
            SaveLayout();
    }

    private void MoveOutOfFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: LauncherItem item })
            return;

        var parent = FindParentCollection(item);
        if (parent is null || ReferenceEquals(parent, _settings.Items))
        {
            ShowToast("Элемент уже находится на главном экране");
            return;
        }

        parent.Remove(item);
        _settings.Items.Add(item);
        SaveAndRefresh();
        ShowToast("Элемент перемещён на главный экран");
    }

    private void RemoveItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: LauncherItem item })
            return;

        var detail = item.IsFolder && item.Children.Count > 0
            ? $"Группа содержит {item.Children.Count} приложений. Файлы на диске удалены не будут."
            : item.IsWidget
                ? "Будет удалена только заготовка виджета."
                : "Само приложение с диска удалено не будет.";
        var result = MessageBox.Show(this,
            $"Убрать «{item.Name}» из AppLauncher?\n\n{detail}",
            "Подтверждение",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
            return;

        var parent = FindParentCollection(item);
        parent?.Remove(item);
        if (ReferenceEquals(item, _currentFolder))
            _currentFolder = null;
        SaveAndRefresh();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsPanel.Visibility == Visibility.Visible)
            HideSettingsPanel();
        else
            ShowSettingsPanel();
    }

    private void CloseSettings_Click(object sender, RoutedEventArgs e)
        => HideSettingsPanel();

    private void ShowSettingsPanel()
    {
        _settingsAnimationGeneration++;
        SettingsPanel.BeginAnimation(OpacityProperty, null);
        SettingsPanelTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        SettingsPanel.Visibility = Visibility.Visible;
        SettingsPanel.IsHitTestVisible = true;
        SettingsPanel.Opacity = 1;
        SettingsPanelTranslate.X = 0;

        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        SettingsPanelTranslate.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(-54, 0, TimeSpan.FromMilliseconds(285))
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.Stop
            });
        SettingsPanel.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(190))
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.Stop
            });
    }

    private void HideSettingsPanel()
    {
        if (SettingsPanel.Visibility != Visibility.Visible)
            return;

        var generation = ++_settingsAnimationGeneration;
        SettingsPanel.IsHitTestVisible = false;
        SettingsPanel.BeginAnimation(OpacityProperty, null);
        SettingsPanelTranslate.BeginAnimation(TranslateTransform.XProperty, null);

        var easing = new CubicEase { EasingMode = EasingMode.EaseIn };
        var slide = new DoubleAnimation(0, -42, TimeSpan.FromMilliseconds(190))
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.HoldEnd
        };
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(155))
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.HoldEnd
        };
        slide.Completed += (_, _) =>
        {
            if (generation != _settingsAnimationGeneration)
                return;
            SettingsPanel.BeginAnimation(OpacityProperty, null);
            SettingsPanelTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            SettingsPanel.Opacity = 1;
            SettingsPanelTranslate.X = 0;
            SettingsPanel.Visibility = Visibility.Collapsed;
        };
        SettingsPanelTranslate.BeginAnimation(TranslateTransform.XProperty, slide);
        SettingsPanel.BeginAnimation(OpacityProperty, fade);
    }

    private void SettingsChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing)
            return;
        _settings.HideAfterLaunch = HideAfterLaunchCheckBox.IsChecked == true;
        _settings.AlwaysOnTop = AlwaysOnTopCheckBox.IsChecked == true;
        _settings.OpenGroupsFullscreen = OpenGroupsFullscreenCheckBox.IsChecked == true;
        Topmost = _settings.AlwaysOnTop;
        UpdatePinButton();
        SaveLayout();
    }

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.AlwaysOnTop = !_settings.AlwaysOnTop;
        Topmost = _settings.AlwaysOnTop;

        _initializing = true;
        AlwaysOnTopCheckBox.IsChecked = _settings.AlwaysOnTop;
        _initializing = false;

        UpdatePinButton();
        SaveLayout();
        ShowToast(_settings.AlwaysOnTop
            ? "Окно закреплено"
            : "Окно откреплено · оно будет скрываться при потере фокуса");
    }

    private void UpdatePinButton()
    {
        if (PinButton is null)
            return;

        PinButton.Content = _settings.AlwaysOnTop ? "\uE77A" : "\uE718";
        PinButton.ToolTip = _settings.AlwaysOnTop
            ? "Открепить окно"
            : "Закрепить окно поверх остальных";
        PinButton.Foreground = (System.Windows.Media.Brush)Resources[
            _settings.AlwaysOnTop ? "AccentSolidBrush" : "TextBrush"];
    }

    private void GlobalHotkeyEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing)
            return;

        _settings.GlobalHotkeyEnabled = GlobalHotkeyCheckBox.IsChecked == true;
        ApplyGlobalHotkey(true);
        SaveLayout();
    }

    private void HotkeyCaptureButton_Click(object sender, RoutedEventArgs e)
    {
        _isCapturingHotkey = true;
        HotkeyCaptureButton.Content = "Нажмите сочетание…";
        HotkeyStatusText.Text = "Используйте Ctrl, Alt, Shift или Win вместе с другой клавишей · Esc — отмена";
        Focus();
        Keyboard.Focus(this);
    }

    private void ResetHotkey_Click(object sender, RoutedEventArgs e)
        => TrySetGlobalHotkey(
            NativeWindowService.HotkeyModifierControl | NativeWindowService.HotkeyModifierAlt,
            0x20);

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_isCapturingHotkey)
            return;

        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            _isCapturingHotkey = false;
            UpdateHotkeyUi();
            return;
        }

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return;

        var modifiers = GetNativeHotkeyModifiers(Keyboard.Modifiers);
        if (modifiers == 0)
        {
            HotkeyStatusText.Text = "Добавьте к клавише Ctrl, Alt, Shift или Win";
            return;
        }

        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey <= 0)
            return;

        _isCapturingHotkey = false;
        TrySetGlobalHotkey(modifiers, virtualKey);
    }

    private void TrySetGlobalHotkey(int modifiers, int virtualKey)
    {
        var previousModifiers = _settings.GlobalHotkeyModifiers;
        var previousVirtualKey = _settings.GlobalHotkeyVirtualKey;
        _settings.GlobalHotkeyModifiers = modifiers;
        _settings.GlobalHotkeyVirtualKey = virtualKey;

        if (!ApplyGlobalHotkey(false))
        {
            _settings.GlobalHotkeyModifiers = previousModifiers;
            _settings.GlobalHotkeyVirtualKey = previousVirtualKey;
            ApplyGlobalHotkey(false);
            HotkeyStatusText.Text = "Это сочетание уже используется. Выберите другое.";
            return;
        }

        UpdateHotkeyUi();
        SaveLayout();
        ShowToast("Горячая клавиша: " + FormatHotkey(modifiers, virtualKey));
    }

    private bool ApplyGlobalHotkey(bool showToast)
    {
        UnregisterGlobalHotkey();
        if (!_settings.GlobalHotkeyEnabled)
        {
            UpdateHotkeyUi();
            if (showToast)
                ShowToast("Глобальная горячая клавиша отключена");
            return true;
        }

        if (_windowHandle == IntPtr.Zero)
        {
            UpdateHotkeyUi();
            return true;
        }

        _globalHotkeyRegistered = NativeWindowService.RegisterGlobalHotkey(
            _windowHandle,
            GlobalHotkeyId,
            _settings.GlobalHotkeyModifiers,
            _settings.GlobalHotkeyVirtualKey);
        UpdateHotkeyUi();

        if (!_globalHotkeyRegistered && showToast)
            ShowToast("Не удалось назначить сочетание: оно уже используется");
        else if (_globalHotkeyRegistered && showToast)
            ShowToast("Горячая клавиша включена");

        return _globalHotkeyRegistered;
    }

    private void UnregisterGlobalHotkey()
    {
        if (!_globalHotkeyRegistered)
            return;

        NativeWindowService.UnregisterGlobalHotkey(_windowHandle, GlobalHotkeyId);
        _globalHotkeyRegistered = false;
    }

    private void UpdateHotkeyUi()
    {
        if (HotkeyCaptureButton is null || _isCapturingHotkey)
            return;

        HotkeyCaptureButton.Content = FormatHotkey(
            _settings.GlobalHotkeyModifiers,
            _settings.GlobalHotkeyVirtualKey);
        HotkeyStatusText.Text = !_settings.GlobalHotkeyEnabled
            ? "Быстрый вызов отключён"
            : _windowHandle == IntPtr.Zero || _globalHotkeyRegistered
                ? "Показывает AppLauncher поверх текущего окна"
                : "Сочетание занято другим приложением";
    }

    private static int GetNativeHotkeyModifiers(ModifierKeys modifiers)
    {
        var result = 0;
        if (modifiers.HasFlag(ModifierKeys.Control))
            result |= NativeWindowService.HotkeyModifierControl;
        if (modifiers.HasFlag(ModifierKeys.Alt))
            result |= NativeWindowService.HotkeyModifierAlt;
        if (modifiers.HasFlag(ModifierKeys.Shift))
            result |= NativeWindowService.HotkeyModifierShift;
        if (modifiers.HasFlag(ModifierKeys.Windows))
            result |= NativeWindowService.HotkeyModifierWindows;
        return result;
    }

    private static string FormatHotkey(int modifiers, int virtualKey)
    {
        var parts = new List<string>(5);
        if ((modifiers & NativeWindowService.HotkeyModifierControl) != 0)
            parts.Add("Ctrl");
        if ((modifiers & NativeWindowService.HotkeyModifierAlt) != 0)
            parts.Add("Alt");
        if ((modifiers & NativeWindowService.HotkeyModifierShift) != 0)
            parts.Add("Shift");
        if ((modifiers & NativeWindowService.HotkeyModifierWindows) != 0)
            parts.Add("Win");

        var key = KeyInterop.KeyFromVirtualKey(virtualKey);
        parts.Add(key switch
        {
            Key.Space => "Space",
            Key.Return => "Enter",
            Key.Back => "Backspace",
            Key.Prior => "Page Up",
            Key.Next => "Page Down",
            _ => key.ToString()
        });
        return string.Join(" + ", parts);
    }

    private void FolderThumbnailSettingChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing)
            return;

        _settings.UseFolderThumbnails = FolderThumbnailsCheckBox.IsChecked == true;
        OnPropertyChanged(nameof(UseFolderThumbnails));
        SaveLayout();
    }

    private void WidgetBackgroundSettingChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing)
            return;

        _settings.WidgetBackgroundMatchesTiles = WidgetBackgroundMatchesTilesCheckBox.IsChecked == true;
        OnPropertyChanged(nameof(WidgetBackgroundMatchesTiles));
        SaveLayout();
    }

    private void FolderThumbnailSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing)
            return;

        _settings.FolderThumbnailScale = Math.Round(FolderThumbnailSizeSlider.Value) / 100;
        OnPropertyChanged(nameof(FolderThumbnailSize));
        UpdateMetricLabels();
        ScheduleSettingsSave();
    }

    private void ThemeChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing)
            return;
        _settings.LightTheme = LightThemeCheckBox.IsChecked == true;
        if (_settings.TileOpacity is null)
        {
            _initializing = true;
            TileOpacitySlider.Value = EffectiveTileOpacity * 100;
            _initializing = false;
        }
        ApplyTheme(_settings.LightTheme);
        SaveLayout();
    }

    private void VisualProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || VisualProfileCombo.SelectedValue is not string profile)
            return;

        _settings.VisualProfile = profile;
        ApplyTheme(_settings.LightTheme);
        SaveLayout();
    }

    private void ChooseWindowColor_Click(object sender, RoutedEventArgs e)
    {
        var selected = ColorPickerDialog.Show(this, "Фон приложения", EffectiveWindowColor, _settings.SavedColors);
        if (selected is null)
        {
            SaveLayout();
            return;
        }

        _settings.WindowBackgroundColor = selected;
        ApplyTheme(_settings.LightTheme);
        SaveLayout();
    }

    private void ChooseTileColor_Click(object sender, RoutedEventArgs e)
    {
        var selected = ColorPickerDialog.Show(this, "Цвет плиток", EffectiveTileColor, _settings.SavedColors);
        if (selected is null)
        {
            SaveLayout();
            return;
        }

        _settings.TileColor = selected;
        OnPropertyChanged(nameof(EffectiveTileColor));
        ApplyTheme(_settings.LightTheme);
        SaveLayout();
    }

    private void ChoosePositiveActionColor_Click(object sender, RoutedEventArgs e)
    {
        var selected = ColorPickerDialog.Show(this, "Позитивные действия", EffectivePositiveActionColor, _settings.SavedColors);
        if (selected is null)
        {
            SaveLayout();
            return;
        }

        _settings.PositiveActionColor = selected;
        ApplyTheme(_settings.LightTheme);
        SaveLayout();
    }

    private void ChooseNegativeActionColor_Click(object sender, RoutedEventArgs e)
    {
        var selected = ColorPickerDialog.Show(this, "Негативные действия", EffectiveNegativeActionColor, _settings.SavedColors);
        if (selected is null)
        {
            SaveLayout();
            return;
        }

        _settings.NegativeActionColor = selected;
        ApplyTheme(_settings.LightTheme);
        SaveLayout();
    }

    private void TileAppearanceChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing)
            return;

        _settings.TileOpacity = Math.Clamp(TileOpacitySlider.Value / 100, 0.05, 1.0);
        ApplyTheme(_settings.LightTheme);
        ScheduleSettingsSave();
    }

    private void WindowOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing)
            return;

        _settings.WindowOpacity = Math.Clamp(WindowOpacitySlider.Value / 100, 0.45, 1.0);
        Opacity = _settings.WindowOpacity;
        UpdateAppearanceLabels();
        ScheduleSettingsSave();
    }

    private void TileShadowChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing)
            return;

        _settings.TileShadowEnabled = TileShadowCheckBox.IsChecked == true;
        UpdateTileShadowEffect();
        UpdateAppearanceLabels();
        SaveLayout();
    }

    private void TileShadowOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing)
            return;

        _settings.TileShadowOpacity = Math.Clamp(TileShadowOpacitySlider.Value / 100, 0.10, 0.70);
        UpdateTileShadowEffect();
        UpdateAppearanceLabels();
        ScheduleSettingsSave();
    }

    private void ResetAppearance_Click(object sender, RoutedEventArgs e)
    {
        _settings.WindowBackgroundColor = null;
        _settings.TileColor = null;
        _settings.PositiveActionColor = null;
        _settings.NegativeActionColor = null;
        _settings.TileOpacity = null;
        _settings.WindowOpacity = 1.0;
        _settings.TileShadowEnabled = true;
        _settings.TileShadowOpacity = 0.25;
        _settings.VisualProfile = "Glass";
        _settings.WidgetBackgroundMatchesTiles = true;

        _initializing = true;
        TileOpacitySlider.Value = EffectiveTileOpacity * 100;
        WindowOpacitySlider.Value = 100;
        TileShadowCheckBox.IsChecked = true;
        TileShadowOpacitySlider.Value = 25;
        VisualProfileCombo.SelectedValue = _settings.VisualProfile;
        WidgetBackgroundMatchesTilesCheckBox.IsChecked = true;
        _initializing = false;
        OnPropertyChanged(nameof(EffectiveTileColor));
        OnPropertyChanged(nameof(WidgetBackgroundMatchesTiles));
        Opacity = 1;

        ApplyTheme(_settings.LightTheme);
        SaveLayout();
        ShowToast("Оформление сброшено");
    }

    private void LayoutMetricsChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing)
            return;

        _settings.IconSize = Math.Round(IconSizeSlider.Value);
        _settings.TileSize = Math.Round(TileSizeSlider.Value);
        _settings.GridSpacing = Math.Round(GridSpacingSlider.Value);
        RaiseLayoutPropertyChanges();
        UpdateMetricLabels();
        RecalculatePagination(true);
        ScheduleSettingsSave();
    }

    private void WindowMetricsChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing)
            return;

        _settings.WindowWidth = Math.Round(WindowWidthSlider.Value);
        _settings.WindowHeight = Math.Round(WindowHeightSlider.Value);
        Width = _settings.WindowWidth;
        Height = _settings.WindowHeight;
        UpdateMetricLabels();
        ScheduleSettingsSave();
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(PositionAboveTaskbar));
    }

    private void RaiseLayoutPropertyChanges()
    {
        OnPropertyChanged(nameof(TileSize));
        OnPropertyChanged(nameof(TileHeight));
        OnPropertyChanged(nameof(GridCellWidth));
        OnPropertyChanged(nameof(GridCellHeight));
        OnPropertyChanged(nameof(IconSize));
        OnPropertyChanged(nameof(IconPlateSize));
        OnPropertyChanged(nameof(FolderThumbnailSize));
        OnPropertyChanged(nameof(TileMargin));
    }

    private void UpdateMetricLabels()
    {
        IconSizeValueText.Text = $"{IconSize:0} px";
        TileSizeValueText.Text = $"{TileSize:0} px";
        GridSpacingValueText.Text = $"{_settings.GridSpacing:0} px";
        FolderThumbnailSizeValueText.Text = $"{_settings.FolderThumbnailScale * 100:0}% · {FolderThumbnailSize:0} px";
        WindowWidthValueText.Text = $"{_settings.WindowWidth:0} px";
        WindowHeightValueText.Text = $"{_settings.WindowHeight:0} px";
        UpdateAppearanceLabels();
    }

    private void ScheduleSettingsSave()
    {
        _settingsSaveTimer.Stop();
        _settingsSaveTimer.Start();
    }

    private void ApplyTheme(bool light)
    {
        var tileOpacity = EffectiveTileOpacity;
        var visualProfile = _settings.VisualProfile is "Solid" or "Soft" ? _settings.VisualProfile : "Glass";
        var panelColor = visualProfile switch
        {
            "Solid" => light ? "#FFF8F9FC" : "#FF151820",
            "Soft" => light ? "#F2F7F8FC" : "#F2191D27",
            _ => light ? "#EFFFFFFF" : "#E6171A22"
        };
        var tileColor = visualProfile switch
        {
            "Solid" => light ? "#FFFFFFFF" : "#FF20242E",
            "Soft" => light ? "#F9FFFFFF" : "#D91E222D",
            _ => light ? "#F2FFFFFF" : "#16FFFFFF"
        };
        var tileHoverColor = visualProfile switch
        {
            "Solid" => light ? "#FFFDFDFF" : "#FF292E3A",
            "Soft" => light ? "#FFFFFFFF" : "#F52A2F3B",
            _ => light ? "#FFFFFFFF" : "#22FFFFFF"
        };
        Resources["WindowBrush"] = Brush(EffectiveWindowColor);
        Resources["PanelBrush"] = Brush(panelColor);
        Resources["TextBrush"] = Brush(light ? "#FF171A24" : "#FFF7F8FC");
        Resources["MutedTextBrush"] = Brush(light ? "#8A252A38" : "#A3FFFFFF");
        Resources["WindowBorderBrush"] = Brush(light ? "#16101828" : "#20FFFFFF");
        Resources["ColorSwatchBorderBrush"] = Brush(light ? "#5C101828" : "#70FFFFFF");
        Resources["TileBrush"] = Brush(tileColor);
        Resources["TileHoverBrush"] = Brush(tileHoverColor);
        Resources["LauncherTileBrush"] = Brush(EffectiveTileColor, tileOpacity);
        Resources["LauncherTileHoverBrush"] = Brush(EffectiveTileColor, Math.Min(1, tileOpacity + (light ? 0.04 : 0.08)));
        Resources["TileBorderBrush"] = Brush(light ? "#17101828" : "#18FFFFFF");
        Resources["SearchBrush"] = Brush(light ? "#FFFFFFFF" : "#0AFFFFFF");
        Resources["HoverBrush"] = Brush(light ? "#0D101828" : "#12FFFFFF");
        Resources["SwitchOffBrush"] = Brush(light ? "#25101828" : "#20FFFFFF");
        Resources["CounterBrush"] = Brush(light ? "#0D101828" : "#0CFFFFFF");
        Resources["ScrollThumbBrush"] = Brush(light ? "#48101828" : "#4DFFFFFF");
        Resources["ScrollThumbHoverBrush"] = Brush(light ? "#76101828" : "#85FFFFFF");
        Resources["AccentSolidBrush"] = Brush("#7569FF");
        Resources["AccentBrush"] = new LinearGradientBrush(
            Color.FromRgb(120, 92, 255),
            Color.FromRgb(62, 126, 255),
            new Point(0, 0),
            new Point(1, 1));
        Resources["PositiveSolidBrush"] = Brush(EffectivePositiveActionColor);
        Resources["PositiveBrush"] = new LinearGradientBrush(
            (Color)ColorConverter.ConvertFromString(EffectivePositiveActionColor)!,
            Color.FromRgb(55, 183, 126),
            new Point(0, 0),
            new Point(1, 1));
        Resources["NegativeSolidBrush"] = Brush(EffectiveNegativeActionColor);
        ApplyThemeWidgetBackgrounds(_settings.Items, light);
        UpdateTileShadowEffect();
        UpdateAppearanceLabels();
        UpdatePinButton();
        Background = (System.Windows.Media.Brush)Resources["WindowBrush"];
        ApplyNativeWindowStyle();
    }

    private static void ApplyThemeWidgetBackgrounds(IEnumerable<LauncherItem> items, bool light)
    {
        var defaultWidgetColor = light ? "#FFF7F9FD" : "#242730";
        foreach (var item in items)
        {
            if (item.IsWidget)
                item.ApplyThemeWidgetBackground(defaultWidgetColor);
            if (item.IsFolder || item.IsWidget)
                ApplyThemeWidgetBackgrounds(item.Children, light);
        }
    }

    private void ApplyNativeWindowStyle()
        => NativeWindowService.ApplyModernWindowStyle(
            new WindowInteropHelper(this).Handle,
            !_settings.LightTheme);

    private string EffectiveWindowColor
        => NormalizeColor(_settings.WindowBackgroundColor, _settings.LightTheme ? "#F7F8FC" : "#101218");

    public string EffectiveTileColor
        => NormalizeColor(_settings.TileColor, "#FFFFFF");

    public bool WidgetBackgroundMatchesTiles
        => _settings.WidgetBackgroundMatchesTiles;

    private string EffectivePositiveActionColor
        => NormalizeColor(_settings.PositiveActionColor, "#27B878");

    private string EffectiveNegativeActionColor
        => NormalizeColor(_settings.NegativeActionColor, "#E25662");

    private double EffectiveTileOpacity
        => Math.Clamp(_settings.TileOpacity ?? (_settings.LightTheme ? 1.0 : 0.10), 0.05, 1.0);

    private static string NormalizeColor(string? value, string fallback)
        => ColorPickerDialog.TryNormalizeColor(value, out var normalized) ? normalized : fallback;

    private void UpdateAppearanceLabels()
    {
        if (WindowColorValueText is null)
            return;

        WindowColorValueText.Text = EffectiveWindowColor;
        TileColorValueText.Text = EffectiveTileColor;
        TileOpacityValueText.Text = $"{EffectiveTileOpacity * 100:0}%";
        WindowOpacityValueText.Text = $"{_settings.WindowOpacity * 100:0}%";
        TileShadowOpacityValueText.Text = $"{_settings.TileShadowOpacity * 100:0}%";
        PositiveActionColorValueText.Text = EffectivePositiveActionColor;
        NegativeActionColorValueText.Text = EffectiveNegativeActionColor;
        WindowColorSwatch.Background = Brush(EffectiveWindowColor);
        TileColorSwatch.Background = Brush(EffectiveTileColor);
        PositiveActionColorSwatch.Background = Brush(EffectivePositiveActionColor);
        NegativeActionColorSwatch.Background = Brush(EffectiveNegativeActionColor);
    }

    private void UpdateTileShadowEffect()
    {
        if (!_settings.TileShadowEnabled)
        {
            TileShadowEffect = null;
        }
        else
        {
            var effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 18,
                ShadowDepth = 4,
                Direction = 270,
                Opacity = _settings.TileShadowOpacity
            };
            effect.Freeze();
            TileShadowEffect = effect;
        }

        OnPropertyChanged(nameof(TileShadowEffect));
    }

    private static SolidColorBrush Brush(string value)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(value)!;
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush Brush(string value, double opacity)
    {
        var color = (Color)ColorConverter.ConvertFromString(value)!;
        color.A = (byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private void OpenLayoutFile_Click(object sender, RoutedEventArgs e)
    {
        SaveLayout();
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{LayoutStore.DataPath}\"",
            UseShellExecute = true
        });
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
            return;
        try { DragMove(); } catch { /* кнопка могла перехватить мышь */ }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => HideLauncher();
    private void CloseButton_Click(object sender, RoutedEventArgs e) => HideLauncher();

    private void HideLauncher()
    {
        _deactivationGeneration++;
        _isCapturingHotkey = false;
        UpdateHotkeyUi();
        WindowState = WindowState.Normal;
        Hide();
    }

    private void Window_Activated(object? sender, EventArgs e)
        => _deactivationGeneration++;

    private async void Window_Deactivated(object? sender, EventArgs e)
    {
        if (_settings.AlwaysOnTop || _allowApplicationExit || !IsVisible)
            return;

        var generation = ++_deactivationGeneration;
        await Task.Delay(140);
        if (generation != _deactivationGeneration
            || IsActive
            || !IsVisible
            || _settings.AlwaysOnTop
            || NativeWindowService.IsForegroundWindowOwnedByCurrentProcess())
            return;

        HideLauncher();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        if (SettingsPanel.Visibility == Visibility.Visible)
            HideSettingsPanel();
        else if (!string.IsNullOrWhiteSpace(SearchBox.Text))
            SearchBox.Clear();
        else if (_currentFolder is not null)
            BackButton_Click(sender, e);
        else
            HideLauncher();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowApplicationExit)
        {
            e.Cancel = true;
            HideLauncher();
            return;
        }

        _settings.WindowWidth = Width;
        _settings.WindowHeight = Height;
        SaveLayout();
        UnregisterGlobalHotkey();
        if (_windowSource is not null)
            _windowSource.RemoveHook(WindowMessageHook);
    }

    private void SaveAndRefresh()
    {
        SaveLayout();
        RefreshItems();
    }

    private void SaveLayout()
    {
        try
        {
            LayoutStore.Save(_settings);
        }
        catch (Exception ex)
        {
            ShowToast("Не удалось сохранить раскладку: " + ex.Message);
        }
    }

    private void ShowToast(string message)
    {
        ToastText.Text = message;
        ToastBorder.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
