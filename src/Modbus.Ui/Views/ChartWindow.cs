using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Modbus.Ui;
using Modbus.Core;
using Modbus.Core.Polling;

namespace Modbus.Ui.Views;

public sealed class ChartSeries
{
    public const int MaxPoints = 1_000_000;

    public required int CellIndex { get; init; }
    public required string Name { get; set; }
    public required Color Color { get; init; }
    public List<(DateTime Time, double Value)> Points { get; } = [];
}

/// <summary>Line chart with time axis. Wheel = zoom, drag = pan, double click = follow live data.</summary>
public sealed class ChartControl : FrameworkElement
{
    private static readonly Typeface Font = new("Segoe UI");
    private Point? _dragStart;
    private DateTime _dragViewEnd;

    public List<ChartSeries> Series { get; } = [];
    public object SeriesLock { get; } = new();
    public TimeSpan Span { get; set; } = TimeSpan.FromSeconds(60);
    public DateTime ViewEnd { get; set; } = DateTime.Now;
    public bool FollowLive { get; set; } = true;
    public double? YMin { get; set; }
    public double? YMax { get; set; }

    public ChartControl()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var plot = PlotRect();
        var x = Math.Clamp((e.GetPosition(this).X - plot.Left) / plot.Width, 0, 1);
        var start = ViewEnd - Span;
        var anchor = start + TimeSpan.FromTicks((long)(Span.Ticks * x));
        var factor = e.Delta > 0 ? 0.8 : 1.25;
        var newSpan = TimeSpan.FromTicks(Math.Clamp((long)(Span.Ticks * factor), TimeSpan.FromMilliseconds(200).Ticks, TimeSpan.FromDays(30).Ticks));
        ViewEnd = anchor + TimeSpan.FromTicks((long)(newSpan.Ticks * (1 - x)));
        Span = newSpan;
        if (e.Delta > 0) FollowLive = false;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        if (e.ClickCount == 2)
        {
            FollowLive = true;
            ViewEnd = DateTime.Now;
            InvalidateVisual();
            return;
        }
        _dragStart = e.GetPosition(this);
        _dragViewEnd = ViewEnd;
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragStart is not { } start || !IsMouseCaptured) return;
        var plot = PlotRect();
        var dx = e.GetPosition(this).X - start.X;
        ViewEnd = _dragViewEnd - TimeSpan.FromTicks((long)(Span.Ticks * dx / Math.Max(1, plot.Width)));
        FollowLive = false;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _dragStart = null;
        ReleaseMouseCapture();
    }

    private Rect PlotRect() => new(60, 10, Math.Max(10, ActualWidth - 75), Math.Max(10, ActualHeight - 40));

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (FollowLive) ViewEnd = DateTime.Now;
        var plot = PlotRect();
        var start = ViewEnd - Span;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // Y range from visible data.
        double lo = double.MaxValue, hi = double.MinValue;
        lock (SeriesLock)
        {
            foreach (var s in Series)
                foreach (var (t, v) in Visible(s.Points, start, ViewEnd))
                {
                    if (double.IsNaN(v)) continue;
                    lo = Math.Min(lo, v);
                    hi = Math.Max(hi, v);
                }
        }
        if (lo == double.MaxValue) { lo = 0; hi = 100; }
        if (YMin is { } yMin) lo = yMin;
        if (YMax is { } yMax) hi = yMax;
        if (hi - lo < 1e-9) { hi += 1; lo -= 1; }
        else if (YMin is null && YMax is null)
        {
            var pad = (hi - lo) * 0.05;
            lo -= pad;
            hi += pad;
        }

        var gridPen = new Pen(new SolidColorBrush(Color.FromRgb(0xE6, 0xE9, 0xEE)), 1);
        var axisPen = new Pen(Brushes.Gray, 1);
        dc.DrawRectangle(null, axisPen, plot);

        // Horizontal grid + Y labels.
        var yStep = NiceStep((hi - lo) / 6);
        for (var y = Math.Ceiling(lo / yStep) * yStep; y <= hi; y += yStep)
        {
            var py = plot.Bottom - (y - lo) / (hi - lo) * plot.Height;
            dc.DrawLine(gridPen, new Point(plot.Left, py), new Point(plot.Right, py));
            var label = new FormattedText(FormatY(y, yStep), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, 11, Brushes.DimGray, dpi);
            dc.DrawText(label, new Point(plot.Left - label.Width - 4, py - label.Height / 2));
        }

        // Vertical grid + time labels.
        var tStep = NiceTimeStep(Span / 6);
        var first = new DateTime((start.Ticks / tStep.Ticks + 1) * tStep.Ticks);
        for (var t = first; t <= ViewEnd; t += tStep)
        {
            var px = plot.Left + (t - start).Ticks / (double)Span.Ticks * plot.Width;
            dc.DrawLine(gridPen, new Point(px, plot.Top), new Point(px, plot.Bottom));
            var fmt = tStep < TimeSpan.FromSeconds(1) ? "HH:mm:ss.f" : tStep < TimeSpan.FromDays(1) ? "HH:mm:ss" : "dd.MM HH:mm";
            var label = new FormattedText(t.ToString(fmt, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, 11, Brushes.DimGray, dpi);
            dc.DrawText(label, new Point(px - label.Width / 2, plot.Bottom + 4));
        }

        dc.PushClip(new RectangleGeometry(plot));
        lock (SeriesLock)
        {
            foreach (var s in Series)
            {
                var geometry = new StreamGeometry();
                using (var g = geometry.Open())
                {
                    var started = false;
                    foreach (var (t, v) in Visible(s.Points, start, ViewEnd, includeNeighbours: true))
                    {
                        if (double.IsNaN(v)) { started = false; continue; }
                        var p = new Point(plot.Left + (t - start).Ticks / (double)Span.Ticks * plot.Width,
                                          plot.Bottom - (v - lo) / (hi - lo) * plot.Height);
                        if (!started) { g.BeginFigure(p, false, false); started = true; }
                        else g.LineTo(p, true, false);
                    }
                }
                geometry.Freeze();
                var pen = new Pen(new SolidColorBrush(s.Color), 1.6);
                pen.Freeze();
                dc.DrawGeometry(null, pen, geometry);
            }
        }
        dc.Pop();

        if (!FollowLive)
        {
            var hint = new FormattedText("Paused view. Double click to follow live data.", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, 11, Brushes.SteelBlue, dpi);
            dc.DrawText(hint, new Point(plot.Right - hint.Width - 6, plot.Top + 4));
        }
    }

    private static IEnumerable<(DateTime, double)> Visible(List<(DateTime Time, double Value)> points, DateTime start, DateTime end, bool includeNeighbours = false)
    {
        if (points.Count == 0) yield break;
        var lo = LowerBound(points, start);
        if (includeNeighbours && lo > 0) lo--;
        for (var i = lo; i < points.Count; i++)
        {
            yield return points[i];
            if (points[i].Time > end) yield break;
        }
    }

    private static int LowerBound(List<(DateTime Time, double Value)> points, DateTime t)
    {
        int a = 0, b = points.Count;
        while (a < b)
        {
            var m = (a + b) / 2;
            if (points[m].Time < t) a = m + 1; else b = m;
        }
        return a;
    }

    private static double NiceStep(double raw)
    {
        if (raw <= 0 || double.IsNaN(raw)) return 1;
        var mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var n = raw / mag;
        return (n < 1.5 ? 1 : n < 3 ? 2 : n < 7 ? 5 : 10) * mag;
    }

    private static TimeSpan NiceTimeStep(TimeSpan raw)
    {
        TimeSpan[] steps =
        [
            TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(500),
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2),
            TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30), TimeSpan.FromHours(1),
            TimeSpan.FromHours(3), TimeSpan.FromHours(6), TimeSpan.FromHours(12), TimeSpan.FromDays(1),
        ];
        return steps.FirstOrDefault(s => s >= raw, TimeSpan.FromDays(1));
    }

    private static string FormatY(double v, double step) =>
        (Math.Abs(v) < step * 1e-6 ? 0.0 : v).ToString(step >= 1 ? "0" : "0." + new string('#', Math.Min(6, (int)Math.Ceiling(-Math.Log10(step)))), CultureInfo.InvariantCulture);
}

