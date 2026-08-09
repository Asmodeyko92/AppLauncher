using AppLauncher.Models;

namespace AppLauncher.Services;

internal static class GridPackingService
{
    public static (int ColumnSpan, int RowSpan) GetSpan(LauncherItem item)
        => item.IsWidget
            ? (Math.Max(1, item.WidgetColumns), Math.Max(1, item.WidgetRows))
            : (1, 1);

    public static bool TryPlace(
        bool[,] occupied,
        int columns,
        int rows,
        int columnSpan,
        int rowSpan,
        out int column,
        out int row)
    {
        column = 0;
        row = 0;
        if (columnSpan > columns || rowSpan > rows)
            return false;

        for (var candidateRow = 0; candidateRow <= rows - rowSpan; candidateRow++)
        {
            for (var candidateColumn = 0; candidateColumn <= columns - columnSpan; candidateColumn++)
            {
                if (!IsAreaFree(occupied, candidateColumn, candidateRow, columnSpan, rowSpan))
                    continue;

                MarkArea(occupied, candidateColumn, candidateRow, columnSpan, rowSpan);
                column = candidateColumn;
                row = candidateRow;
                return true;
            }
        }

        return false;
    }

    public static bool TryPlaceAt(
        bool[,] occupied,
        int columns,
        int rows,
        int columnSpan,
        int rowSpan,
        int column,
        int row)
    {
        if (column < 0 || row < 0
            || column + columnSpan > columns
            || row + rowSpan > rows
            || !IsAreaFree(occupied, column, row, columnSpan, rowSpan))
            return false;

        MarkArea(occupied, column, row, columnSpan, rowSpan);
        return true;
    }

    public static bool TryPlaceNearest(
        bool[,] occupied,
        int columns,
        int rows,
        int columnSpan,
        int rowSpan,
        int preferredColumn,
        int preferredRow,
        out int column,
        out int row)
    {
        column = 0;
        row = 0;
        if (columnSpan > columns || rowSpan > rows)
            return false;

        var candidates = new List<(int Column, int Row, int Distance)>();
        for (var candidateRow = 0; candidateRow <= rows - rowSpan; candidateRow++)
        {
            for (var candidateColumn = 0; candidateColumn <= columns - columnSpan; candidateColumn++)
            {
                candidates.Add((
                    candidateColumn,
                    candidateRow,
                    Math.Abs(candidateColumn - preferredColumn) + Math.Abs(candidateRow - preferredRow)));
            }
        }

        foreach (var candidate in candidates
                     .OrderBy(candidate => candidate.Distance)
                     .ThenBy(candidate => candidate.Row)
                     .ThenBy(candidate => candidate.Column))
        {
            if (!TryPlaceAt(
                    occupied, columns, rows, columnSpan, rowSpan,
                    candidate.Column, candidate.Row))
                continue;

            column = candidate.Column;
            row = candidate.Row;
            return true;
        }

        return false;
    }

    private static bool IsAreaFree(bool[,] occupied, int column, int row, int columnSpan, int rowSpan)
    {
        for (var y = row; y < row + rowSpan; y++)
            for (var x = column; x < column + columnSpan; x++)
                if (occupied[y, x])
                    return false;
        return true;
    }

    private static void MarkArea(bool[,] occupied, int column, int row, int columnSpan, int rowSpan)
    {
        for (var y = row; y < row + rowSpan; y++)
            for (var x = column; x < column + columnSpan; x++)
                occupied[y, x] = true;
    }
}
