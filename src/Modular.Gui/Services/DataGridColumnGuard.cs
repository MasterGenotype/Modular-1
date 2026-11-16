using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;

namespace Modular.Gui.Services;

/// <summary>
/// Keeps user column resizing from wrecking a grid, and keeps every value readable.
/// </summary>
/// <remarks>
/// When a column is dragged wider, the DataGrid takes the space from every resizable
/// column to its right: star columns first, then fixed ones, each down to its MinWidth,
/// which defaults to 20. A single drag on the checkbox or thumbnail column's edge (an
/// easy slip with a touchscreen or trackpad under gamescope) therefore collapses Name,
/// Author, Downloads and the rest into unreadable slivers.
///
/// Every grid gets this when it loads:
/// - A column with no header text (checkbox, thumbnail, row buttons) is fixed at its
///   declared width. It can't be dragged, and the grid never squeezes it either.
/// - A text column's MinWidth is the width of its header and of the longest value in the
///   rows loaded so far. A drag stops at the end of the longest name instead of cutting
///   it off, and when the columns no longer fit, the grid scrolls sideways. The width
///   grows as more rows load and resets when the results are replaced (a new search).
/// </remarks>
public static class DataGridColumnGuard
{
    private const double ContentFloor = 56;
    private const double StarFloor = 160;

    // A single value never makes its column wider than this; past it the cell trims.
    private const double ContentCap = 720;

    // Header font is 11px SemiBold (Themes/Modular.axaml). Besides the text, a Fluent
    // header holds 12 of left padding, 32 kept for the sort arrow and a 1 separator.
    // The text is measured with 10% to spare, as the drawn SemiBold runs wider.
    private const double HeaderFontSize = 11;
    private const double HeaderChrome = 46;
    private const double HeaderTextSpare = 1.1;

    // Cell edges: the grid line and a little breathing room after the text.
    private const double CellSlack = 6;

    private static readonly ConditionalWeakTable<DataGrid, GridState> States = new();

    public static void Register()
    {
        Control.LoadedEvent.AddClassHandler<DataGrid>(OnLoaded);
    }

    private static void OnLoaded(DataGrid grid, RoutedEventArgs e)
    {
        if (States.TryGetValue(grid, out _))
        {
            return;
        }

        var state = new GridState(grid);
        States.Add(grid, state);
        state.Attach();
    }

    private sealed class GridState(DataGrid grid)
    {
        private readonly Dictionary<DataGridColumn, double> _floors = new();
        private readonly Dictionary<DataGridColumn, double> _declared = new();
        private readonly HashSet<DataGridRow> _rows = new();
        private INotifyCollectionChanged? _items;
        private bool _fitQueued;

        public void Attach()
        {
            foreach (var column in grid.Columns)
            {
                Guard(column);
            }

            grid.LoadingRow += (_, e) =>
            {
                _rows.Add(e.Row);
                QueueFit();
            };
            grid.UnloadingRow += (_, e) => _rows.Remove(e.Row);

            grid.PropertyChanged += (_, e) =>
            {
                if (e.Property == DataGrid.ItemsSourceProperty)
                {
                    WatchItems();
                    Reset();
                }
            };
            WatchItems();
            QueueFit();
        }

        private void Guard(DataGridColumn column)
        {
            var width = column.Width;
            var header = column.Header as string;

            if (string.IsNullOrWhiteSpace(header) && column.Header is not Control)
            {
                if (width.IsAbsolute)
                {
                    // Not resizable is enough: the grid skips such columns when it takes
                    // space, and only ever hands spare space to star columns. (Setting
                    // MaxWidth here throws while the column is still unmeasured.)
                    column.CanUserResize = false;
                    column.MinWidth = width.Value;
                }
                return;
            }

            // A fixed column never goes below its declared width. Otherwise widening one
            // column (by hand or to fit a long name) squeezes its neighbours instead of
            // letting the grid scroll sideways.
            var headerWidth = MeasureText(header ?? "", new Typeface(grid.FontFamily, FontStyle.Normal, FontWeight.SemiBold), HeaderFontSize) * HeaderTextSpare + HeaderChrome;
            var floor = width.IsStar ? StarFloor : Math.Max(ContentFloor, headerWidth);
            if (width.IsAbsolute)
            {
                floor = Math.Max(floor, width.Value);
                _declared[column] = width.Value;
            }

            floor = Math.Max(floor, column.MinWidth);
            _floors[column] = floor;
            column.MinWidth = floor;
        }

        private void WatchItems()
        {
            if (_items is not null)
            {
                _items.CollectionChanged -= OnItemsChanged;
            }

            _items = grid.ItemsSource as INotifyCollectionChanged;
            if (_items is not null)
            {
                _items.CollectionChanged += OnItemsChanged;
            }
        }

        private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                Reset();
            }
        }

        private void Reset()
        {
            foreach (var (column, floor) in _floors)
            {
                var grownTo = column.MinWidth;
                column.MinWidth = floor;

                // A column that grew only to fit the old results settles back to its
                // declared width. One the user dragged wider than that keeps its width.
                if (column.Width.IsAbsolute && grownTo > floor &&
                    Math.Abs(column.Width.Value - grownTo) < 1 &&
                    _declared.TryGetValue(column, out var declared))
                {
                    column.Width = new DataGridLength(Math.Max(floor, declared));
                }
            }

            QueueFit();
        }

        private void QueueFit()
        {
            if (_fitQueued)
            {
                return;
            }

            _fitQueued = true;

            // Run after the new rows have bound and laid out, so their text is there.
            Dispatcher.UIThread.Post(() =>
            {
                _fitQueued = false;
                Fit();
            }, DispatcherPriority.Background);
        }

        private void Fit()
        {
            foreach (var (column, floor) in _floors)
            {
                var widest = floor;
                foreach (var row in _rows)
                {
                    if (column.GetCellContent(row) is { } content)
                    {
                        widest = Math.Max(widest, Math.Min(ContentCap, TextWidth(content)));
                    }
                }

                if (widest > column.MinWidth)
                {
                    column.MinWidth = Math.Ceiling(widest);
                }
            }
        }

    }

    private static double TextWidth(Control content)
    {
        var block = content as TextBlock
            ?? (content as Panel)?.Children.OfType<TextBlock>().FirstOrDefault()
            ?? (content as Decorator)?.Child as TextBlock;

        if (block is null || string.IsNullOrEmpty(block.Text))
        {
            return 0;
        }

        var cell = content.Parent as DataGridCell;
        return MeasureText(block.Text, new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch), block.FontSize)
            + block.Margin.Left + block.Margin.Right
            + block.Padding.Left + block.Padding.Right
            + (cell?.Padding.Left ?? 0) + (cell?.Padding.Right ?? 0)
            + CellSlack;
    }

    private static double MeasureText(string text, Typeface typeface, double fontSize)
    {
        using var layout = new TextLayout(text, typeface, fontSize, foreground: null);
        return layout.WidthIncludingTrailingWhitespace;
    }
}
