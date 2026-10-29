using ModbusMaster.Ui.Automation;
using ModbusMaster.Ui.Views;
using ModbusMaster.Ui.Dialogs;
using ModbusMaster.Ui;
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
using ModbusMaster.App.Dialogs;
using ModbusMaster.App.Views;
using ModbusMaster.Core;
using ModbusMaster.Core.Data;
using ModbusMaster.Core.Polling;
using ModbusMaster.Core.Protocol;

namespace ModbusMaster.App;

public sealed class MainWindow : Window
{
    private readonly TabControl _tabs = new() { Margin = new Thickness(0) };
    private readonly TextBlock _statusConnection = new();
    private readonly TextBlock _statusHint = new() { Foreground = Brushes.DimGray };
    private readonly MenuItem _disabledItem = new() { Header = "Read/Write _Disabled", IsCheckable = true };
    private readonly MenuItem _plcItem = new() { Header = "_PLC Addresses (Base 1)", IsCheckable = true };
    private readonly MenuItem _connectItem;
    private readonly MenuItem _disconnectItem;
    private readonly Dictionary<PollDocument, ChartWindow> _charts = [];
    private string? _workspacePath;
    private int _windowCounter;

    public MainWindow()
    {
        Title = "Modbus Master";
        Width = 1000;
        Height = 680;
        UseLayoutRounding = true;
        Icon = AppIcon.Create();

        var menu = new Menu { Padding = new Thickness(2) };

        // File
        var file = TopMenu(menu, "_File");
        Item(file, "_New Window", NewWindow, Key.N, ModifierKeys.Control);
        Item(file, "_Open Workspace...", OpenWorkspace, Key.O, ModifierKeys.Control);
        Item(file, "_Save Workspace", () => SaveWorkspace(false), Key.S, ModifierKeys.Control);
        Item(file, "Save Workspace _As...", () => SaveWorkspace(true));
        file.Items.Add(new Separator());
        Item(file, "Open Window...", OpenWindowFile);
        Item(file, "Save Window As...", SaveWindowFile);
        Item(file, "_Export to CSV...", ExportCsv);
        file.Items.Add(new Separator());
        Item(file, "_Close Window", CloseCurrent, Key.F4, ModifierKeys.Control);
        Item(file, "E_xit", Close);

        // Connection
        var conn = TopMenu(menu, "_Connection");
        _connectItem = Item(conn, "_Connect...", ConnectWithDialog, Key.F3);
        Item(conn, "_Quick Connect", QuickConnect, Key.F5);
        _disconnectItem = Item(conn, "_Disconnect", Disconnect, Key.F4);

        // Setup
        var setup = TopMenu(menu, "_Setup");
        Item(setup, "_Read/Write Definition...", EditDefinition, Key.F8);
        setup.Items.Add(_disabledItem);
        _disabledItem.Click += (_, _) => WithCurrent(v => { v.Document.Definition.Disabled = _disabledItem.IsChecked; AppState.Current.Scheduler.Kick(v.Document); v.Invalidate(); });
        Item(setup, "Read/Write _Once", () => WithCurrent(v => { v.Document.RequestOnce(); AppState.Current.Scheduler.Kick(v.Document); }), Key.F6);
        setup.Items.Add(new Separator());
        Item(setup, "_Log...", StartLog);
        Item(setup, "Logging _Off", StopLog);
        setup.Items.Add(new Separator());
        Item(setup, "Reset _Counters", () => WithCurrent(v => { v.Document.ResetCounters(); v.Invalidate(); }));

        // Functions
        var fn = TopMenu(menu, "F_unctions");
        Item(fn, "0_5: Write Single Coil...", () => FunctionDialogs.WriteCoils(CurrentSlave, CurrentAddress, false));
        Item(fn, "0_6: Write Single Register...", () => FunctionDialogs.WriteRegisters(CurrentSlave, CurrentAddress, false, CurrentFormat));
        Item(fn, "15: Write Multiple _Coils...", () => FunctionDialogs.WriteCoils(CurrentSlave, CurrentAddress, true));
        Item(fn, "16: Write Multiple _Registers...", () => FunctionDialogs.WriteRegisters(CurrentSlave, CurrentAddress, true, CurrentFormat));
        Item(fn, "17: Report _Server ID...", () => FunctionDialogs.ReportServerId(CurrentSlave));
        Item(fn, "22: _Mask Write Register...", () => FunctionDialogs.MaskWrite(CurrentSlave, CurrentAddress));
        Item(fn, "2_3: Read/Write Multiple Registers...", () => FunctionDialogs.ReadWrite(CurrentSlave, CurrentAddress));
        Item(fn, "08: _Diagnostics...", () => FunctionDialogs.Diagnostics(CurrentSlave));
        fn.Items.Add(new Separator());
        Item(fn, "_Test Center...", () => new TestCenterWindow().Show(), Key.T, ModifierKeys.Control);

        // Display
        var display = TopMenu(menu, "_Display");
        BuildFormatMenu(display);
        display.Items.Add(new Separator());
        Item(display, "_Scaling...", () => EditCells(CellDialogs.Scaling));
        Item(display, "Conditional _Colors...", () => EditCells(CellDialogs.Colors));
        Item(display, "_Value Names...", () => EditCells(CellDialogs.ValueNames));
        display.Items.Add(_plcItem);
        _plcItem.Click += (_, _) => WithCurrent(v => { v.Document.Definition.PlcAddresses = _plcItem.IsChecked; v.Rebuild(); });
        display.Items.Add(new Separator());
        Item(display, "C_ommunication...", () => TrafficWindow.ShowSingle(AppState.Current), Key.F7);
        Item(display, "_Real Time Charting...", () => WithCurrent(v => OpenChart(v)));
        Item(display, "_Link to Chart", () => WithCurrent(v => OpenChart(v).Link(v.SelectedIndices)), Key.L, ModifierKeys.Control);

        // Tools
        var tools = TopMenu(menu, "_Tools");
        Item(tools, "_Address Scan...", () => new AddressScanWindow(CurrentSlave).Show());
        Item(tools, "_Slave Scan...", () => new SlaveScanWindow().Show());
        tools.Items.Add(new Separator());
        Item(tools, "_Register Automation (COM)...", Automation.AutomationRegistration.ShowDialog);

        // Help
        var help = TopMenu(menu, "_Help");
        Item(help, "_Help Topics", () => Help.Open(CurrentHelpTopic()), Key.F1);
        Item(help, "_Quick Start", () => Help.Open("quick-start"));
        Item(help, "_Command Line (mbm)", () => Help.Open("cli"));
        Item(help, "_Automation", () => Help.Open("automation"));
        help.Items.Add(new Separator());
        Item(help, "_About Modbus Master", About);

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
        AppState.Current.ConnectionChanged += () => Dispatcher.BeginInvoke(UpdateStatus);
        AppState.Current.ConnectionLost += ex => Dispatcher.BeginInvoke(() => _statusHint.Text = ex.Message);
        UpdateStatus();
        AllowDrop = true;
        Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) OpenPath(files[0]);
        };
    }

    // ---- windows ----------------------------------------------------------------------

    public IEnumerable<PollView> Views => _tabs.Items.Cast<TabItem>().Select(t => (PollView)t.Content);

    public PollView? CurrentView => (_tabs.SelectedItem as TabItem)?.Content as PollView;

    private byte CurrentSlave => CurrentView?.Document.Definition.SlaveId ?? 1;

    private ushort CurrentAddress
    {
        get
        {
            if (CurrentView is not { } v) return 0;
            var sel = v.SelectedIndices;
            return (ushort)(v.Document.Definition.Address + (sel.Count > 0 ? sel[0] : 0));
        }
    }

    private DisplayFormat CurrentFormat
    {
        get
        {
            if (CurrentView is not { } v) return DisplayFormat.Signed;
            var sel = v.SelectedIndices;
            return sel.Count > 0 ? v.Document.PeekCell(sel[0])?.Format ?? DisplayFormat.Signed : DisplayFormat.Signed;
        }
    }

    public void NewWindow()
    {
        var doc = new PollDocument { Title = $"Mbpoll{++_windowCounter}" };
        AddDocument(doc);
    }

    public PollView AddDocument(PollDocument doc, bool select = true)
    {
        var view = new PollView(doc) { IsOnline = () => AppState.Current.IsConnected };
        view.EditRequested += EditValue;
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        var title = new TextBlock { Text = doc.Title, VerticalAlignment = VerticalAlignment.Center };
        var close = new Button
        {
            Content = "x", Padding = new Thickness(4, 0, 4, 1), Margin = new Thickness(8, 0, 0, 0), Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), ToolTip = "Close window (Ctrl+F4)", VerticalAlignment = VerticalAlignment.Center,
        };
        header.Children.Add(title);
        header.Children.Add(close);
        var tab = new TabItem { Header = header, Content = view, Tag = title };
        close.Click += (_, _) => CloseView(view);
        _tabs.Items.Add(tab);
        if (select) _tabs.SelectedItem = tab;
        AppState.Current.Scheduler.Add(doc);
        return view;
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
        AppState.Current.Scheduler.Remove(view.Document);
        view.Stop();
        if (_charts.Remove(view.Document, out var chart)) chart.Close();
        _tabs.Items.Remove(tab);
    }

    private void CloseCurrent() => WithCurrent(CloseView);

    private void WithCurrent(Action<PollView> action)
    {
        if (CurrentView is { } v) action(v);
        else _statusHint.Text = "No data window. Create one with File > New Window (Ctrl+N).";
    }

    private void RefreshTitle(PollView view)
    {
        var tab = _tabs.Items.Cast<TabItem>().FirstOrDefault(t => t.Content == view);
        if (tab?.Tag is TextBlock t) t.Text = view.Document.Title;
    }

    private void SyncMenu()
    {
        if (CurrentView is not { } v) return;
        _disabledItem.IsChecked = v.Document.Definition.Disabled;
        _plcItem.IsChecked = v.Document.Definition.PlcAddresses;
    }

    private string CurrentHelpTopic() => CurrentView is null ? "" : "overview";

    // ---- connection -------------------------------------------------------------------

    private void ConnectWithDialog()
    {
        var settings = ConnectionDialog.Show(AppState.Current.Settings);
        if (settings is null) return;
        AppState.Current.Settings = settings;
        QuickConnect();
    }

    private void QuickConnect()
    {
        Cursor = Cursors.Wait;
        try
        {
            AppState.Current.Connect();
            _statusHint.Text = "";
        }
        catch (ModbusConnectionException ex)
        {
            MessageBox.Show(this, ex.Message, "Connection failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Cursor = null;
            foreach (var v in Views) v.Invalidate();
        }
    }

    private void Disconnect()
    {
        AppState.Current.Disconnect();
        foreach (var v in Views) v.Invalidate();
    }

    public void UpdateStatus()
    {
        var s = AppState.Current;
        _statusConnection.Text = s.IsConnected ? $"Connected: {s.Settings.Describe()}" : $"Disconnected ({s.Settings.Describe()}). Press F3 to connect.";
        _statusConnection.Foreground = s.IsConnected ? Brushes.DarkGreen : Brushes.Firebrick;
        _connectItem.IsEnabled = true;
        _disconnectItem.IsEnabled = s.IsConnected;
        foreach (var v in Views) v.Invalidate();
    }

    // ---- setup ------------------------------------------------------------------------

    private void EditDefinition() => WithCurrent(v =>
    {
        var result = DefinitionDialog.Show(v.Document.Definition, v.Document.Title);
        if (result is not { } r) return;
        v.Document.Title = r.Title;
        v.Document.ApplyDefinition(r.Definition);
        v.Rebuild();
        RefreshTitle(v);
        SyncMenu();
        AppState.Current.Scheduler.Kick(v.Document);
    });

    private void StartLog() => WithCurrent(v =>
    {
        var f = new FormDialog("Log", "logging");
        var path = f.AddText("Log file", v.Logger?.Options.Path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), $"{v.Document.Title}.csv"));
        var browse = f.AddButton("Browse...", () =>
        {
            var dlg = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv|Text (*.txt)|*.txt", FileName = Path.GetFileName(path.Text), OverwritePrompt = false };
            if (dlg.ShowDialog() == true) path.Text = dlg.FileName;
        });
        var interval = f.AddText("Log every [s] (0 = each poll)", "0", 90);
        var delimiter = f.AddCombo("Delimiter", new[] { new Choice<string>(";", "; (semicolon)"), new Choice<string>(",", ", (comma)"), new Choice<string>("\t", "Tab") }, ";");
        var changed = f.AddCheck("Only log when a value changed", false);
        var errors = f.AddCheck("Log errors", true);
        var errorsOnly = f.AddCheck("Log errors only", false);
        var midnight = f.AddCheck("New log file at midnight", false);
        f.AddInfo("Each row: time, the formatted value of each cell, and the poll status. The file can be opened in Excel.");
        LogOptions? options = null;
        f.Validate = () =>
        {
            options = new LogOptions
            {
                Path = path.Text.Trim(), IntervalSeconds = int.Parse(interval.Text, CultureInfo.InvariantCulture),
                Delimiter = FormDialog.Selected<string>(delimiter), OnlyWhenChanged = changed.IsChecked == true,
                LogErrors = errors.IsChecked == true, ErrorsOnly = errorsOnly.IsChecked == true, NewFileAtMidnight = midnight.IsChecked == true,
            };
            return options.Path.Length == 0 ? "Choose a log file." : null;
        };
        _ = browse;
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
        _statusHint.Text = $"Logging off. {v.Logger.Rows} rows written to {v.Logger.Options.Path}";
        v.Logger.Dispose();
        v.Logger = null;
    });

    // ---- display ----------------------------------------------------------------------

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
            foreach (var order in Enum.GetValues<ByteOrder>())
                FormatItem(sub, DisplayFormats.OrderLabel(order), DisplayFormats.Of(kind, order));
            display.Items.Add(sub);
        }
    }

    private void SetFormat(DisplayFormat format) => WithCurrent(v =>
    {
        if (v.Document.Definition.IsBits)
        {
            _statusHint.Text = "Coils and discrete inputs are shown as 0/1.";
            return;
        }
        var step = format.Registers();
        foreach (var i in v.SelectedIndices.Where(i => !v.Document.IsContinuation(i) || step == 1))
            v.Document.Cell(i).Format = format;
        v.Invalidate();
    });

    private void EditCells(Func<PollDocument, IReadOnlyList<int>, bool> dialog) => WithCurrent(v =>
    {
        if (v.SelectedIndices.Count == 0)
        {
            _statusHint.Text = "Select one or more value cells first.";
            return;
        }
        if (dialog(v.Document, v.SelectedIndices)) v.Invalidate();
    });

    private ChartWindow OpenChart(PollView view)
    {
        if (!_charts.TryGetValue(view.Document, out var chart))
        {
            chart = new ChartWindow(view.Document);
            chart.Closed += (_, _) => _charts.Remove(view.Document);
            _charts[view.Document] = chart;
            chart.Show();
            if (!chart.LinkedCells.Any() && view.SelectedIndices.Count > 0) chart.Link(view.SelectedIndices);
        }
        else
        {
            chart.Activate();
        }
        return chart;
    }

    // ---- editing values ---------------------------------------------------------------

    private void EditValue(PollView view)
    {
        var doc = view.Document;
        var sel = view.SelectedIndices;
        if (sel.Count == 0) return;
        var index = sel[0];
        var def = doc.Definition;
        var address = (ushort)(def.Address + index);

        if (def.IsWriteWindow)
        {
            EditWriteWindowValue(view, index);
            return;
        }

        switch (def.Function)
        {
            case FunctionCode.ReadCoils:
                FunctionDialogs.WriteCoils(def.SlaveId, address, false, doc.Values.ElementAtOrDefault(index) == 0);
                break;
            case FunctionCode.ReadHoldingRegisters:
            case FunctionCode.ReadWriteMultipleRegisters:
                var format = doc.PeekCell(index)?.Format ?? DisplayFormat.Signed;
                var current = doc.Values.Length > index ? RegisterCodec.Format(doc.Values.AsSpan(index), format) : "";
                FunctionDialogs.WriteRegisters(def.SlaveId, address, false, format, current is "-" ? "" : current.Replace(" ", ""));
                break;
            default:
                _statusHint.Text = "Discrete inputs and input registers are read-only.";
                return;
        }
        doc.RequestOnce();
        AppState.Current.Scheduler.Kick(doc);
    }

    private void EditWriteWindowValue(PollView view, int index)
    {
        var doc = view.Document;
        var f = new FormDialog("Enter Value", "definition");
        var format = doc.PeekCell(index)?.Format ?? DisplayFormat.Signed;
        var value = f.AddText(doc.Definition.IsBits ? "Value (1/0)" : $"Value ({format.Label()})", doc.FormatCell(index));
        ushort[] parsed = [];
        f.Validate = () =>
        {
            parsed = doc.Definition.IsBits
                ? [value.Text.Trim() is "1" or "on" or "true" ? (ushort)1 : (ushort)0]
                : RegisterCodec.Parse(value.Text, format);
            return null;
        };
        value.Loaded += (_, _) => value.Focus();
        if (!f.ShowModal()) return;
        doc.SetWriteValues(index, parsed);
        view.Invalidate();
        if (doc.Definition.Disabled) return;
        AppState.Current.Scheduler.Kick(doc);
    }

    // ---- files ------------------------------------------------------------------------

    private const string WorkspaceFilter = "Modbus Master workspace (*.mmw)|*.mmw|All files (*.*)|*.*";
    private const string WindowFilter = "Modbus Master window (*.mmp)|*.mmp|All files (*.*)|*.*";

    private Workspace CurrentWorkspace() => new()
    {
        Connection = AppState.Current.Settings.Clone(),
        Windows = Views.Select(v => v.Document.CloneDefinitionOnly()).ToList(),
    };

    private void SaveWorkspace(bool saveAs)
    {
        if (saveAs || _workspacePath is null)
        {
            var dlg = new SaveFileDialog { Filter = WorkspaceFilter, FileName = "workspace.mmw" };
            if (dlg.ShowDialog(this) != true) return;
            _workspacePath = dlg.FileName;
        }
        var ws = CurrentWorkspace();
        if (ws.Connection is not null) ws.Connection.ClientCertificatePassword = null;
        ws.Save(_workspacePath);
        Title = $"Modbus Master - {Path.GetFileName(_workspacePath)}";
        _statusHint.Text = $"Saved {_workspacePath}";
    }

    private void OpenWorkspace()
    {
        var dlg = new OpenFileDialog { Filter = WorkspaceFilter + "|" + WindowFilter };
        if (dlg.ShowDialog(this) == true) OpenPath(dlg.FileName);
    }

    public void OpenPath(string path)
    {
        try
        {
            var ws = Workspace.Load(path);
            if (path.EndsWith(".mmw", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var v in Views.ToList()) CloseView(v);
                if (ws.Connection is not null) AppState.Current.Settings = ws.Connection;
                _workspacePath = path;
                Title = $"Modbus Master - {Path.GetFileName(path)}";
            }
            foreach (var w in ws.Windows) AddDocument(w);
            UpdateStatus();
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"Could not open {path}:\n{ex.Message}", "Open", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveWindowFile() => WithCurrent(v =>
    {
        var dlg = new SaveFileDialog { Filter = WindowFilter, FileName = v.Document.Title + ".mmp" };
        if (dlg.ShowDialog(this) == true) new Workspace { Windows = [v.Document.CloneDefinitionOnly()] }.Save(dlg.FileName);
    });

    private void OpenWindowFile()
    {
        var dlg = new OpenFileDialog { Filter = WindowFilter };
        if (dlg.ShowDialog(this) == true) OpenPath(dlg.FileName);
    }

    private void ExportCsv() => WithCurrent(v =>
    {
        var doc = v.Document;
        var dlg = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = doc.Title + ".csv" };
        if (dlg.ShowDialog(this) != true) return;
        var values = doc.Definition.IsWriteWindow ? doc.WriteValues : doc.Values;
        var sb = new StringBuilder("Address;Name;Value;Format\r\n");
        for (var i = 0; i < doc.Definition.CellCount; i++)
        {
            if (doc.IsContinuation(i)) continue;
            var cell = doc.PeekCell(i);
            sb.Append(doc.Definition.Address + i).Append(';').Append(cell?.Name ?? "").Append(';')
              .Append(i < values.Length ? doc.FormatCell(i, values) : "").Append(';')
              .Append(doc.Definition.IsBits ? "Bit" : (cell?.Format ?? DisplayFormat.Signed).Label()).Append("\r\n");
        }
        File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(false));
    });

    private void About()
    {
        var version = typeof(MainWindow).Assembly.GetName().Version;
        MessageBox.Show(this,
            $"Modbus Master {version?.ToString(3)}\n\nModbus master for Modbus TCP, UDP, RTU, ASCII, RTU/ASCII over TCP/UDP and Modbus/TCP Security.\n\n" +
            $"Program folder: {AppContext.BaseDirectory}",
            "About Modbus Master", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        foreach (var v in Views) v.Stop();
        foreach (var c in _charts.Values.ToList()) c.Close();
        TrafficWindow.CloseSingle();
        base.OnClosing(e);
    }

    // ---- menu helpers -----------------------------------------------------------------

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
