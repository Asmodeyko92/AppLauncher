using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AppLauncher.Models;
using AppLauncher.Services;

namespace AppLauncher.Controls;

public sealed class LauncherItemInvokedEventArgs(LauncherItem item) : EventArgs
{
    public LauncherItem Item { get; } = item;
}

public sealed class LauncherItemDragStateEventArgs(LauncherItem item, bool isActive) : EventArgs
{
    public LauncherItem Item { get; } = item;
    public bool IsActive { get; } = isActive;
}

public partial class WidgetLauncherGridView : UserControl
{
    private const string WidgetChildDragFormat = "AppLauncher.WidgetChild";
    public const string LauncherItemDragFormat = "AppLauncher.LauncherItem";
    private LauncherItem? _pressedItem;
    private ObservableCollection<LauncherItem>? _visibleItems;
    private Point _dragStart;
    private readonly Stack<LauncherItem> _folderStack = new();

    public static readonly DependencyProperty IsEditingProperty = DependencyProperty.Register(
        nameof(IsEditing),
        typeof(bool),
        typeof(WidgetLauncherGridView),
        new PropertyMetadata(false));

    public WidgetLauncherGridView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            _folderStack.Clear();
            ShowCurrentFolder();
        };
    }

    public bool IsEditing
    {
        get => (bool)GetValue(IsEditingProperty);
        set => SetValue(IsEditingProperty, value);
    }

    public event EventHandler<LauncherItemInvokedEventArgs>? ItemInvoked;
    public event EventHandler<LauncherItemDragStateEventArgs>? DragStateChanged;
    public event EventHandler? LayoutChanged;

    private void Root_MouseEnter(object sender, MouseEventArgs e)
    {
        if (DataContext is LauncherItem widget && WidgetFolderService.Refresh(widget))
            LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    private void FolderBack_Click(object sender, RoutedEventArgs e)
    {
        _folderStack.Pop();
        ShowCurrentFolder();
    }

    private void Child_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (IsEditing || sender is not Button { DataContext: LauncherItem item })
            return;
        if (item.IsFolder)
        {
            WidgetFolderService.RefreshFolder(item);
            _folderStack.Push(item);
            ShowCurrentFolder();
            return;
        }
        ItemInvoked?.Invoke(this, new LauncherItemInvokedEventArgs(item));
    }

    private void RemoveChild_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (!IsEditing
            || sender is not Button { DataContext: LauncherItem item })
            return;
        if (CurrentItems?.Remove(item) == true)
            LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Child_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is LauncherItem { HasWidgetFolderSource: true })
            return;
        if (sender is not Button { DataContext: LauncherItem item })
            return;
        _pressedItem = item;
        _dragStart = e.GetPosition(this);
    }

    private void Child_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (DataContext is LauncherItem { HasWidgetFolderSource: true })
            return;
        if (_pressedItem is null || e.LeftButton != MouseButtonState.Pressed)
            return;
        var point = e.GetPosition(this);
        if (Math.Abs(point.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(point.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var item = _pressedItem;
        _pressedItem = null;
        var data = new DataObject();
        data.SetData(WidgetChildDragFormat, item);
        // Главная сетка использует тот же формат для обычных плиток. Благодаря
        // второму представлению один drag можно завершить как внутри виджета,
        // так и снаружи — на любой странице AppLauncher.
        data.SetData(LauncherItemDragFormat, item);
        DragStateChanged?.Invoke(this, new LauncherItemDragStateEventArgs(item, true));
        try
        {
            DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Move);
        }
        finally
        {
            DragStateChanged?.Invoke(this, new LauncherItemDragStateEventArgs(item, false));
        }
    }

    private void Child_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(WidgetChildDragFormat))
            return;
        if (e.Data.GetData(WidgetChildDragFormat) is not LauncherItem source
            || sender is not Button { DataContext: LauncherItem target })
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        e.Effects = ReferenceEquals(source, target) ? DragDropEffects.None : DragDropEffects.Move;
        e.Handled = true;
    }

    private void Child_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(WidgetChildDragFormat))
            return;
        if (CurrentItems is not { } currentItems
            || e.Data.GetData(WidgetChildDragFormat) is not LauncherItem source
            || sender is not Button { DataContext: LauncherItem target }
            || ReferenceEquals(source, target))
        {
            e.Handled = true;
            return;
        }

        if (target.IsFolder && source.IsApplication)
        {
            currentItems.Remove(source);
            target.Children.Add(source);
            LayoutChanged?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            var sourceIndex = currentItems.IndexOf(source);
            var targetIndex = currentItems.IndexOf(target);
            if (sourceIndex < 0 || targetIndex < 0)
                return;
            currentItems.Move(sourceIndex, targetIndex);
            LayoutChanged?.Invoke(this, EventArgs.Empty);
        }
        e.Handled = true;
    }

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(WidgetChildDragFormat))
            return;
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void Root_Drop(object sender, DragEventArgs e)
    {
        if (CurrentItems is not { } currentItems
            || e.Data.GetData(WidgetChildDragFormat) is not LauncherItem source)
            return;
        var index = currentItems.IndexOf(source);
        if (index >= 0 && index != currentItems.Count - 1)
        {
            currentItems.Move(index, currentItems.Count - 1);
            LayoutChanged?.Invoke(this, EventArgs.Empty);
        }
        e.Handled = true;
    }

    private ObservableCollection<LauncherItem>? CurrentItems
        => _folderStack.Count > 0
            ? _folderStack.Peek().Children
            : (DataContext as LauncherItem)?.Children;

    private void ShowCurrentFolder()
    {
        if (_visibleItems is not null)
            _visibleItems.CollectionChanged -= VisibleItems_CollectionChanged;

        _visibleItems = CurrentItems;
        InnerItems.ItemsSource = _visibleItems;
        if (_visibleItems is not null)
            _visibleItems.CollectionChanged += VisibleItems_CollectionChanged;

        var folder = _folderStack.Count > 0 ? _folderStack.Peek() : null;
        FolderNavigationBar.Visibility = folder is null ? Visibility.Collapsed : Visibility.Visible;
        FolderTitle.Text = folder?.Name ?? string.Empty;
        UpdateEmptyState();
    }

    private void VisibleItems_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => UpdateEmptyState();

    private void UpdateEmptyState()
        => EmptyState.Visibility = _visibleItems is { Count: 0 } ? Visibility.Visible : Visibility.Collapsed;
}
