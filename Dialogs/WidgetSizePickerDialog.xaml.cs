using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AppLauncher.Services;

namespace AppLauncher.Dialogs;

public partial class WidgetSizePickerDialog : Window
{
    private const double CellSize = 48;
    private readonly int _availableColumns;
    private readonly int _availableRows;
    private readonly List<Border> _cells = new();
    private bool _isSelecting;
    private int _anchorColumn;
    private int _anchorRow;
    private int _endColumn = -1;
    private int _endRow = -1;

    private WidgetSizePickerDialog(Window owner, int availableColumns, int availableRows)
    {
        InitializeComponent();
        Owner = owner;
        _availableColumns = Math.Max(1, availableColumns);
        _availableRows = Math.Max(1, availableRows);
        var darkMode = ApplyOwnerTheme(owner);
        SourceInitialized += (_, _) => NativeWindowService.ApplyModernWindowStyle(
            new WindowInteropHelper(this).Handle,
            darkMode);
        Loaded += WidgetSizePickerDialog_Loaded;
        PreviewKeyDown += WidgetSizePickerDialog_PreviewKeyDown;
        BuildSelectionGrid();
        FitToOwner(owner);
    }

    public int SelectedRows { get; private set; }
    public int SelectedColumns { get; private set; }

    public static bool TryPick(
        Window owner,
        int availableColumns,
        int availableRows,
        out int selectedRows,
        out int selectedColumns)
    {
        var dialog = new WidgetSizePickerDialog(owner, availableColumns, availableRows);
        var accepted = dialog.ShowDialog() == true;
        selectedRows = accepted ? dialog.SelectedRows : 0;
        selectedColumns = accepted ? dialog.SelectedColumns : 0;
        return accepted;
    }

    private void FitToOwner(Window owner)
    {
        var workArea = SystemParameters.WorkArea;
        var desiredWidth = Math.Clamp(_availableColumns * CellSize + 100, 580, 850);
        var desiredHeight = Math.Clamp(_availableRows * CellSize + 245, 520, 760);
        Width = Math.Min(desiredWidth, Math.Min(workArea.Width - 48, Math.Max(580, owner.ActualWidth * 0.72)));
        Height = Math.Min(desiredHeight, Math.Min(workArea.Height - 48, Math.Max(520, owner.ActualHeight * 0.78)));
    }

