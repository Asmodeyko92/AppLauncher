using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace AppLauncher.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LauncherItemKind
{
    Application,
    Folder,
    Widget
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WidgetContentKind
{
    WebPage,
    TextFile,
    PdfFile,
    ImageFile,
    LauncherGrid
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WidgetLauncherDisplayMode
{
    Icons,
    List,
    Tiles
}

public sealed class LauncherItem : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private string _path = string.Empty;
    private string _arguments = string.Empty;
    private string _iconPath = string.Empty;
    private bool _runAsAdministrator;
    private long _launchCount;
    private WidgetContentKind _widgetContentKind;
    private string _widgetUrl = string.Empty;
    private string _widgetContentPath = string.Empty;
    private string _widgetClickActionPath = string.Empty;
    private string _widgetClickArguments = string.Empty;
    private string _widgetCssSelector = string.Empty;
    private double _widgetScale = 1.0;
    private double _widgetOffsetX;
    private double _widgetOffsetY;
    private double _widgetContentOpacity = 1.0;
    private bool _widgetShowTitle = true;
    private double _widgetInnerIconSize = 42;
    private WidgetLauncherDisplayMode _widgetLauncherDisplayMode;
    private string _widgetFolderPath = string.Empty;
    private string _widgetBackgroundColor = "#242730";
    private double _displayWidth = 130;
    private double _displayHeight = 138;
    private int _layoutColumn;
    private int _layoutRow;
    private ObservableCollection<LauncherItem> _children = new();

    public LauncherItem()
    {
        AttachChildren(_children);
    }

    public Guid Id { get; set; } = Guid.NewGuid();
    public LauncherItemKind Kind { get; set; } = LauncherItemKind.Application;

    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    public string Path
    {
        get => _path;
        set => SetField(ref _path, value);
    }

    public string Arguments
    {
        get => _arguments;
        set => SetField(ref _arguments, value);
    }

    public string IconPath
    {
        get => _iconPath;
        set => SetField(ref _iconPath, value);
    }

    public bool RunAsAdministrator
    {
        get => _runAsAdministrator;
        set => SetField(ref _runAsAdministrator, value);
    }

    public long LaunchCount
    {
        get => _launchCount;
        set => SetField(ref _launchCount, Math.Max(0, value));
    }

    public int WidgetRows { get; set; } = 4;
    public int WidgetColumns { get; set; } = 4;
    public int WidgetPage { get; set; } = -1;
    public int WidgetColumn { get; set; } = -1;
    public int WidgetRow { get; set; } = -1;

    public WidgetContentKind WidgetContentType
    {
        get => _widgetContentKind;
        set
        {
            if (_widgetContentKind == value)
                return;
            SetField(ref _widgetContentKind, value);
            NotifyWidgetContent();
        }
    }

    public string WidgetUrl
    {
        get => _widgetUrl;
        set
        {
            var normalizedValue = value?.Trim() ?? string.Empty;
            if (string.Equals(_widgetUrl, normalizedValue, StringComparison.Ordinal))
                return;
            SetField(ref _widgetUrl, normalizedValue);
            NotifyWidgetContent();
        }
    }

    public string WidgetContentPath
    {
        get => _widgetContentPath;
        set
        {
            var normalizedValue = value?.Trim() ?? string.Empty;
            if (string.Equals(_widgetContentPath, normalizedValue, StringComparison.Ordinal))
                return;
            SetField(ref _widgetContentPath, normalizedValue);
            NotifyWidgetContent();
        }
    }

    public string WidgetClickActionPath
    {
        get => _widgetClickActionPath;
        set
        {
            var normalizedValue = value?.Trim() ?? string.Empty;
            if (string.Equals(_widgetClickActionPath, normalizedValue, StringComparison.Ordinal))
                return;
            SetField(ref _widgetClickActionPath, normalizedValue);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasWidgetClickAction)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ToolTipText)));
        }
    }

    public string WidgetClickArguments
    {
        get => _widgetClickArguments;
        set => SetField(ref _widgetClickArguments, value?.Trim() ?? string.Empty);
    }

    public string WidgetCssSelector
    {
        get => _widgetCssSelector;
        set => SetField(ref _widgetCssSelector, value?.Trim() ?? string.Empty);
    }

    public double WidgetScale
    {
        get => _widgetScale;
        set => SetField(ref _widgetScale, Math.Clamp(value, 0.25, 3.0));
    }

    public double WidgetOffsetX
    {
        get => _widgetOffsetX;
        set => SetField(ref _widgetOffsetX, Math.Clamp(value, -2000, 2000));
    }

    public double WidgetOffsetY
    {
        get => _widgetOffsetY;
        set => SetField(ref _widgetOffsetY, Math.Clamp(value, -2000, 2000));
    }

    public double WidgetContentOpacity
    {
        get => _widgetContentOpacity;
        set => SetField(ref _widgetContentOpacity, Math.Clamp(value, 0.20, 1.0));
    }

    public bool WidgetShowTitle
    {
        get => _widgetShowTitle;
        set => SetField(ref _widgetShowTitle, value);
    }

    public double WidgetInnerIconSize
    {
        get => _widgetInnerIconSize;
        set
        {
            if (!SetField(ref _widgetInnerIconSize, Math.Clamp(value, 22, 96)))
                return;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WidgetInnerTileWidth)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WidgetInnerTileHeight)));
        }
    }

    public WidgetLauncherDisplayMode WidgetDisplayMode
    {
        get => _widgetLauncherDisplayMode;
        set
        {
            if (!SetField(ref _widgetLauncherDisplayMode, value))
                return;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WidgetInnerTileWidth)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WidgetInnerTileHeight)));
        }
    }

    public string WidgetFolderPath
    {
        get => _widgetFolderPath;
        set
        {
            var normalizedValue = value?.Trim() ?? string.Empty;
            if (!SetField(ref _widgetFolderPath, normalizedValue))
                return;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasWidgetFolderSource)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanEditWidgetChildren)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanAcceptWidgetChild)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ToolTipText)));
        }
    }

    public string WidgetBackgroundColor
    {
        get => _widgetBackgroundColor;
        set => SetField(ref _widgetBackgroundColor,
            string.IsNullOrWhiteSpace(value) ? "#242730" : value.Trim());
    }

    public ObservableCollection<LauncherItem> Children
    {
        get => _children;
        set
        {
            if (ReferenceEquals(_children, value))
                return;
            DetachChildren(_children);
            _children = value ?? new ObservableCollection<LauncherItem>();
            AttachChildren(_children);
            NotifyFolderVisuals();
        }
    }

    [JsonIgnore]
    public bool IsFolder => Kind == LauncherItemKind.Folder;

    [JsonIgnore]
    public bool IsWidget => Kind == LauncherItemKind.Widget;

    [JsonIgnore]
    public bool IsApplication => Kind == LauncherItemKind.Application;

    [JsonIgnore]
    public bool HasWidgetContent => HasWidgetDocumentContent || HasWidgetLauncherGridContent;

    [JsonIgnore]
    public bool HasWidgetDocumentContent => IsWidget
        && WidgetContentType != WidgetContentKind.LauncherGrid
        && (WidgetContentType == WidgetContentKind.WebPage
            ? !string.IsNullOrWhiteSpace(WidgetUrl)
            : !string.IsNullOrWhiteSpace(WidgetContentPath));

    [JsonIgnore]
    public bool IsWidgetLauncherGrid => IsWidget && WidgetContentType == WidgetContentKind.LauncherGrid;

    [JsonIgnore]
    public bool HasWidgetLauncherGridContent => IsWidgetLauncherGrid && Children.Count > 0;

    [JsonIgnore]
    public bool HasWidgetFolderSource => IsWidgetLauncherGrid && !string.IsNullOrWhiteSpace(WidgetFolderPath);

    [JsonIgnore]
    public bool CanEditWidgetChildren => !HasWidgetFolderSource;

    [JsonIgnore]
    public bool CanAcceptWidgetChild => IsWidget
        && !HasWidgetFolderSource
        && (IsWidgetLauncherGrid || !HasWidgetContent);

    [JsonIgnore]
    public bool HasWidgetClickAction => IsWidget && !string.IsNullOrWhiteSpace(WidgetClickActionPath);

    [JsonIgnore]
    public bool IsInFolder { get; set; }

    [JsonIgnore]
    public bool HasCustomIcon => !string.IsNullOrWhiteSpace(IconPath);

    [JsonIgnore]
    public bool HasFolderThumbnails => IsFolder && Children.Count > 0;

    [JsonIgnore]
    public IReadOnlyList<LauncherItem> FolderThumbnailItems
        => Children.OrderByDescending(child => child.LaunchCount).Take(4).ToList();

    [JsonIgnore]
    public string WidgetSizeText => $"{WidgetRows}×{WidgetColumns}";

    [JsonIgnore]
    public double WidgetInnerTileWidth => WidgetDisplayMode switch
    {
        WidgetLauncherDisplayMode.Tiles => Math.Clamp(WidgetInnerIconSize * 2.15 + 86, 152, 292),
        WidgetLauncherDisplayMode.List => 1,
        _ => WidgetInnerIconSize + 30
    };

    [JsonIgnore]
    public double WidgetInnerTileHeight => WidgetDisplayMode switch
    {
        WidgetLauncherDisplayMode.Tiles => Math.Max(66, WidgetInnerIconSize + 28),
        WidgetLauncherDisplayMode.List => Math.Max(48, WidgetInnerIconSize + 16),
        _ => WidgetInnerIconSize + 38
    };

    [JsonIgnore]
    public double DisplayWidth
    {
        get => _displayWidth;
        private set => SetField(ref _displayWidth, value);
    }

    [JsonIgnore]
    public double DisplayHeight
    {
        get => _displayHeight;
        private set => SetField(ref _displayHeight, value);
    }

    [JsonIgnore]
    public int LayoutColumn
    {
        get => _layoutColumn;
        private set => SetField(ref _layoutColumn, value);
    }

    [JsonIgnore]
    public int LayoutRow
    {
        get => _layoutRow;
        private set => SetField(ref _layoutRow, value);
    }

    [JsonIgnore]
    public string ToolTipText => IsFolder
        ? $"{Name}\n{Children.Count} приложений"
        : IsWidget
            ? HasWidgetContent
                ? $"{Name}\n{GetWidgetContentDescription()}"
                : $"{Name}\nЗаготовка виджета {WidgetSizeText}"
            : string.IsNullOrWhiteSpace(Path) ? Name : $"{Name}\n{Path}";

    public event PropertyChangedEventHandler? PropertyChanged;

    public void RefreshFolderThumbnails() => NotifyFolderVisuals();

    public void UpdateDisplaySize(double width, double height)
    {
        DisplayWidth = Math.Max(1, width);
        DisplayHeight = Math.Max(1, height);
    }

    public void UpdateGridPlacement(int column, int row)
    {
        LayoutColumn = Math.Max(0, column);
        LayoutRow = Math.Max(0, row);
    }

    private void AttachChildren(ObservableCollection<LauncherItem> children)
    {
        children.CollectionChanged += Children_CollectionChanged;
        foreach (var child in children)
            child.PropertyChanged += Child_PropertyChanged;
    }

    private void DetachChildren(ObservableCollection<LauncherItem> children)
    {
        children.CollectionChanged -= Children_CollectionChanged;
        foreach (var child in children)
            child.PropertyChanged -= Child_PropertyChanged;
    }

    private void Children_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (LauncherItem child in e.OldItems)
                child.PropertyChanged -= Child_PropertyChanged;
        if (e.NewItems is not null)
            foreach (LauncherItem child in e.NewItems)
                child.PropertyChanged += Child_PropertyChanged;
        NotifyFolderVisuals();
    }

    private void Child_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LaunchCount) or nameof(IconPath) or nameof(Path))
            NotifyFolderVisuals();
    }

    private void NotifyFolderVisuals()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasFolderThumbnails)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FolderThumbnailItems)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ToolTipText)));
        if (IsWidget)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasWidgetContent)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasWidgetLauncherGridContent)));
        }
    }

    private void NotifyWidgetContent()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasWidgetContent)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasWidgetDocumentContent)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsWidgetLauncherGrid)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasWidgetLauncherGridContent)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasWidgetFolderSource)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanEditWidgetChildren)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanAcceptWidgetChild)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ToolTipText)));
    }

    private string GetWidgetContentDescription() => WidgetContentType switch
    {
        WidgetContentKind.LauncherGrid => HasWidgetFolderSource
            ? $"Папка: {WidgetFolderPath} · элементов: {Children.Count}"
            : $"Значков: {Children.Count}",
        WidgetContentKind.TextFile => $"Текст: {WidgetContentPath}",
        WidgetContentKind.PdfFile => $"PDF: {WidgetContentPath}",
        WidgetContentKind.ImageFile => $"Изображение: {WidgetContentPath}",
        _ => WidgetUrl
    };

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        if (propertyName == nameof(Name) || propertyName == nameof(Path) || propertyName == nameof(IconPath))
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ToolTipText)));
        if (propertyName == nameof(IconPath))
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasCustomIcon)));
        return true;
    }
}
