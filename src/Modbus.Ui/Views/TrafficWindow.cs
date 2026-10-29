using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Modbus.Ui;
using Modbus.Core;

namespace Modbus.Ui.Views;

/// <summary>Display > Communication: every frame sent (Tx) and received (Rx).</summary>
public sealed class TrafficWindow : Window
{
    private const int MaxLines = 5000;
    private static TrafficWindow? _instance;
    private readonly ITrafficSource _source;

    private readonly ObservableCollection<string> _lines = [];
    private readonly ConcurrentQueue<TrafficEntry> _pending = new();
    private readonly ListBox _list;
    private readonly DispatcherTimer _timer;
    private readonly CheckBox _autoScroll = new() { Content = "Auto scroll", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) };
    private bool _stopped;

    public static void ShowSingle(ITrafficSource source)
    {
        if (_instance is null)
        {
            _instance = new TrafficWindow(source);
            _instance.Closed += (_, _) => _instance = null;
            _instance.Show();
        }
        else
        {
            _instance.Activate();
        }
    }

    public static void CloseSingle() => _instance?.Close();

    private TrafficWindow(ITrafficSource source)
    {
        AppIcon.Apply(this);
        _source = source;
        Title = "Communication Traffic";
        Width = 760;
        Height = 420;
        if (Application.Current.MainWindow is { IsVisible: true } owner) Owner = owner;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var bar = new ToolBar();
        var stop = new Button { Content = "Stop" };
        stop.Click += (_, _) => { _stopped = !_stopped; stop.Content = _stopped ? "Continue" : "Stop"; };
        bar.Items.Add(stop);
        Add(bar, "Clear", () => { _lines.Clear(); _source.ClearTraffic(); });
        Add(bar, "Copy", () => Clipboard.SetText(string.Join(Environment.NewLine, _list!.SelectedItems.Count > 0 ? _list.SelectedItems.Cast<string>() : _lines)));
        Add(bar, "Save...", Save);
        bar.Items.Add(_autoScroll);
        Add(bar, "Help", () => Help.Open("traffic"));

        _list = new ListBox
        {
            ItemsSource = _lines,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            SelectionMode = SelectionMode.Extended,
        };
        VirtualizingPanel.SetIsVirtualizing(_list, true);
        VirtualizingPanel.SetVirtualizationMode(_list, VirtualizationMode.Recycling);

        var dock = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        dock.Children.Add(bar);
        dock.Children.Add(_list);
        Content = dock;

        foreach (var e in _source.TrafficSnapshot().TakeLast(MaxLines)) _lines.Add(e.ToString());
        _source.TrafficAdded += OnTraffic;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => Flush(), Dispatcher);
        _timer.Start();
        Closed += (_, _) => { _source.TrafficAdded -= OnTraffic; _timer.Stop(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.F1) Help.Open("traffic"); };
        ScrollToEnd();
    }

    private void OnTraffic(TrafficEntry e)
    {
        if (!_stopped) _pending.Enqueue(e);
    }

    private void Flush()
    {
        var added = false;
        while (_pending.TryDequeue(out var e))
        {
            _lines.Add(e.ToString());
            added = true;
        }
        while (_lines.Count > MaxLines) _lines.RemoveAt(0);
        if (added) ScrollToEnd();
    }

    private void ScrollToEnd()
    {
        if (_autoScroll.IsChecked == true && _lines.Count > 0) _list.ScrollIntoView(_lines[^1]);
    }

    private void Save()
    {
        var dlg = new SaveFileDialog { Filter = "Communication log (*.txt)|*.txt|All Files (*.*)|*.*", FileName = "modbus_traffic.txt" };
        if (dlg.ShowDialog(this) == true)
            File.WriteAllLines(dlg.FileName, _source.TrafficSnapshot().Select(e => e.ToString()));
    }

    private static void Add(ToolBar bar, string text, Action action)
    {
        var b = new Button { Content = text };
        b.Click += (_, _) => action();
        bar.Items.Add(b);
    }
}
