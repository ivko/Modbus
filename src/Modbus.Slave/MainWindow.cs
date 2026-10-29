using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Modbus.Core;
using Modbus.Core.Data;
using Modbus.Core.Polling;
using Modbus.Core.Protocol;
using Modbus.Core.Slave;
using Modbus.Slave.Dialogs;
using Modbus.Ui;
using Modbus.Ui.Dialogs;
using Modbus.Ui.Views;

namespace Modbus.Slave;

public sealed class MainWindow : Window
{
    private readonly TabControl _tabs = new();
    private readonly TextBlock _statusConnection = new();
    private readonly TextBlock _statusHint = new() { Foreground = Brushes.DimGray };
    private readonly MenuItem _disabledItem = new() { Header = "_Disabled (no answers for this window)", IsCheckable = true };
    private readonly MenuItem _strictItem = new() { Header = "_Strict Addressing", IsCheckable = true };
    private readonly MenuItem _plcItem = new() { Header = "_PLC Addresses (Base 1)", IsCheckable = true };
    private readonly Dictionary<PollDocument, ChartWindow> _charts = [];
    private string? _workspacePath;
    private int _counter;

    private static SlaveState State => SlaveState.Current;

    public MainWindow()
    {
        Title = "Modbus Slave";
        Width = 1000;
        Height = 680;
        UseLayoutRounding = true;
        AppIcon.Apply(this);

        var menu = new Menu { Padding = new Thickness(2) };

        var file = TopMenu(menu, "_File");
        Item(file, "_New Window", () => NewWindow(), Key.N, ModifierKeys.Control);
        Item(file, "_Open Workspace...", OpenWorkspace, Key.O, ModifierKeys.Control);
        Item(file, "_Save Workspace", () => SaveWorkspace(false), Key.S, ModifierKeys.Control);
        Item(file, "Save Workspace _As...", () => SaveWorkspace(true));
        file.Items.Add(new Separator());
        Item(file, "_Export to CSV...", ExportCsv);
        Item(file, "_Import Values from CSV...", ImportCsv);
        file.Items.Add(new Separator());
        Item(file, "_Close Window", () => WithCurrent(CloseView), Key.F4, ModifierKeys.Control);
        Item(file, "E_xit", Close);

        var conn = TopMenu(menu, "_Connection");
        Item(conn, "_Connect...", ConnectWithDialog, Key.F3);
        Item(conn, "_Quick Connect", QuickConnect, Key.F5);
        Item(conn, "_Disconnect", Disconnect, Key.F4);

        var setup = TopMenu(menu, "_Setup");
        Item(setup, "_Slave Definition...", EditDefinition, Key.F8);
        setup.Items.Add(_disabledItem);
        _disabledItem.Click += (_, _) => WithCurrent(v => { v.Document.Definition.Disabled = _disabledItem.IsChecked; v.Invalidate(); });
        setup.Items.Add(new Separator());
        Item(setup, "_Error Simulation...", () => WithCurrent(v => { if (SlaveDialogs.ErrorSimulation(State.Engine.Device(v.Document.Definition.SlaveId))) InvalidateAll(); }), Key.E, ModifierKeys.Control);
        Item(setup, "Device _Identification...", () => WithCurrent(v => SlaveDialogs.Identification(State.Engine.Device(v.Document.Definition.SlaveId))));
        setup.Items.Add(_strictItem);
        _strictItem.IsChecked = State.Settings.StrictAddressing;
        _strictItem.Click += (_, _) => { State.Settings.StrictAddressing = _strictItem.IsChecked; State.ApplyEngineOptions(); };
        setup.Items.Add(new Separator());
        Item(setup, "_Log...", StartLog);
        Item(setup, "Logging _Off", StopLog);
        Item(setup, "Reset _Counters", () => { foreach (var v in Views) v.Document.ResetCounters(); foreach (var d in State.Engine.Devices) d.ResetCounters(); InvalidateAll(); });

        var display = TopMenu(menu, "_Display");
        BuildFormatMenu(display);
        display.Items.Add(new Separator());
        Item(display, "_Scaling...", () => EditCells(CellDialogs.Scaling));
        Item(display, "Conditional _Colors...", () => EditCells(CellDialogs.Colors));
        Item(display, "_Value Names...", () => EditCells(CellDialogs.ValueNames));
        display.Items.Add(_plcItem);
        _plcItem.Click += (_, _) => WithCurrent(v => { v.Document.Definition.PlcAddresses = _plcItem.IsChecked; v.Rebuild(); });
        display.Items.Add(new Separator());
        Item(display, "C_ommunication...", () => TrafficWindow.ShowSingle(State.Traffic), Key.F7);
        Item(display, "_Real Time Charting...", () => WithCurrent(v => OpenChart(v)));
        Item(display, "_Link to Chart", () => WithCurrent(v => OpenChart(v).Link(v.SelectedIndices)), Key.L, ModifierKeys.Control);

        var sim = TopMenu(menu, "S_imulation");
        Item(sim, "_Value Simulation...", () => EditCells(SlaveDialogs.ValueSimulation), Key.F9);
        Item(sim, "_Auto Increment", () => QuickSimulation(SimulationMode.Increment));
        Item(sim, "Auto _Decrement", () => QuickSimulation(SimulationMode.Decrement));
        Item(sim, "_Stop Simulation for Selection", () => QuickSimulation(SimulationMode.None));
        Item(sim, "Stop _All Simulations", StopAllSimulation);

        var tools = TopMenu(menu, "_Tools");
        Item(tools, "_Register Automation (COM)...", () => App.Automation.ShowRegistrationDialog("Modbus Slave (Witte Software)"));

        var help = TopMenu(menu, "_Help");
        Item(help, "_Help Topics", () => Help.Open(""), Key.F1);
        Item(help, "_Error Simulation", () => Help.Open("error-simulation"));
        Item(help, "_Automation", () => Help.Open("automation"));
        help.Items.Add(new Separator());
        Item(help, "_About Modbus Slave", About);

        var status = new StatusBar();
        status.Items.Add(new StatusBarItem { Content = _statusConnection });
        status.Items.Add(new Separator());
        status.Items.Add(new StatusBarItem { Content = _statusHint });

        var dock = new DockPanel();
        DockPanel.SetDock(menu, Dock.Top);
        DockPanel.SetDock(status, Dock.Bottom);
        dock.Children.Add(menu);
        dock.Children.Add(status);
        dock.Children.Add(_tabs);
        Content = dock;

        _tabs.SelectionChanged += (_, e) => { if (e.Source == _tabs) SyncMenu(); };
        State.StateChanged += () => Dispatcher.BeginInvoke(UpdateStatus);
        UpdateStatus();
        AllowDrop = true;
        Drop += (_, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } f) OpenPath(f[0]); };
    }

    // ---- windows ----

    public IEnumerable<PollView> Views => _tabs.Items.Cast<TabItem>().Select(t => (PollView)t.Content);
    public PollView? CurrentView => (_tabs.SelectedItem as TabItem)?.Content as PollView;

    public PollView NewWindow(byte slaveId = 1, FunctionCode table = FunctionCode.ReadHoldingRegisters, ushort address = 0, int quantity = 10, bool select = true)
    {
        var doc = new PollDocument
        {
            Title = $"Mbslave{++_counter}",
            Definition = new PollDefinition { SlaveId = slaveId, Function = table, Address = address, Quantity = quantity, ScanRateMs = 0 },
        };
        return AddDocument(doc, select);
    }

    public PollView AddDocument(PollDocument doc, bool select = true)
    {
        var view = new PollView(doc)
        {
            StatusText = Status,
            ErrorText = d => !State.IsRunning ? "Not connected. Press F3 to start the slave." : ErrorLine(d),
        };
        view.EditRequested += EditValue;
        var title = new TextBlock { Text = doc.Title, VerticalAlignment = VerticalAlignment.Center };
        var close = new Button
        {
            Content = "x", Padding = new Thickness(4, 0, 4, 1), Margin = new Thickness(8, 0, 0, 0), Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Close window (Ctrl+F4)",
        };
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(title);
        header.Children.Add(close);
        var tab = new TabItem { Header = header, Content = view, Tag = title };
        close.Click += (_, _) => CloseView(view);
        _tabs.Items.Add(tab);
        if (select) _tabs.SelectedItem = tab;
        State.Engine.AddWindow(doc);
        return view;
    }

    private static string Status(PollDocument d)
    {
        var def = d.Definition;
        var device = State.Engine.Device(def.SlaveId);
        return $"ID = {def.SlaveId}: F = {(byte)def.Function:D2}: Requests = {d.TxCount}: Errors = {d.ErrorCount}" +
               (device.SimulatedErrors > 0 ? $": Simulated faults = {device.SimulatedErrors}" : "") +
               (def.Disabled ? "  [disabled]" : "");
    }

    private static string ErrorLine(PollDocument d)
    {
        var device = State.Engine.Device(d.Definition.SlaveId);
        if (device.Handler.ListenOnly) return "Listen only mode (diagnostics 04). Send diagnostics 01 to restart.";
        if (device.Errors.IsActive) return "Error simulation is on for this slave (Setup > Error Simulation).";
        return "";
    }

    public void SelectView(PollView view)
    {
        foreach (TabItem t in _tabs.Items)
            if (t.Content == view) _tabs.SelectedItem = t;
    }

    public void CloseView(PollView view)
    {
        var tab = _tabs.Items.Cast<TabItem>().FirstOrDefault(t => t.Content == view);
        if (tab is null) return;
        State.Engine.RemoveWindow(view.Document);
        view.Stop();
        if (_charts.Remove(view.Document, out var chart)) chart.Close();
        _tabs.Items.Remove(tab);
    }

    private void WithCurrent(Action<PollView> action)
    {
        if (CurrentView is { } v) action(v);
        else _statusHint.Text = "No slave window. Create one with File > New Window (Ctrl+N).";
    }

    private void InvalidateAll()
    {
        foreach (var v in Views) v.Invalidate();
    }

    private void SyncMenu()
    {
        if (CurrentView is not { } v) return;
        _disabledItem.IsChecked = v.Document.Definition.Disabled;
        _plcItem.IsChecked = v.Document.Definition.PlcAddresses;
    }

    // ---- connection ----

    private void ConnectWithDialog()
    {
        var s = SlaveDialogs.Connection(State.Settings);
        if (s is null) return;
        s.StrictAddressing = State.Settings.StrictAddressing;
        State.Settings = s;
        QuickConnect();
    }

    private void QuickConnect()
    {
        try
        {
            State.Start();
            _statusHint.Text = "";
        }
        catch (ModbusConnectionException ex)
        {
            MessageBox.Show(this, ex.Message, "Connection failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        InvalidateAll();
    }

    private void Disconnect()
    {
        State.Stop();
        InvalidateAll();
    }

    public void UpdateStatus()
    {
        _statusConnection.Text = State.IsRunning ? $"Listening: {State.Settings.Describe()}" : $"Not connected ({State.Settings.Describe()}). Press F3 to start.";
        _statusConnection.Foreground = State.IsRunning ? Brushes.DarkGreen : Brushes.Firebrick;
        _strictItem.IsChecked = State.Settings.StrictAddressing;
        InvalidateAll();
    }

    // ---- setup ----

    private void EditDefinition() => WithCurrent(v =>
    {
        var r = SlaveDialogs.Definition(v.Document.Definition, v.Document.Title);
        if (r is not { } result) return;
        v.Document.Title = result.Title;
        v.Document.Definition = result.Definition;
        State.Engine.WindowChanged(v.Document);
        v.Rebuild();
        if (_tabs.SelectedItem is TabItem { Tag: TextBlock t }) t.Text = result.Title;
        SyncMenu();
    });

    private void StartLog() => WithCurrent(v =>
    {
        var f = new FormDialog("Log", "logging");
        var path = f.AddText("Log file", v.Logger?.Options.Path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), $"{v.Document.Title}.csv"));
        f.AddButton("Browse...", () =>
        {
            var dlg = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = Path.GetFileName(path.Text), OverwritePrompt = false };
            if (dlg.ShowDialog() == true) path.Text = dlg.FileName;
        });
        var interval = f.AddText("Log every [s] (0 = every change)", "1", 90);
        var changed = f.AddCheck("Only log when a value changed", true);
        var midnight = f.AddCheck("New log file at midnight", false);
        LogOptions? options = null;
        f.Validate = () =>
        {
            options = new LogOptions
            {
                Path = path.Text.Trim(), IntervalSeconds = int.Parse(interval.Text, CultureInfo.InvariantCulture),
                OnlyWhenChanged = changed.IsChecked == true, NewFileAtMidnight = midnight.IsChecked == true, LogErrors = false,
            };
            return options.Path.Length == 0 ? "Choose a log file." : null;
        };
        if (!f.ShowModal() || options is null) return;
        v.Logger?.Dispose();
        try
        {
            v.Logger = new DataLogger(v.Document, options);
            _statusHint.Text = $"Logging {v.Document.Title} to {options.Path}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"Log file open error: {ex.Message}", "Log", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    });

    private void StopLog() => WithCurrent(v =>
    {
        if (v.Logger is null) return;
        _statusHint.Text = $"Logging off. {v.Logger.Rows} rows written.";
        v.Logger.Dispose();
        v.Logger = null;
    });

    // ---- display and simulation ----

    private void BuildFormatMenu(MenuItem display)
    {
        void FormatItem(ItemsControl parent, string text, DisplayFormat format, Key key = Key.None)
        {
            var item = new MenuItem { Header = text };
            item.Click += (_, _) => SetFormat(format);
            if (key != Key.None)
            {
                item.InputGestureText = $"Alt+{key}";
                InputBindings.Add(new KeyBinding(new RelayCommand(() => SetFormat(format)), key, ModifierKeys.Alt));
            }
            parent.Items.Add(item);
        }
        FormatItem(display, "_Signed", DisplayFormat.Signed, Key.S);
        FormatItem(display, "_Unsigned", DisplayFormat.Unsigned, Key.U);
        FormatItem(display, "_Hex", DisplayFormat.Hex, Key.H);
        FormatItem(display, "_Binary", DisplayFormat.Binary, Key.B);
        foreach (var kind in new[] { ValueKind.Int32, ValueKind.UInt32, ValueKind.Int64, ValueKind.UInt64, ValueKind.Float, ValueKind.Double })
        {
            var sub = new MenuItem { Header = DisplayFormats.Of(kind, ByteOrder.BigEndian).Label().Replace(" Big-endian (ABCD)", "") };
            foreach (var order in Enum.GetValues<ByteOrder>()) FormatItem(sub, DisplayFormats.OrderLabel(order), DisplayFormats.Of(kind, order));
            display.Items.Add(sub);
        }
    }

    private void SetFormat(DisplayFormat format) => WithCurrent(v =>
    {
        if (v.Document.Definition.IsBits) { _statusHint.Text = "Coils and inputs are shown as 0/1."; return; }
        foreach (var i in v.SelectedIndices) v.Document.Cell(i).Format = format;
        v.Invalidate();
    });

    private void EditCells(Func<PollDocument, IReadOnlyList<int>, bool> dialog) => WithCurrent(v =>
    {
        if (v.SelectedIndices.Count == 0) { _statusHint.Text = "Select one or more value cells first."; return; }
        if (dialog(v.Document, v.SelectedIndices)) { State.Engine.WindowChanged(v.Document); v.Invalidate(); }
    });

    /// <summary>Auto increment / decrement by 1 every second over the full range of the cell format.</summary>
    private void QuickSimulation(SimulationMode mode) => WithCurrent(v =>
    {
        if (v.SelectedIndices.Count == 0) { _statusHint.Text = "Select one or more value cells first."; return; }
        foreach (var i in v.SelectedIndices)
            v.Document.Cell(i).Simulation = mode == SimulationMode.None ? null : AutoSimulation(v.Document, i, mode);
        State.Engine.WindowChanged(v.Document);
        v.Invalidate();
    });

    public static CellSimulation AutoSimulation(PollDocument doc, int index, SimulationMode mode)
    {
        var (min, max) = doc.Definition.IsBits ? (0d, 1d) : (doc.PeekCell(index)?.Format ?? DisplayFormat.Signed).Kind() switch
        {
            ValueKind.Int16 => (short.MinValue, (double)short.MaxValue),
            ValueKind.Int32 => (int.MinValue, int.MaxValue),
            ValueKind.UInt32 => (0, uint.MaxValue),
            ValueKind.Int64 or ValueKind.Float or ValueKind.Double => (-1e9, 1e9),
            ValueKind.UInt64 => (0, 1e9),
            _ => (0, ushort.MaxValue),
        };
        return new CellSimulation { Mode = mode, Min = min, Max = max, Step = 1, IntervalMs = 1000 };
    }

    private void StopAllSimulation()
    {
        foreach (var v in Views)
        {
            foreach (var c in v.Document.Cells.Values) c.Simulation = null;
            State.Engine.WindowChanged(v.Document);
            v.Invalidate();
        }
    }

    private ChartWindow OpenChart(PollView view)
    {
        if (!_charts.TryGetValue(view.Document, out var chart))
        {
            chart = new ChartWindow(view.Document);
            chart.Closed += (_, _) => _charts.Remove(view.Document);
            _charts[view.Document] = chart;
            chart.Show();
            if (view.SelectedIndices.Count > 0) chart.Link(view.SelectedIndices);
        }
        else
        {
            chart.Activate();
        }
        return chart;
    }

    // ---- editing ----

    private void EditValue(PollView view)
    {
        var sel = view.SelectedIndices;
        if (sel.Count == 0) return;
        var doc = view.Document;
        if (doc.Definition.IsBits && sel.Count >= 1)
        {
            // Double click toggles coils and inputs; for several selected cells, all get the opposite of the first.
            var values = doc.Values;
            var on = values.ElementAtOrDefault(sel[0]) == 0;
            foreach (var i in sel) State.Engine.Write(doc, i, [(ushort)(on ? 1 : 0)]);
            view.Invalidate();
            return;
        }
        var parsed = SlaveDialogs.EditValue(doc, sel[0]);
        if (parsed is null) return;
        State.Engine.Write(doc, sel[0], parsed);
        view.Invalidate();
    }

    // ---- files ----

    private void SaveWorkspace(bool saveAs)
    {
        if (saveAs || _workspacePath is null)
        {
            var dlg = new SaveFileDialog { Filter = SlaveWorkspace.FileFilter, FileName = "slave.mms" };
            if (dlg.ShowDialog(this) != true) return;
            _workspacePath = dlg.FileName;
        }
        SlaveWorkspace.From(State.Engine, State.Settings, Views.Select(v => v.Document)).Save(_workspacePath);
        Title = $"Modbus Slave - {Path.GetFileName(_workspacePath)}";
        _statusHint.Text = $"Saved {_workspacePath} (windows, settings and values)";
    }

    private void OpenWorkspace()
    {
        var dlg = new OpenFileDialog { Filter = SlaveWorkspace.FileFilter };
        if (dlg.ShowDialog(this) == true) OpenPath(dlg.FileName);
    }

    public void OpenPath(string path)
    {
        try
        {
            var ws = SlaveWorkspace.Load(path);
            foreach (var v in Views.ToList()) CloseView(v);
            State.Settings = ws.Server;
            var added = ws.Windows.Select(w => AddDocument(w).Document).ToList();
            ws.ApplyTo(State.Engine, added);
            _workspacePath = path;
            Title = $"Modbus Slave - {Path.GetFileName(path)}";
            UpdateStatus();
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show(this, $"Could not open {path}:\n{ex.Message}", "Open", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ExportCsv() => WithCurrent(v =>
    {
        var doc = v.Document;
        var dlg = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = doc.Title + ".csv" };
        if (dlg.ShowDialog(this) != true) return;
        var values = doc.Values;
        var sb = new StringBuilder("Address;Name;Value;Raw\r\n");
        for (var i = 0; i < doc.Definition.CellCount; i++)
        {
            if (doc.IsContinuation(i)) continue;
            sb.Append(doc.Definition.Address + i).Append(';').Append(doc.PeekCell(i)?.Name ?? "").Append(';')
              .Append(i < values.Length ? doc.FormatCell(i, values) : "").Append(';')
              .Append(i < values.Length ? values[i].ToString(CultureInfo.InvariantCulture) : "").Append("\r\n");
        }
        File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(false));
    });

    /// <summary>Loads "address;value" rows (raw register values; extra columns ignored) into the current window.</summary>
    private void ImportCsv() => WithCurrent(v =>
    {
        var dlg = new OpenFileDialog { Filter = "CSV (*.csv)|*.csv|All files|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        var doc = v.Document;
        var count = 0;
        foreach (var line in File.ReadLines(dlg.FileName))
        {
            var parts = line.Split([';', ',', '\t']);
            if (parts.Length < 2 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var address)) continue;
            var raw = parts.Length >= 4 ? parts[3] : parts[1];
            if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) continue;
            var index = address - doc.Definition.Address;
            if (index < 0 || index >= doc.Definition.Quantity) continue;
            State.Engine.Write(doc, index, [unchecked((ushort)value)]);
            count++;
        }
        _statusHint.Text = $"Imported {count} values.";
        v.Invalidate();
    });

    private void About() => MessageBox.Show(this,
        $"Modbus Slave {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}\n\nModbus slave simulator for Modbus TCP, UDP, RTU, ASCII, RTU/ASCII over TCP/UDP and Modbus/TCP Security, with value and error simulation.\n\nProgram folder: {AppContext.BaseDirectory}",
        "About Modbus Slave", MessageBoxButton.OK, MessageBoxImage.Information);

    protected override void OnClosing(CancelEventArgs e)
    {
        foreach (var v in Views) v.Stop();
        foreach (var c in _charts.Values.ToList()) c.Close();
        TrafficWindow.CloseSingle();
        base.OnClosing(e);
    }

    private static MenuItem TopMenu(Menu menu, string header)
    {
        var m = new MenuItem { Header = header };
        menu.Items.Add(m);
        return m;
    }

    private MenuItem Item(MenuItem parent, string header, Action action, Key key = Key.None, ModifierKeys modifiers = ModifierKeys.None)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        if (key != Key.None)
        {
            var gesture = new KeyGesture(key, modifiers);
            item.InputGestureText = gesture.GetDisplayStringForCulture(CultureInfo.CurrentUICulture);
            InputBindings.Add(new KeyBinding(new RelayCommand(action), gesture));
        }
        parent.Items.Add(item);
        return item;
    }
}
