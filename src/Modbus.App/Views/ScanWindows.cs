using Modbus.Ui.Automation;
using Modbus.Ui.Views;
using Modbus.Ui.Dialogs;
using Modbus.Ui;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Modbus.Core;
using Modbus.Core.Polling;
using Modbus.Core.Protocol;

namespace Modbus.App.Views;

public sealed record ScanRow(string Key, string Value, string Result);

/// <summary>Shared layout for Address Scan and Slave Scan: settings on top, results grid, Start/Stop/Export.</summary>
public abstract class ScanWindowBase : Window
{
    private readonly Grid _form = new() { Margin = new Thickness(10, 10, 10, 4) };
    private readonly Button _start = new() { Content = "Start", MinWidth = 80, Margin = new Thickness(0, 0, 6, 0) };
    private readonly TextBlock _progress = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), Foreground = Brushes.DimGray };
    private readonly string _help;
    private CancellationTokenSource? _cts;
    private int _formRow;

    protected ScanWindowBase(string title, string help, string keyHeader)
    {
        Title = title;
        _help = help;
        Width = 560;
        Height = 560;
        if (Application.Current.MainWindow is { IsVisible: true } owner) Owner = owner;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _form.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var grid = new DataGrid
        {
            AutoGenerateColumns = false, IsReadOnly = true, ItemsSource = Rows, HeadersVisibility = DataGridHeadersVisibility.Column,
            FontFamily = new FontFamily("Consolas"), Margin = new Thickness(10, 4, 10, 4), GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
        };
        grid.Columns.Add(new DataGridTextColumn { Header = keyHeader, Binding = new Binding(nameof(ScanRow.Key)), Width = 90 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Value", Binding = new Binding(nameof(ScanRow.Value)), Width = 140 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Result", Binding = new Binding(nameof(ScanRow.Result)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });

        var export = new Button { Content = "Export CSV...", MinWidth = 90, Margin = new Thickness(0, 0, 6, 0) };
        export.Click += (_, _) => Export();
        var helpButton = new Button { Content = "Help", MinWidth = 70 };
        helpButton.Click += (_, _) => Help.Open(_help);
        _start.Click += (_, _) => { if (_cts is null) Start(); else _cts.Cancel(); };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 4, 10, 10) };
        buttons.Children.Add(_start);
        buttons.Children.Add(export);
        buttons.Children.Add(helpButton);
        buttons.Children.Add(_progress);

        var dock = new DockPanel();
        DockPanel.SetDock(_form, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        dock.Children.Add(_form);
        dock.Children.Add(buttons);
        dock.Children.Add(grid);
        Content = dock;
        Closed += (_, _) => _cts?.Cancel();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.F1) Help.Open(_help); };
    }

    protected ObservableCollection<ScanRow> Rows { get; } = [];

    protected TextBox Field(string label, string value)
    {
        _form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var l = new Label { Content = label, Padding = new Thickness(0, 3, 10, 3) };
        var t = new TextBox { Text = value, Width = 120, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 2) };
        Grid.SetRow(l, _formRow);
        Grid.SetRow(t, _formRow);
        Grid.SetColumn(t, 1);
        _formRow++;
        _form.Children.Add(l);
        _form.Children.Add(t);
        return t;
    }

    protected ComboBox FunctionField(FunctionCode selected)
    {
        _form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var l = new Label { Content = "Function", Padding = new Thickness(0, 3, 10, 3) };
        var c = new ComboBox { Width = 260, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 2) };
        foreach (var f in new[] { FunctionCode.ReadCoils, FunctionCode.ReadDiscreteInputs, FunctionCode.ReadHoldingRegisters, FunctionCode.ReadInputRegisters })
            c.Items.Add(new Choice<FunctionCode>(f, FunctionCodes.Describe(f)));
        c.SelectedIndex = (int)selected - 1;
        Grid.SetRow(l, _formRow);
        Grid.SetRow(c, _formRow);
        Grid.SetColumn(c, 1);
        _formRow++;
        _form.Children.Add(l);
        _form.Children.Add(c);
        return c;
    }

    /// <summary>Reads the fields on the UI thread and returns the scan to run on a worker thread.</summary>
    protected abstract Action<ModbusClient, CancellationToken, Action<ScanRow, string>> Prepare();

    private async void Start()
    {
        var client = AppState.Current.Client;
        if (client is null)
        {
            MessageBox.Show(this, "No connection. Connect first (F3).", Title);
            return;
        }
        Action<ModbusClient, CancellationToken, Action<ScanRow, string>> scan;
        try
        {
            scan = Prepare();
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Rows.Clear();
        _cts = new CancellationTokenSource();
        _start.Content = "Stop";
        var token = _cts.Token;
        try
        {
            await Task.Run(() => scan(client, token, (row, progress) => Dispatcher.BeginInvoke(() =>
            {
                if (row.Key.Length > 0) Rows.Add(row);
                _progress.Text = progress;
            })), token);
            _progress.Text = $"Done. {Rows.Count} found.";
        }
        catch (OperationCanceledException)
        {
            _progress.Text = "Stopped.";
        }
        catch (ModbusCommunicationException ex)
        {
            _progress.Text = ex.Message;
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            _start.Content = "Start";
        }
    }

    private void Export()
    {
        var dlg = new SaveFileDialog { Filter = "Scan data (*.csv)|*.csv", FileName = "modbus_scan.csv" };
        if (dlg.ShowDialog(this) != true) return;
        var sb = new StringBuilder("Key;Value;Result\r\n");
        foreach (var r in Rows) sb.Append(r.Key).Append(';').Append(r.Value).Append(';').Append(r.Result).Append("\r\n");
        File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(false));
    }
}