    private void BuildSelectionGrid()
    {
        SelectionGrid.Width = _availableColumns * CellSize;
        SelectionGrid.Height = _availableRows * CellSize;
        for (var column = 0; column < _availableColumns; column++)
            SelectionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(CellSize) });
        for (var row = 0; row < _availableRows; row++)
            SelectionGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(CellSize) });

        for (var row = 0; row < _availableRows; row++)
        {
            for (var column = 0; column < _availableColumns; column++)
            {
                var cell = new Border
                {
                    Margin = new Thickness(3),
                    CornerRadius = new CornerRadius(7),
                    BorderThickness = new Thickness(1),
                    Tag = new CellPosition(column, row),
                    IsHitTestVisible = false
                };
                cell.SetResourceReference(Border.BackgroundProperty, "PickerCellBrush");
                cell.SetResourceReference(Border.BorderBrushProperty, "PickerBorderBrush");
                Grid.SetColumn(cell, column);
                Grid.SetRow(cell, row);
                SelectionGrid.Children.Add(cell);
                _cells.Add(cell);
            }
        }

        AvailableGridText.Text = $"Доступно: {_availableRows} × {_availableColumns}";
    }

    private bool ApplyOwnerTheme(Window owner)
    {
        Resources["PickerSurfaceBrush"] = GetOwnerBrush(owner, "PanelBrush", "#181B24");
        Resources["PickerCardBrush"] = GetOwnerBrush(owner, "TileBrush", "#20232D");
        Resources["PickerTextBrush"] = GetOwnerBrush(owner, "TextBrush", "#FFFFFF");
        Resources["PickerMutedBrush"] = GetOwnerBrush(owner, "MutedTextBrush", "#A8FFFFFF");
        Resources["PickerBorderBrush"] = GetOwnerBrush(owner, "ColorSwatchBorderBrush", "#30FFFFFF");
        Resources["PickerCellBrush"] = GetOwnerBrush(owner, "TileBrush", "#0FFFFFFF");
        Resources["PickerCellHoverBrush"] = GetOwnerBrush(owner, "HoverBrush", "#227569FF");
        Resources["PickerAccentBrush"] = GetOwnerBrush(owner, "AccentSolidBrush", "#6E62FF");
        Resources["PickerSelectedBrush"] = CreateSelectionBrush(
            GetOwnerBrush(owner, "AccentSolidBrush", "#6E62FF"),
            0.78);
        Resources["PickerSelectedBorderBrush"] = CreateSelectionBrush(
            GetOwnerBrush(owner, "TextBrush", "#FFFFFF"),
            0.88);

        if (Resources["PickerSurfaceBrush"] is not SolidColorBrush surface)
            return true;
        var luminance = (0.2126 * surface.Color.R + 0.7152 * surface.Color.G + 0.0722 * surface.Color.B) / 255;
        return luminance < 0.5;
    }

    private static Brush CreateSelectionBrush(Brush source, double opacity)
    {
        if (source is SolidColorBrush solid)
        {
            var brush = new SolidColorBrush(solid.Color) { Opacity = opacity };
            brush.Freeze();
            return brush;
        }

        var clone = source.CloneCurrentValue();
        clone.Opacity = opacity;
        clone.Freeze();
        return clone;
    }

    private static Brush GetOwnerBrush(Window owner, string key, string fallback)
    {
        if (owner.Resources[key] is Brush brush)
            return brush;
        var fallbackBrush = (SolidColorBrush)new BrushConverter().ConvertFromString(fallback)!;
        fallbackBrush.Freeze();
        return fallbackBrush;
    }

    private void WidgetSizePickerDialog_Loaded(object sender, RoutedEventArgs e)
    {
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(155))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private void WidgetSizePickerDialog_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;
        e.Handled = true;
        DialogResult = false;
        Close();
    }

    private void SelectionGrid_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var cell = GetCell(e.GetPosition(SelectionGrid));
        _anchorColumn = cell.Column;
        _anchorRow = cell.Row;
        _endColumn = cell.Column;
        _endRow = cell.Row;
        _isSelecting = true;
        SelectionGrid.CaptureMouse();
        UpdateSelection();
        e.Handled = true;
    }

    private void SelectionGrid_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isSelecting || e.LeftButton != MouseButtonState.Pressed)
            return;
        var cell = GetCell(e.GetPosition(SelectionGrid));
        if (cell.Column == _endColumn && cell.Row == _endRow)
            return;
        _endColumn = cell.Column;
        _endRow = cell.Row;
        UpdateSelection();
        e.Handled = true;
    }

    private void SelectionGrid_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isSelecting)
            return;
        var cell = GetCell(e.GetPosition(SelectionGrid));
        _endColumn = cell.Column;
        _endRow = cell.Row;
        _isSelecting = false;
        SelectionGrid.ReleaseMouseCapture();
        UpdateSelection();
        e.Handled = true;
    }

    private void SelectionGrid_LostMouseCapture(object sender, MouseEventArgs e)
        => _isSelecting = false;

    private CellPosition GetCell(Point point)
    {
        var column = Math.Clamp(
            (int)Math.Floor(point.X / Math.Max(1, SelectionGrid.ActualWidth) * _availableColumns),
            0,
            _availableColumns - 1);
        var row = Math.Clamp(
            (int)Math.Floor(point.Y / Math.Max(1, SelectionGrid.ActualHeight) * _availableRows),
            0,
            _availableRows - 1);
        return new CellPosition(column, row);
    }

    private void UpdateSelection()
    {
        if (_endColumn < 0 || _endRow < 0)
            return;

        var left = Math.Min(_anchorColumn, _endColumn);
        var right = Math.Max(_anchorColumn, _endColumn);
        var top = Math.Min(_anchorRow, _endRow);
        var bottom = Math.Max(_anchorRow, _endRow);
        SelectedColumns = right - left + 1;
        SelectedRows = bottom - top + 1;

        foreach (var cell in _cells)
        {
            var position = (CellPosition)cell.Tag;
            var selected = position.Column >= left && position.Column <= right
                           && position.Row >= top && position.Row <= bottom;
            cell.SetResourceReference(
                Border.BackgroundProperty,
                selected ? "PickerSelectedBrush" : "PickerCellBrush");
            cell.SetResourceReference(
                Border.BorderBrushProperty,
                selected ? "PickerSelectedBorderBrush" : "PickerBorderBrush");
            cell.BorderThickness = selected ? new Thickness(1.35) : new Thickness(1);
        }

        SelectedRowsText.Text = SelectedRows.ToString();
        SelectedColumnsText.Text = SelectedColumns.ToString();
        SelectionHintText.Text = $"Виджет {SelectedRows} × {SelectedColumns}";
        CreateButton.IsEnabled = true;
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRows < 1 || SelectedColumns < 1)
            return;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
            return;
        try { DragMove(); } catch { }
    }

    private readonly record struct CellPosition(int Column, int Row);
}
