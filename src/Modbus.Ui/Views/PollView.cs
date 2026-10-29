using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Modbus.Ui;
using Modbus.Core;
using Modbus.Core.Polling;
using Modbus.Core.Protocol;

namespace Modbus.Ui.Views;

public sealed class CellRow : INotifyPropertyChanged
{
    private readonly PollDocument _doc;
    private string _value = "";
    private Brush _background = Brushes.Transparent;
    private Brush _foreground = Brushes.Black;

    public CellRow(PollDocument doc, int index)
    {
        _doc = doc;
        Index = index;
    }

    public int Index { get; }

    public string Address
    {
        get
        {
            var d = _doc.Definition;
            var a = d.Address + Index;
            return d.PlcAddresses ? (a + FunctionCodes.PlcBase(d.Function)).ToString() : a.ToString();
        }
    }

    public string Name
    {
        get => _doc.PeekCell(Index)?.Name ?? "";
        set
        {
            if (value == Name) return;
            _doc.Cell(Index).Name = value;
            OnChanged();
        }
    }

    public string Value
    {
        get => _value;
        set { if (_value != value) { _value = value; OnChanged(); } }
    }

    public Brush Background
    {
        get => _background;
        set { if (!Equals(_background, value)) { _background = value; OnChanged(); } }
    }