/// <summary>Finds which addresses of one slave can be read.</summary>
public sealed class AddressScanWindow : ScanWindowBase
{
    private readonly TextBox _slave;
    private readonly ComboBox _function;
    private readonly TextBox _from;
    private readonly TextBox _to;
    private readonly TextBox _block;
    private readonly CheckBox _showErrors = new() { Content = "Show addresses that return an exception", Margin = new Thickness(10, 0, 10, 4) };

    public AddressScanWindow(byte slaveId) : base("Address Scan", "address-scan", "Address")
    {
        _slave = Field("Slave ID", slaveId.ToString(CultureInfo.InvariantCulture));
        _function = FunctionField(FunctionCode.ReadHoldingRegisters);
        _from = Field("From address", "0");
        _to = Field("To address", "999");
        _block = Field("Block size", "100");
        if (Content is DockPanel dock)
        {
            DockPanel.SetDock(_showErrors, Dock.Top);
            dock.Children.Insert(1, _showErrors);
        }
    }

    protected override Action<ModbusClient, CancellationToken, Action<ScanRow, string>> Prepare()
    {
        var slave = (byte)Hex.ParseNumber(_slave.Text);
        var function = FormDialog.Selected<FunctionCode>(_function);
        var from = (int)Hex.ParseNumber(_from.Text);
        var to = (int)Hex.ParseNumber(_to.Text);
        var block = (int)Hex.ParseNumber(_block.Text);
        var showErrors = _showErrors.IsChecked == true;
        if (from < 0 || to > 65535 || to < from) throw new FormatException("The address range must be within 0 to 65535.");
        return (client, cancel, report) =>
        {
            foreach (var r in Scanners.ScanAddresses(client, slave, function, from, to, block, cancel))
            {
                var ok = r.Result == ModbusResult.Success;
                if (ok || showErrors)
                    report(new ScanRow(r.Address.ToString(CultureInfo.InvariantCulture), r.Value?.ToString(CultureInfo.InvariantCulture) ?? "",
                        ok ? "OK" : ModbusResults.Describe(r.Result)), $"Address {r.Address}");
                else
                    report(new ScanRow("", "", ""), $"Address {r.Address}");
            }
        };
    }
}

/// <summary>Finds which slave IDs answer on the bus.</summary>
public sealed class SlaveScanWindow : ScanWindowBase
{
    private readonly TextBox _from;
    private readonly TextBox _to;
    private readonly ComboBox _function;
    private readonly TextBox _address;
    private readonly TextBox _timeout;

    public SlaveScanWindow() : base("Slave Scan", "slave-scan", "Slave ID")
    {
        _from = Field("From slave ID", "1");
        _to = Field("To slave ID", "247");
        _function = FunctionField(FunctionCode.ReadHoldingRegisters);
        _address = Field("Address", "0");
        _timeout = Field("Response timeout [ms]", "200");
    }

    protected override Action<ModbusClient, CancellationToken, Action<ScanRow, string>> Prepare()
    {
        var from = (int)Hex.ParseNumber(_from.Text);
        var to = (int)Hex.ParseNumber(_to.Text);
        if (from < 1 || to > 255 || to < from) throw new FormatException("Slave IDs must be 1 to 255.");
        var function = FormDialog.Selected<FunctionCode>(_function);
        var address = (ushort)Hex.ParseNumber(_address.Text);
        var timeout = TimeSpan.FromMilliseconds(Hex.ParseNumber(_timeout.Text));
        return (client, cancel, report) =>
        {
            var saved = client.ResponseTimeout;
            client.ResponseTimeout = timeout;
            try
            {
                foreach (var r in Scanners.ScanSlaves(client, from, to, function, address, 1, cancel))
                {
                    report(r.Found ? new ScanRow(r.SlaveId.ToString(CultureInfo.InvariantCulture), r.Result == ModbusResult.Success ? r.Detail : "",
                        r.Result == ModbusResult.Success ? "Answered" : ModbusResults.Describe(r.Result)) : new ScanRow("", "", ""), $"Slave ID {r.SlaveId}");
                }
            }
            finally
            {
                client.ResponseTimeout = saved;
            }
        };
    }
}
