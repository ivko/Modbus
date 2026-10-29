using Modbus.Ui.Automation;
using Modbus.Ui.Views;
using Modbus.Ui.Dialogs;
using Modbus.Ui;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Modbus.Core;
using Modbus.Core.Framing;
using Modbus.Core.Protocol;

namespace Modbus.App.Views;

/// <summary>
/// Test Center: send any bytes and see the raw answer. With "Add checksum" on, type slave ID + PDU and
/// the program adds CRC (RTU), LRC and framing (ASCII) or the MBAP header (TCP/UDP).
/// Test strings can be saved to a text file; lines starting with # are comments.
/// </summary>
public sealed class TestCenterWindow : Window
{
    private readonly TextBox _input = new() { FontFamily = new FontFamily("Consolas"), FontSize = 13, Padding = new Thickness(3) };
    private readonly CheckBox _addChecksum = new() { Content = "Add checksum / header", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
    private readonly ListBox _strings = new() { FontFamily = new FontFamily("Consolas"), FontSize = 12 };
    private readonly TextBox _output = new()
    {
        FontFamily = new FontFamily("Consolas"), FontSize = 12, IsReadOnly = true, AcceptsReturn = true,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.NoWrap,
    };
    private ushort _tid = 0x1000;

    public TestCenterWindow()
    {
        AppIcon.Apply(this);
        Title = "Test Center";
        Width = 820;
        Height = 520;
        if (Application.Current.MainWindow is { IsVisible: true } owner) Owner = owner;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var send = new Button { Content = "Send", MinWidth = 80, IsDefault = true, Margin = new Thickness(6, 0, 0, 0) };
        send.Click += async (_, _) => await SendAsync(_input.Text);
        var inputRow = new DockPanel { Margin = new Thickness(8) };
        DockPanel.SetDock(send, Dock.Right);
        DockPanel.SetDock(_addChecksum, Dock.Right);
        inputRow.Children.Add(send);
        inputRow.Children.Add(_addChecksum);
        inputRow.Children.Add(_input);

        var bar = new ToolBar();
        Add(bar, "Add to list", () => { if (_input.Text.Trim().Length > 0) _strings.Items.Add(_input.Text.Trim()); });
        Add(bar, "Remove", () => { if (_strings.SelectedItem is not null) _strings.Items.Remove(_strings.SelectedItem); });
        Add(bar, "Open strings...", OpenStrings);
        Add(bar, "Save strings...", SaveStrings);
        bar.Items.Add(new Separator());
        Add(bar, "Clear output", () => _output.Clear());
        Add(bar, "Copy output", () => Clipboard.SetText(_output.Text));
        Add(bar, "Help", () => Help.Open("test-center"));

        _strings.MouseDoubleClick += async (_, _) =>
        {
            if (_strings.SelectedItem is string s && !s.StartsWith('#'))
            {
                _input.Text = s;
                await SendAsync(s);
            }
        };
        _strings.SelectionChanged += (_, _) => { if (_strings.SelectedItem is string s && !s.StartsWith('#')) _input.Text = s; };

        var hint = new TextBlock
        {
            Text = "Hex bytes, e.g. \"01 03 00 00 00 0A\" (slave ID + PDU). Start with ':' to send ASCII text as typed. " +
                   "Double click a saved string to send it.",
            Margin = new Thickness(8, 0, 8, 4), Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap,
        };

        var split = new Grid { Margin = new Thickness(8, 0, 8, 8) };
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        split.Children.Add(Labeled("Test strings", _strings));
        var splitter = new GridSplitter { Width = 6, HorizontalAlignment = HorizontalAlignment.Stretch };
        Grid.SetColumn(splitter, 1);
        split.Children.Add(splitter);
        var outBox = Labeled("Output", _output);
        Grid.SetColumn(outBox, 2);
        split.Children.Add(outBox);

        var dock = new DockPanel();
        foreach (var top in new FrameworkElement[] { bar, inputRow, hint })
        {
            DockPanel.SetDock(top, Dock.Top);
            dock.Children.Add(top);
        }
        dock.Children.Add(split);
        Content = dock;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.F1) Help.Open("test-center"); };
        Loaded += (_, _) => _input.Focus();
    }

    private async Task SendAsync(string text)
    {
        var client = AppState.Current.Client;
        if (client is null)
        {
            Append("No connection. Connect first (F3).");
            return;
        }

        byte[] frame;
        try
        {
            frame = BuildFrame(text.Trim());
        }
        catch (FormatException ex)
        {
            Append("Input error: " + ex.Message);
            return;
        }

        var ascii = frame.Length > 0 && frame[0] == (byte)':';
        Append($"Tx: {(ascii ? Encoding.ASCII.GetString(frame).TrimEnd() : Hex.Format(frame))}");
        try
        {
            var response = await Task.Run(() => client.SendRaw(frame, TimeSpan.FromMilliseconds(60)));
            if (response.Length == 0)
            {
                Append("Rx: (no response)");
                return;
            }
            Append($"Rx: {(ascii ? Encoding.ASCII.GetString(response).TrimEnd() : Hex.Format(response))}");
            var note = Check(response);
            if (note.Length > 0) Append("    " + note);
        }
        catch (ModbusCommunicationException ex)
        {
            Append("Error: " + ex.Message);
        }
    }

    private byte[] BuildFrame(string text)
    {
        if (text.StartsWith(':'))
        {
            if (_addChecksum.IsChecked == true)
            {
                var raw = Hex.Parse(text[1..]);
                if (raw.Length < 2) throw new FormatException("Enter at least slave ID and function code.");
                return AsciiFramer.Encode(raw[0], raw.AsSpan(1));
            }
            return Encoding.ASCII.GetBytes(text + "\r\n");
        }

        var bytes = Hex.Parse(text);
        if (bytes.Length == 0) throw new FormatException("Enter some hex bytes.");
        if (_addChecksum.IsChecked != true) return bytes;
        if (bytes.Length < 2) throw new FormatException("Enter at least slave ID and function code.");
        var s = AppState.Current.Settings;
        return s.Kind switch
        {
            ConnectionKind.Tcp or ConnectionKind.Udp or ConnectionKind.TcpSecurity => MbapFramer.Encode(++_tid, bytes[0], bytes.AsSpan(1)),
            _ when s.Mode == SerialMode.Ascii => AsciiFramer.Encode(bytes[0], bytes.AsSpan(1)),
            _ => RtuFramer.Encode(bytes[0], bytes.AsSpan(1)),
        };
    }

    private static string Check(byte[] response)
    {
        var s = AppState.Current.Settings;
        if (s.Kind is ConnectionKind.Tcp or ConnectionKind.Udp or ConnectionKind.TcpSecurity)
            return response.Length >= 8 && (response[7] & 0x80) != 0 ? $"Exception {response[8]:D2}: {ModbusSlaveException.Describe(response[8])}" : "";
        if (s.Mode == SerialMode.Ascii)
        {
            try
            {
                var raw = AsciiFramer.Decode(response);
                return (raw[1] & 0x80) != 0 ? $"LRC ok. Exception {raw[2]:D2}: {ModbusSlaveException.Describe(raw[2])}" : "LRC ok.";
            }
            catch (ModbusFrameException ex)
            {
                return ex.Message;
            }
        }
        if (!RtuFramer.CheckCrc(response)) return "CRC error (or not a complete RTU frame).";
        return (response[1] & 0x80) != 0 ? $"CRC ok. Exception {response[2]:D2}: {ModbusSlaveException.Describe(response[2])}" : "CRC ok.";
    }

    private void Append(string line)
    {
        _output.AppendText($"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
        _output.ScrollToEnd();
    }

    private void OpenStrings()
    {
        var dlg = new OpenFileDialog { Filter = "Test Center strings (*.txt)|*.txt|All files|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        _strings.Items.Clear();
        foreach (var line in File.ReadAllLines(dlg.FileName).Where(l => l.Trim().Length > 0)) _strings.Items.Add(line.Trim());
    }

    private void SaveStrings()
    {
        var dlg = new SaveFileDialog { Filter = "Test Center strings (*.txt)|*.txt", FileName = "test-strings.txt" };
        if (dlg.ShowDialog(this) == true) File.WriteAllLines(dlg.FileName, _strings.Items.Cast<string>());
    }

    private static DockPanel Labeled(string label, FrameworkElement content)
    {
        var p = new DockPanel();
        var l = new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 3) };
        DockPanel.SetDock(l, Dock.Top);
        p.Children.Add(l);
        p.Children.Add(content);
        return p;
    }

    private static void Add(ToolBar bar, string text, Action action)
    {
        var b = new Button { Content = text };
        b.Click += (_, _) => action();
        bar.Items.Add(b);
    }
}
