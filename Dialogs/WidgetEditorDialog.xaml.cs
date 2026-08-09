using System.ComponentModel;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using AppLauncher.Models;
using AppLauncher.Services;
using Microsoft.Win32;

namespace AppLauncher.Dialogs;

public partial class WidgetEditorDialog : Window
{
    private readonly LauncherItem _source;
    private readonly LauncherItem _draft;
    private readonly ObservableCollection<string> _savedColors;
    private bool _initializing = true;
    private bool _previewDragging;
    private Point _previewDragStart;
    private double _previewStartOffsetX;
    private double _previewStartOffsetY;
    private int _previewTransformGeneration;

    private WidgetEditorDialog(
        Window owner,
        LauncherItem source,
        ObservableCollection<string> savedColors)
    {
        InitializeComponent();
        Owner = owner;
        var workArea = SystemParameters.WorkArea;
        Width = Math.Clamp(
            owner.ActualWidth * 0.80,
            MinWidth,
            Math.Max(MinWidth, workArea.Width - 54));
        Height = Math.Clamp(
            owner.ActualHeight * 0.82,
            MinHeight,
            Math.Max(MinHeight, workArea.Height - 54));
        _source = source;
        _savedColors = savedColors;
        _draft = CreateDraft(source);
        DataContext = _draft;
        var darkMode = ApplyOwnerTheme(owner);
        SourceInitialized += (_, _) => NativeWindowService.ApplyModernWindowStyle(
            new WindowInteropHelper(this).Handle,
            darkMode);

        ScaleSlider.Value = _draft.WidgetScale * 100;
        OffsetXSlider.Value = _draft.WidgetOffsetX;
        OffsetYSlider.Value = _draft.WidgetOffsetY;
        ContentOpacitySlider.Value = _draft.WidgetContentOpacity * 100;
        WidgetIconSizeSlider.Value = _draft.WidgetInnerIconSize;
        ContentKindCombo.SelectedIndex = (int)_draft.WidgetContentType;
        LauncherDisplayModeCombo.SelectedIndex = (int)_draft.WidgetDisplayMode;
        _draft.PropertyChanged += Draft_PropertyChanged;
        PreviewWidgetView.ViewportRendered += PreviewWidgetView_ViewportRendered;
        _initializing = false;
        UpdateLabels();
        UpdateTitleVisibility();
        UpdateContentPanels();

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                DialogResult = false;
                Close();
            }
        };
    }

    public static bool Show(
        Window owner,
        LauncherItem item,
        ObservableCollection<string> savedColors)
        => new WidgetEditorDialog(owner, item, savedColors).ShowDialog() == true;

    private static LauncherItem CreateDraft(LauncherItem source)
    {
        var draft = CloneLauncherItem(source);
        draft.Kind = LauncherItemKind.Widget;
        return draft;
    }

    private static LauncherItem CloneLauncherItem(LauncherItem source)
        => new()
        {
            Id = source.Id,
            Kind = source.Kind,
            Name = source.Name,
            Path = source.Path,
            Arguments = source.Arguments,
            IconPath = source.IconPath,
            RunAsAdministrator = source.RunAsAdministrator,
            LaunchCount = source.LaunchCount,
            WidgetRows = source.WidgetRows,
            WidgetColumns = source.WidgetColumns,
            WidgetPage = source.WidgetPage,
            WidgetColumn = source.WidgetColumn,
            WidgetRow = source.WidgetRow,
            WidgetContentType = source.WidgetContentType,
            WidgetUrl = source.WidgetUrl,
            WidgetContentPath = source.WidgetContentPath,
            WidgetClickActionPath = source.WidgetClickActionPath,
            WidgetClickArguments = source.WidgetClickArguments,
            WidgetCssSelector = source.WidgetCssSelector,
            WidgetScale = source.WidgetScale,
            WidgetOffsetX = source.WidgetOffsetX,
            WidgetOffsetY = source.WidgetOffsetY,
            WidgetContentOpacity = source.WidgetContentOpacity,
            WidgetShowTitle = source.WidgetShowTitle,
            WidgetInnerIconSize = source.WidgetInnerIconSize,
            WidgetDisplayMode = source.WidgetDisplayMode,
            WidgetFolderPath = source.WidgetFolderPath,
            WidgetBackgroundColor = source.WidgetBackgroundColor,
            Children = new ObservableCollection<LauncherItem>(source.Children.Select(CloneLauncherItem))
        };

    private bool ApplyOwnerTheme(Window owner)
    {
        Resources["DialogSurfaceBrush"] = GetOwnerBrush(owner, "PanelBrush", "#181B24");
        Resources["DialogTextBrush"] = GetOwnerBrush(owner, "TextBrush", "#FFFFFF");
        Resources["DialogMutedTextBrush"] = GetOwnerBrush(owner, "MutedTextBrush", "#A8FFFFFF");
        Resources["DialogBorderBrush"] = GetOwnerBrush(owner, "ColorSwatchBorderBrush", "#30FFFFFF");
        Resources["DialogControlBrush"] = GetOwnerBrush(owner, "TileBrush", "#15FFFFFF");
        Resources["DialogHoverBrush"] = GetOwnerBrush(owner, "HoverBrush", "#17FFFFFF");
        Resources["DialogAccentBrush"] = GetOwnerBrush(owner, "AccentSolidBrush", "#6E62FF");
        Resources["AccentSolidBrush"] = GetOwnerBrush(owner, "AccentSolidBrush", "#6E62FF");
        Resources["HoverBrush"] = GetOwnerBrush(owner, "HoverBrush", "#17FFFFFF");
        Resources["AccentBrush"] = owner.Resources["AccentBrush"] ?? GetOwnerBrush(owner, "AccentSolidBrush", "#6E62FF");
        Resources["SwitchOffBrush"] = GetOwnerBrush(owner, "SwitchOffBrush", "#20FFFFFF");
        Resources["TileBorderBrush"] = GetOwnerBrush(owner, "TileBorderBrush", "#20FFFFFF");
        Resources["TextBrush"] = GetOwnerBrush(owner, "TextBrush", "#FFFFFF");

        if (Resources["DialogSurfaceBrush"] is not SolidColorBrush surface)
            return true;
        var luminance = (0.2126 * surface.Color.R + 0.7152 * surface.Color.G + 0.0722 * surface.Color.B) / 255;
        return luminance < 0.5;
    }

    private static Brush GetOwnerBrush(Window owner, string key, string fallback)
    {
        if (owner.Resources[key] is Brush brush)
            return brush;
        var fallbackBrush = (SolidColorBrush)new BrushConverter().ConvertFromString(fallback)!;
        fallbackBrush.Freeze();
        return fallbackBrush;
    }

    private void PreviewSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing)
            return;
        _draft.WidgetScale = ScaleSlider.Value / 100;
        _draft.WidgetOffsetX = OffsetXSlider.Value;
        _draft.WidgetOffsetY = OffsetYSlider.Value;
        _draft.WidgetContentOpacity = ContentOpacitySlider.Value / 100;
        UpdateLabels();
    }

    private void Draft_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LauncherItem.WidgetShowTitle))
            UpdateTitleVisibility();
    }

    private void UpdateLabels()
    {
        ScaleValueText.Text = $"{_draft.WidgetScale * 100:0}%";
        OffsetXValueText.Text = $"{_draft.WidgetOffsetX:+0;-0;0} px";
        OffsetYValueText.Text = $"{_draft.WidgetOffsetY:+0;-0;0} px";
        OpacityValueText.Text = $"{_draft.WidgetContentOpacity * 100:0}%";
        WidgetIconSizeValueText.Text = $"{_draft.WidgetInnerIconSize:0} px";
    }

    private void UpdateTitleVisibility()
        => PreviewTitleSurface.Visibility = _draft.WidgetShowTitle
            ? Visibility.Visible
            : Visibility.Collapsed;

    private void ContentKindCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || DataContext is not LauncherItem draft
            || ContentKindCombo.SelectedItem is not ComboBoxItem { Tag: string tag }
            || !Enum.TryParse<WidgetContentKind>(tag, out var kind))
            return;

        draft.WidgetContentType = kind;
        ValidationText.Visibility = Visibility.Collapsed;
        UpdateContentPanels();
    }

    private void UpdateContentPanels()
    {
        var isWebPage = _draft.WidgetContentType == WidgetContentKind.WebPage;
        var isLauncherGrid = _draft.WidgetContentType == WidgetContentKind.LauncherGrid;
        WebSourcePanel.Visibility = isWebPage ? Visibility.Visible : Visibility.Collapsed;
        FileSourcePanel.Visibility = !isWebPage && !isLauncherGrid ? Visibility.Visible : Visibility.Collapsed;
        LauncherGridPanel.Visibility = isLauncherGrid ? Visibility.Visible : Visibility.Collapsed;
        CssSelectorPanel.Visibility = isWebPage ? Visibility.Visible : Visibility.Collapsed;
        ViewportPanel.Visibility = isLauncherGrid ? Visibility.Collapsed : Visibility.Visible;
        PreviewWidgetView.Visibility = isLauncherGrid ? Visibility.Collapsed : Visibility.Visible;
        PreviewLauncherGrid.Visibility = isLauncherGrid ? Visibility.Visible : Visibility.Collapsed;
        PreviewDragSurface.Visibility = isLauncherGrid ? Visibility.Collapsed : Visibility.Visible;
        PreviewDragHint.Visibility = isLauncherGrid ? Visibility.Collapsed : Visibility.Visible;
        PreviewNudgePad.Visibility = isLauncherGrid ? Visibility.Collapsed : Visibility.Visible;
        if (isLauncherGrid)
            ResetPreviewTransform();

        (ContentFileLabel.Text, ContentFileHint.Text) = _draft.WidgetContentType switch
        {
            WidgetContentKind.TextFile => ("Текстовый файл", "TXT, Markdown, LOG, JSON, XML, CSV, INI и другие текстовые форматы."),
            WidgetContentKind.PdfFile => ("Документ PDF", "PDF отображается встроенным просмотрщиком Microsoft Edge WebView2."),
            WidgetContentKind.ImageFile => ("Изображение", "PNG, JPEG, BMP, GIF, TIFF, ICO, WebP и SVG."),
            _ => ("Файл", string.Empty)
        };
    }

    private void WidgetIconSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing)
            return;
        _draft.WidgetInnerIconSize = WidgetIconSizeSlider.Value;
        UpdateLabels();
    }

    private void LauncherDisplayModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing
            || LauncherDisplayModeCombo.SelectedItem is not ComboBoxItem { Tag: string tag }
            || !Enum.TryParse<WidgetLauncherDisplayMode>(tag, out var displayMode))
            return;
        _draft.WidgetDisplayMode = displayMode;
    }

    private void BrowseWidgetFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Выберите папку, содержимое которой будет показано в виджете",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            SelectedPath = Directory.Exists(_draft.WidgetFolderPath)
                ? _draft.WidgetFolderPath
                : string.Empty
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
            return;

        _draft.WidgetContentType = WidgetContentKind.LauncherGrid;
        _draft.WidgetFolderPath = dialog.SelectedPath;
        WidgetFolderService.Refresh(_draft, force: true);
        UpdateContentPanels();
    }

    private void ClearWidgetFolder_Click(object sender, RoutedEventArgs e)
        => _draft.WidgetFolderPath = string.Empty;

    private void ChooseWidgetBackground_Click(object sender, RoutedEventArgs e)
    {
        var selected = ColorPickerDialog.Show(
            this,
            "Фон виджета",
            _draft.WidgetBackgroundColor,
            _savedColors);
        if (!string.IsNullOrWhiteSpace(selected))
            _draft.WidgetBackgroundColor = selected;
    }

    private void AddWidgetApplications_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Добавить значки в виджет",
            Filter = "Приложения и ярлыки|*.exe;*.lnk;*.url;*.bat;*.cmd;*.com;*.msc;*.appref-ms|Все файлы|*.*",
            Multiselect = true,
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true)
            return;

        var supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".exe", ".lnk", ".url", ".bat", ".cmd", ".com", ".msc", ".appref-ms"
        };
        foreach (var path in dialog.FileNames)
        {
            if (!supported.Contains(Path.GetExtension(path))
                || _draft.Children.Any(item => item.IsApplication
                                               && string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase)))
                continue;
            _draft.Children.Add(new LauncherItem
            {
                Name = Path.GetFileNameWithoutExtension(path),
                Path = path
            });
        }
    }

    private void AddWidgetFolder_Click(object sender, RoutedEventArgs e)
    {
        var name = PromptDialog.Show(this, "Папка внутри виджета", "Введите название папки:", "Новая папка");
        if (string.IsNullOrWhiteSpace(name))
            return;
        _draft.Children.Add(new LauncherItem
        {
            Kind = LauncherItemKind.Folder,
            Name = name
        });
    }

    private void ClearWidgetGrid_Click(object sender, RoutedEventArgs e)
        => _draft.Children.Clear();

    private void BrowseContent_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = _draft.WidgetContentType switch
            {
                WidgetContentKind.TextFile => "Выберите текстовый файл",
                WidgetContentKind.PdfFile => "Выберите документ PDF",
                WidgetContentKind.ImageFile => "Выберите изображение",
                _ => "Выберите файл"
            },
            Filter = _draft.WidgetContentType switch
            {
                WidgetContentKind.TextFile => "Текстовые документы|*.txt;*.md;*.log;*.json;*.xml;*.csv;*.ini;*.yaml;*.yml;*.config|Все файлы|*.*",
                WidgetContentKind.PdfFile => "Документы PDF|*.pdf|Все файлы|*.*",
                WidgetContentKind.ImageFile => "Изображения|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.ico;*.webp;*.svg|Все файлы|*.*",
                _ => "Все файлы|*.*"
            },
            CheckFileExists = true,
            Multiselect = false
        };
        SetInitialDirectory(dialog, _draft.WidgetContentPath);
        if (dialog.ShowDialog(this) == true)
            _draft.WidgetContentPath = dialog.FileName;
    }

    private void BrowseClickAction_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Выберите действие для левого клика",
            Filter = "Программы и ярлыки|*.exe;*.lnk;*.bat;*.cmd;*.com;*.url|Документы и другие файлы|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        SetInitialDirectory(dialog, _draft.WidgetClickActionPath);
        if (dialog.ShowDialog(this) == true)
            _draft.WidgetClickActionPath = dialog.FileName;
    }

    private void ClearClickAction_Click(object sender, RoutedEventArgs e)
    {
        _draft.WidgetClickActionPath = string.Empty;
        _draft.WidgetClickArguments = string.Empty;
    }

    private static void SetInitialDirectory(OpenFileDialog dialog, string value)
    {
        try
        {
            var path = Environment.ExpandEnvironmentVariables(value ?? string.Empty);
            if (File.Exists(path))
                dialog.InitialDirectory = Path.GetDirectoryName(path) ?? string.Empty;
        }
        catch
        {
            // Диалог просто откроется в стандартной папке Windows.
        }
    }

    private void ResetViewport_Click(object sender, RoutedEventArgs e)
    {
        ResetPreviewTransform();
        ScaleSlider.Value = 100;
        OffsetXSlider.Value = 0;
        OffsetYSlider.Value = 0;
        ContentOpacitySlider.Value = 100;
        _draft.WidgetCssSelector = string.Empty;
    }

    private void Preview_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
            return;

        _previewTransformGeneration++;
        PreviewContentTranslate.X = 0;
        PreviewContentTranslate.Y = 0;
        _previewDragging = true;
        _previewDragStart = e.GetPosition(PreviewDragSurface);
        _previewStartOffsetX = _draft.WidgetOffsetX;
        _previewStartOffsetY = _draft.WidgetOffsetY;
        PreviewDragSurface.CaptureMouse();
        e.Handled = true;
    }

    private void Preview_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_previewDragging || e.LeftButton != MouseButtonState.Pressed)
            return;

        var point = e.GetPosition(PreviewDragSurface);
        var targetX = Math.Clamp(
            _previewStartOffsetX + point.X - _previewDragStart.X,
            OffsetXSlider.Minimum,
            OffsetXSlider.Maximum);
        var targetY = Math.Clamp(
            _previewStartOffsetY + point.Y - _previewDragStart.Y,
            OffsetYSlider.Minimum,
            OffsetYSlider.Maximum);
        PreviewContentTranslate.X = targetX - _previewStartOffsetX;
        PreviewContentTranslate.Y = targetY - _previewStartOffsetY;
        OffsetXValueText.Text = $"{targetX:+0;-0;0} px";
        OffsetYValueText.Text = $"{targetY:+0;-0;0} px";
        e.Handled = true;
    }

    private void Preview_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_previewDragging)
            return;

        CommitPreviewDrag(e.GetPosition(PreviewDragSurface));
        PreviewDragSurface.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void Preview_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (!_previewDragging)
            return;

        CommitPreviewDrag(Mouse.GetPosition(PreviewDragSurface));
    }

    private void CommitPreviewDrag(Point point)
    {
        if (!_previewDragging)
            return;
        _previewDragging = false;

        var targetX = Math.Clamp(
            _previewStartOffsetX + point.X - _previewDragStart.X,
            OffsetXSlider.Minimum,
            OffsetXSlider.Maximum);
        var targetY = Math.Clamp(
            _previewStartOffsetY + point.Y - _previewDragStart.Y,
            OffsetYSlider.Minimum,
            OffsetYSlider.Maximum);
        SetViewportOffsets(targetX, targetY);
    }

    private void NudgeViewport_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string direction })
            return;

        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10d : 1d;
        var targetX = _draft.WidgetOffsetX;
        var targetY = _draft.WidgetOffsetY;
        switch (direction)
        {
            case "Left": targetX -= step; break;
            case "Right": targetX += step; break;
            case "Up": targetY -= step; break;
            case "Down": targetY += step; break;
            case "Reset": targetX = 0; targetY = 0; break;
        }

        targetX = Math.Clamp(targetX, OffsetXSlider.Minimum, OffsetXSlider.Maximum);
        targetY = Math.Clamp(targetY, OffsetYSlider.Minimum, OffsetYSlider.Maximum);
        PreviewContentTranslate.X += targetX - _draft.WidgetOffsetX;
        PreviewContentTranslate.Y += targetY - _draft.WidgetOffsetY;
        SetViewportOffsets(targetX, targetY);
    }

    private void Preview_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            return;

        var delta = e.Delta > 0 ? 5 : -5;
        ScaleSlider.Value = Math.Clamp(
            ScaleSlider.Value + delta,
            ScaleSlider.Minimum,
            ScaleSlider.Maximum);
        e.Handled = true;
    }

    private void SetViewportOffsets(double offsetX, double offsetY)
    {
        _initializing = true;
        OffsetXSlider.Value = offsetX;
        OffsetYSlider.Value = offsetY;
        _initializing = false;
        _draft.WidgetOffsetX = offsetX;
        _draft.WidgetOffsetY = offsetY;
        UpdateLabels();
        SchedulePreviewTransformFallback();
    }

    private async void SchedulePreviewTransformFallback()
    {
        var generation = ++_previewTransformGeneration;
        await Task.Delay(850);
        if (generation == _previewTransformGeneration && !_previewDragging)
            ResetPreviewTransform();
    }

    private void PreviewWidgetView_ViewportRendered(object? sender, EventArgs e)
    {
        if (!_previewDragging && PreviewWidgetView.IsViewportCurrent(_draft))
            ResetPreviewTransform();
    }

    private void ResetPreviewTransform()
    {
        _previewTransformGeneration++;
        PreviewContentTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        PreviewContentTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        PreviewContentTranslate.X = 0;
        PreviewContentTranslate.Y = 0;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_draft.Name))
        {
            ShowValidation("Введите название виджета");
            return;
        }

        if (_draft.WidgetContentType == WidgetContentKind.WebPage
            && !string.IsNullOrWhiteSpace(_draft.WidgetUrl)
            && !IsValidWebAddress(_draft.WidgetUrl))
        {
            ShowValidation("Проверьте адрес страницы — поддерживаются http:// и https://");
            return;
        }

        if (_draft.WidgetContentType != WidgetContentKind.WebPage
            && _draft.WidgetContentType != WidgetContentKind.LauncherGrid
            && !string.IsNullOrWhiteSpace(_draft.WidgetContentPath)
            && !File.Exists(Environment.ExpandEnvironmentVariables(_draft.WidgetContentPath)))
        {
            ShowValidation("Выбранный файл не найден");
            return;
        }

        if (_draft.IsWidgetLauncherGrid
            && !string.IsNullOrWhiteSpace(_draft.WidgetFolderPath)
            && !Directory.Exists(Environment.ExpandEnvironmentVariables(_draft.WidgetFolderPath)))
        {
            ShowValidation("Выбранная папка не найдена");
            return;
        }

        _source.Name = _draft.Name.Trim();
        _source.WidgetContentType = _draft.WidgetContentType;
        _source.WidgetUrl = _draft.WidgetUrl;
        _source.WidgetContentPath = _draft.WidgetContentPath;
        _source.WidgetClickActionPath = _draft.WidgetClickActionPath;
        _source.WidgetClickArguments = _draft.WidgetClickArguments;
        _source.WidgetCssSelector = _draft.WidgetCssSelector;
        _source.WidgetScale = _draft.WidgetScale;
        _source.WidgetOffsetX = _draft.WidgetOffsetX;
        _source.WidgetOffsetY = _draft.WidgetOffsetY;
        _source.WidgetContentOpacity = _draft.WidgetContentOpacity;
        _source.WidgetShowTitle = _draft.WidgetShowTitle;
        _source.WidgetInnerIconSize = _draft.WidgetInnerIconSize;
        _source.WidgetDisplayMode = _draft.WidgetDisplayMode;
        _source.WidgetFolderPath = _draft.WidgetFolderPath;
        _source.WidgetBackgroundColor = _draft.WidgetBackgroundColor;
        _source.Children = new ObservableCollection<LauncherItem>(_draft.Children.Select(CloneLauncherItem));
        DialogResult = true;
        Close();
    }

    private static bool IsValidWebAddress(string value)
    {
        var text = value.Trim();
        if (!text.Contains("://", StringComparison.Ordinal))
            text = "https://" + text;
        return Uri.TryCreate(text, UriKind.Absolute, out var uri)
               && uri.Scheme is "http" or "https";
    }

    private void ShowValidation(string message)
    {
        ValidationText.Text = message;
        ValidationText.Visibility = Visibility.Visible;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            try { DragMove(); } catch { }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