/// <summary>Real-time chart for one data window (Display > Real Time Charting).</summary>
public sealed class ChartWindow : Window
{
    private static readonly Color[] SeriesColors =
    [
        Color.FromRgb(0x1F, 0x77, 0xB4), Color.FromRgb(0xD6, 0x27, 0x28), Color.FromRgb(0x2C, 0xA0, 0x2C), Color.FromRgb(0xFF, 0x7F, 0x0E),
        Color.FromRgb(0x94, 0x67, 0xBD), Color.FromRgb(0x8C, 0x56, 0x4B), Color.FromRgb(0xE3, 0x77, 0xC2), Color.FromRgb(0x17, 0xBE, 0xCF),
    ];

    private readonly PollDocument _doc;
    private readonly ChartControl _chart = new();
    private readonly WrapPanel _legend = new() { Margin = new Thickness(6, 2, 6, 4) };
    private readonly DispatcherTimer _timer;
    private bool _paused;

    public ChartWindow(PollDocument doc)
    {
        AppIcon.Apply(this);
        _doc = doc;
        Title = $"Chart - {doc.DisplayTitle}";
        Width = 900;
        Height = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        if (Application.Current.MainWindow is { IsVisible: true } owner) Owner = owner;

        var toolbar = new ToolBar();
        var pause = new Button { Content = "Pause" };
        pause.Click += (_, _) => { _paused = !_paused; pause.Content = _paused ? "Resume" : "Pause"; };
        toolbar.Items.Add(pause);
        toolbar.Items.Add(new Separator());
        toolbar.Items.Add(new TextBlock { Text = "Time span ", VerticalAlignment = VerticalAlignment.Center });
        var span = new ComboBox { Width = 90 };
        foreach (var (text, ts) in new[] { ("10 s", 10), ("30 s", 30), ("1 min", 60), ("5 min", 300), ("10 min", 600), ("1 hour", 3600), ("8 hours", 28800) })
            span.Items.Add(new Choice<int>(ts, text));
        span.SelectedIndex = 2;
        span.SelectionChanged += (_, _) => { _chart.Span = TimeSpan.FromSeconds(FormDialog.Selected<int>(span)); _chart.FollowLive = true; _chart.InvalidateVisual(); };
        toolbar.Items.Add(span);
        toolbar.Items.Add(new Separator());
        toolbar.Items.Add(new TextBlock { Text = "Y min ", VerticalAlignment = VerticalAlignment.Center });
        var yMin = new TextBox { Width = 60, ToolTip = "Empty = automatic" };
        toolbar.Items.Add(yMin);
        toolbar.Items.Add(new TextBlock { Text = " Y max ", VerticalAlignment = VerticalAlignment.Center });
        var yMax = new TextBox { Width = 60, ToolTip = "Empty = automatic" };
        toolbar.Items.Add(yMax);
        void ApplyY()
        {
            _chart.YMin = double.TryParse(yMin.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var a) ? a : null;
            _chart.YMax = double.TryParse(yMax.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var b) ? b : null;
            _chart.InvalidateVisual();
        }
        yMin.LostFocus += (_, _) => ApplyY();
        yMax.LostFocus += (_, _) => ApplyY();
        toolbar.Items.Add(new Separator());
        AddButton(toolbar, "Clear", () => { lock (_chart.SeriesLock) foreach (var s in _chart.Series) s.Points.Clear(); _chart.InvalidateVisual(); });
        AddButton(toolbar, "Export series...", ExportCsv);
        AddButton(toolbar, "Save as PNG...", SavePng);
        AddButton(toolbar, "Help", () => Help.Open("chart"));

        var dock = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        DockPanel.SetDock(_legend, Dock.Bottom);
        dock.Children.Add(toolbar);
        dock.Children.Add(_legend);
        dock.Children.Add(_chart);
        Content = dock;

        doc.Updated += OnUpdated;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => { if (!_paused) _chart.InvalidateVisual(); }, Dispatcher);
        _timer.Start();
        Closed += (_, _) => { _timer.Stop(); doc.Updated -= OnUpdated; };
        RebuildLegend();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.F1) Help.Open("chart"); };
    }

    public IEnumerable<int> LinkedCells
    {
        get { lock (_chart.SeriesLock) return _chart.Series.Select(s => s.CellIndex).ToArray(); }
    }

    /// <summary>Links cells to chart series (or unlinks them if already linked).</summary>
    public void Link(IEnumerable<int> cells)
    {
        lock (_chart.SeriesLock)
        {
            foreach (var cell in cells)
            {
                var existing = _chart.Series.FirstOrDefault(s => s.CellIndex == cell);
                if (existing is not null)
                {
                    _chart.Series.Remove(existing);
                    continue;
                }
                var name = _doc.PeekCell(cell)?.Name;
                var address = _doc.Definition.Address + cell;
                _chart.Series.Add(new ChartSeries
                {
                    CellIndex = cell,
                    Name = string.IsNullOrEmpty(name) ? $"Address {address}" : $"{name} ({address})",
                    Color = SeriesColors[_chart.Series.Count % SeriesColors.Length],
                });
            }
        }
        RebuildLegend();
    }

    private void RebuildLegend()
    {
        _legend.Children.Clear();
        lock (_chart.SeriesLock)
        {
            foreach (var s in _chart.Series)
            {
                var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 0) };
                item.Children.Add(new Border { Width = 18, Height = 3, Background = new SolidColorBrush(s.Color), Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
                item.Children.Add(new TextBlock { Text = s.Name });
                _legend.Children.Add(item);
            }
        }
        if (_legend.Children.Count == 0)
            _legend.Children.Add(new TextBlock { Text = "No series. Select value cells in the data window and choose Display > Link to Chart.", Foreground = Brushes.DimGray });
    }

    private void OnUpdated(PollDocument doc)
    {
        if (_paused || doc.LastResult != ModbusResult.Success) return;
        var values = doc.Definition.IsWriteWindow ? doc.WriteValues : doc.Values;
        var now = DateTime.Now;
        lock (_chart.SeriesLock)
        {
            foreach (var s in _chart.Series)
            {
                s.Points.Add((now, doc.NumericValue(s.CellIndex, values)));
                if (s.Points.Count > ChartSeries.MaxPoints) s.Points.RemoveRange(0, s.Points.Count - ChartSeries.MaxPoints);
            }
        }
    }

    private void ExportCsv()
    {
        var dlg = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "modbus_chart.csv" };
        if (dlg.ShowDialog(this) != true) return;
        var sb = new StringBuilder();
        lock (_chart.SeriesLock)
        {
            sb.AppendLine("Time;" + string.Join(";", _chart.Series.Select(s => s.Name)));
            var times = _chart.Series.SelectMany(s => s.Points.Select(p => p.Time)).Distinct().Order();
            var lookups = _chart.Series.Select(s => s.Points.GroupBy(p => p.Time).ToDictionary(g => g.Key, g => g.Last().Value)).ToArray();
            foreach (var t in times)
                sb.AppendLine(t.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + ";" +
                              string.Join(";", lookups.Select(l => l.TryGetValue(t, out var v) ? v.ToString(CultureInfo.InvariantCulture) : "")));
        }
        File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(false));
    }

    private void SavePng()
    {
        var dlg = new SaveFileDialog { Filter = "Portable Network Graphics (*.png)|*.png", FileName = "modbus_chart.png" };
        if (dlg.ShowDialog(this) != true) return;
        var dpi = VisualTreeHelper.GetDpi(_chart);
        var bmp = new RenderTargetBitmap((int)(_chart.ActualWidth * dpi.DpiScaleX), (int)(_chart.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bmp.Render(_chart);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(dlg.FileName);
        encoder.Save(fs);
    }

    private static void AddButton(ToolBar bar, string text, Action action)
    {
        var b = new Button { Content = text };
        b.Click += (_, _) => action();
        bar.Items.Add(b);
    }
}
