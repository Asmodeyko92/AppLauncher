using System.Windows;
using System.Windows.Controls;
using AppLauncher.Models;
using AppLauncher.Services;

namespace AppLauncher.Controls;

public sealed class WidgetGridPanel : Panel
{
    public static readonly DependencyProperty CellWidthProperty = DependencyProperty.Register(
        nameof(CellWidth),
        typeof(double),
        typeof(WidgetGridPanel),
        new FrameworkPropertyMetadata(140d,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty CellHeightProperty = DependencyProperty.Register(
        nameof(CellHeight),
        typeof(double),
        typeof(WidgetGridPanel),
        new FrameworkPropertyMetadata(148d,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public double CellWidth
    {
        get => (double)GetValue(CellWidthProperty);
        set => SetValue(CellWidthProperty, value);
    }

    public double CellHeight
    {
        get => (double)GetValue(CellHeightProperty);
        set => SetValue(CellHeightProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var cellWidth = Math.Max(1, CellWidth);
        var cellHeight = Math.Max(1, CellHeight);
        var width = double.IsInfinity(availableSize.Width)
            ? InternalChildren.Cast<UIElement>().Select(GetDesiredColumnSpan).DefaultIfEmpty(1).Max() * cellWidth
            : availableSize.Width;
        var height = double.IsInfinity(availableSize.Height)
            ? InternalChildren.Cast<UIElement>().Select(GetDesiredRowSpan).DefaultIfEmpty(1).Max() * cellHeight
            : availableSize.Height;

        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(
                GetDesiredColumnSpan(child) * cellWidth,
                GetDesiredRowSpan(child) * cellHeight));
        }

        return new Size(Math.Max(0, width), Math.Max(0, height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var cellWidth = Math.Max(1, CellWidth);
        var cellHeight = Math.Max(1, CellHeight);
        var columns = Math.Max(1, (int)Math.Floor(finalSize.Width / cellWidth));
        var rows = Math.Max(1, (int)Math.Floor(finalSize.Height / cellHeight));
        foreach (UIElement child in InternalChildren)
        {
            var columnSpan = GetDesiredColumnSpan(child);
            var rowSpan = GetDesiredRowSpan(child);
            if (GetItem(child) is { } item)
            {
                var column = Math.Clamp(item.LayoutColumn, 0, Math.Max(0, columns - columnSpan));
                var row = Math.Clamp(item.LayoutRow, 0, Math.Max(0, rows - rowSpan));
                child.Arrange(new Rect(
                    column * cellWidth,
                    row * cellHeight,
                    columnSpan * cellWidth,
                    rowSpan * cellHeight));
            }
            else
            {
                child.Arrange(Rect.Empty);
            }
        }

        return finalSize;
    }

    private static LauncherItem? GetItem(UIElement child)
        => child is ContentPresenter { Content: LauncherItem item }
            ? item
            : (child as FrameworkElement)?.DataContext as LauncherItem;

    private static int GetDesiredColumnSpan(UIElement child)
        => GetItem(child) is { } item ? GridPackingService.GetSpan(item).ColumnSpan : 1;

    private static int GetDesiredRowSpan(UIElement child)
        => GetItem(child) is { } item ? GridPackingService.GetSpan(item).RowSpan : 1;
}