    public Brush Foreground
    {
        get => _foreground;
        set { if (!Equals(_foreground, value)) { _foreground = value; OnChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>One data window: status lines plus a grid of Name / Address / Value.</summary>
public sealed class PollView : DockPanel
{
    private readonly ObservableCollection<CellRow> _rows = [];
    private readonly TextBlock _status = new() { Margin = new Thickness(6, 4, 6, 0), FontFamily = new FontFamily("Consolas") };
    private readonly TextBlock _error = new() { Margin = new Thickness(6, 0, 6, 4), Foreground = Brushes.Firebrick, FontWeight = FontWeights.SemiBold };
    private readonly DispatcherTimer _refresh;
    private volatile bool _dirty = true;
    private bool _refreshEnabled = true;

    public PollView(PollDocument document)
    {
        Document = document;
        LastChildFill = true;

        var header = new StackPanel { Background = new SolidColorBrush(Color.FromRgb(0xF4, 0xF6, 0xF8)) };
        header.Children.Add(_status);
        header.Children.Add(_error);
        SetDock(header, Dock.Top);
        Children.Add(header);

        Grid = new DataGrid
        {
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            CanUserSortColumns = false,
            CanUserReorderColumns = false,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            SelectionMode = DataGridSelectionMode.Extended,
            SelectionUnit = DataGridSelectionUnit.FullRow,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            HorizontalGridLinesBrush = new SolidColorBrush(Color.FromRgb(0xE3, 0xE6, 0xEA)),
            RowHeaderWidth = 0,
            EnableRowVirtualization = true,
            ItemsSource = _rows,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            BorderThickness = new Thickness(0),
        };
        Grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Name", Binding = new Binding(nameof(CellRow.Name)) { UpdateSourceTrigger = UpdateSourceTrigger.LostFocus }, Width = 180,
        });
        Grid.Columns.Add(new DataGridTextColumn { Header = "Address", Binding = new Binding(nameof(CellRow.Address)), IsReadOnly = true, Width = 80 });

        var valueStyle = new Style(typeof(TextBlock));
        valueStyle.Setters.Add(new Setter(TextBlock.BackgroundProperty, new Binding(nameof(CellRow.Background))));
        valueStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, new Binding(nameof(CellRow.Foreground))));
        valueStyle.Setters.Add(new Setter(TextBlock.PaddingProperty, new Thickness(4, 1, 4, 1)));
        Grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Value", Binding = new Binding(nameof(CellRow.Value)), IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            ElementStyle = valueStyle, MinWidth = 140,
        });

        Grid.MouseDoubleClick += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject src && FindParent<DataGridCell>(src) is { Column.DisplayIndex: 2 })
                EditRequested?.Invoke(this);
        };
        Grid.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Grid.CurrentColumn?.DisplayIndex != 0)
            {
                EditRequested?.Invoke(this);
                e.Handled = true;
            }
        };
        Children.Add(Grid);

        document.Updated += _ => _dirty = true;
        _refresh = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) => RefreshIfDirty(), Dispatcher);
        _refresh.Start();
        Rebuild();
    }

    public PollDocument Document { get; }
    public DataGrid Grid { get; }

    /// <summary>False shows <see cref="OfflineText"/> instead of the last result.</summary>
    public Func<bool> IsOnline { get; set; } = () => true;
    public string OfflineText { get; set; } = "No connection";

    /// <summary>Replaces the status line (Tx / Err / ID / F / SR).</summary>
    public Func<PollDocument, string>? StatusText { get; set; }

    /// <summary>Replaces the red error line; return "" for none.</summary>
    public Func<PollDocument, string>? ErrorText { get; set; }

    /// <summary>Logging attached to this window, if any.</summary>
    public DataLogger? Logger { get; set; }

    /// <summary>The user asked to change the selected value (double click / Enter).</summary>
    public event Action<PollView>? EditRequested;

    /// <summary>False while automation sets many cells (EnableRefresh = False).</summary>
    public bool RefreshEnabled
    {
        get => _refreshEnabled;
        set
        {
            _refreshEnabled = value;
            if (value) Invalidate();
        }
    }

    public IReadOnlyList<int> SelectedIndices
    {
        get
        {
            var list = Grid.SelectedItems.Cast<CellRow>().Select(r => r.Index).OrderBy(i => i).ToList();
            if (list.Count == 0 && Grid.CurrentItem is CellRow current) list.Add(current.Index);
            return list;
        }
    }

    public void Invalidate() => _dirty = true;

    /// <summary>Recreates the rows after a definition change.</summary>
    public void Rebuild()
    {
        var count = Document.Definition.CellCount;
        if (_rows.Count != count)
        {
            _rows.Clear();
            for (var i = 0; i < count; i++) _rows.Add(new CellRow(Document, i));
        }
        else
        {
            foreach (var r in _rows) { r.OnChanged(nameof(CellRow.Address)); r.OnChanged(nameof(CellRow.Name)); }
        }
        _dirty = true;
        RefreshIfDirty();
    }

    private void RefreshIfDirty()
    {
        if (!_dirty || !_refreshEnabled) return;
        _dirty = false;
        var d = Document;
        var def = d.Definition;
        _status.Text = StatusText?.Invoke(d) ?? $"Tx = {d.TxCount}: Err = {d.ErrorCount}: {def.Describe()}{(def.Disabled ? "  [disabled]" : "")}";

        _error.Text = ErrorText is not null ? ErrorText(d)
            : !IsOnline() ? OfflineText
            : d.LastResult switch
            {
                ModbusResult.Success => "",
                ModbusResult.DataUninitialized => def.Disabled ? "Read/Write disabled. Press F6 for one request." : "",
                _ => d.LastError.Length > 0 ? d.LastError : ModbusResults.Describe(d.LastResult),
            };
        _error.Visibility = _error.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        var values = def.IsWriteWindow ? d.WriteValues : d.Values;
        foreach (var row in _rows)
        {
            if (row.Index >= values.Length)
            {
                row.Value = "";
                continue;
            }
            if (d.IsContinuation(row.Index))
            {
                row.Value = "";
                row.Background = Brushes.Transparent;
                continue;
            }
            row.Value = d.FormatCell(row.Index, values);
            var colors = d.PeekCell(row.Index)?.Colors;
            if (colors is not null)
            {
                var (bg, fg) = colors.Evaluate(d.NumericValue(row.Index, values));
                row.Background = ColorUtil.Brush(bg);
                row.Foreground = ColorUtil.Brush(fg);
            }
            else
            {
                row.Background = Brushes.Transparent;
                row.Foreground = Brushes.Black;
            }
        }
    }

    public void Stop()
    {
        _refresh.Stop();
        Logger?.Dispose();
        Logger = null;
    }

    private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        var current = child;
        while (current is not null and not T) current = VisualTreeHelper.GetParent(current);
        return current as T;
    }
}
