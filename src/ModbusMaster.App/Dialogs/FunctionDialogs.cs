using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ModbusMaster.App.Ui;
using ModbusMaster.Core;
using ModbusMaster.Core.Data;
using ModbusMaster.Core.Protocol;

namespace ModbusMaster.App.Dialogs;

/// <summary>One-shot requests from the Functions menu. Each dialog stays open so you can send again.</summary>
public static class FunctionDialogs
{
    private static readonly string[] DiagnosticSubFunctions =
    [
        "00 Return Query Data", "01 Restart Communications Option", "02 Return Diagnostic Register",
        "03 Change ASCII Input Delimiter", "04 Force Listen Only Mode", "10 Clear Counters and Diagnostic Register",
        "11 Return Bus Message Count", "12 Return Bus Communication Error Count", "13 Return Bus Exception Error Count",
        "14 Return Server Message Count", "15 Return Server No Response Count", "16 Return Server NAK Count",
        "17 Return Server Busy Count", "18 Return Bus Character Overrun Count", "20 Clear Overrun Counter and Flag",
    ];

    /// <summary>Formats offered for register cells: Signed, Unsigned, Hex, Binary, then the multi-register formats.</summary>
    public static IEnumerable<Choice<DisplayFormat>> RegisterFormats() =>
        DisplayFormats.All.OrderBy(f => f.Registers()).ThenBy(f => f.Kind()).ThenBy(f => f.Order())
            .Select(f => new Choice<DisplayFormat>(f, f.Label()));

    /// <summary>Function 05 or 15.</summary>
    public static void WriteCoils(byte slaveId, ushort address, bool multiple, bool? initial = null)
    {
        var f = Runner(multiple ? "15: Write Multiple Coils" : "05: Write Single Coil", out var send, out var result);
        var slave = f.AddText("Slave ID", slaveId.ToString(), 90);
        var addr = f.AddText("Address", address.ToString(), 90);
        TextBox? values = null;
        ComboBox? value = null;
        if (multiple)
            values = f.AddText("Values (1/0, comma separated)", "1,0,1");
        else
            value = f.AddCombo("Value", new[] { new Choice<bool>(true, "On (FF00)"), new Choice<bool>(false, "Off (0000)") }, initial ?? true);
        f.AddRow("", result);

        Bind(send, result, () =>
        {
            var id = ParseByte(slave.Text);
            var a = ParseWord(addr.Text);
            if (multiple)
            {
                var bits = values!.Text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries)
                    .Select(v => v.Trim().ToLowerInvariant() switch
                    {
                        "1" or "on" or "true" => true,
                        "0" or "off" or "false" => false,
                        var other => throw new FormatException($"'{other}' is not a coil value. Use 1 or 0."),
                    }).ToArray();
                return client => { client.WriteMultipleCoils(id, a, bits); return $"Wrote {bits.Length} coils."; };
            }
            var on = FormDialog.Selected<bool>(value!);
            return client => { client.WriteSingleCoil(id, a, on); return on ? "Coil set On." : "Coil set Off."; };
        });
        f.ShowDialog();
    }

    /// <summary>Function 06 or 16 with any display format.</summary>
    public static void WriteRegisters(byte slaveId, ushort address, bool multiple, DisplayFormat format = DisplayFormat.Signed, string initial = "")
    {
        var f = Runner(multiple ? "16: Write Multiple Registers" : "06: Write Single Register", out var send, out var result);
        var slave = f.AddText("Slave ID", slaveId.ToString(), 90);
        var addr = f.AddText("Address", address.ToString(), 90);
        var fmt = f.AddCombo("Format", RegisterFormats(), format);
        var values = f.AddText(multiple ? "Values (comma separated)" : "Value", initial);
        var use16 = f.AddCheck("Use function 16 (needed for 32/64-bit values)", multiple || format.Registers() > 1);
        use16.IsEnabled = !multiple;
        fmt.SelectionChanged += (_, _) => { if (FormDialog.Selected<DisplayFormat>(fmt).Registers() > 1) use16.IsChecked = true; };
        f.AddRow("", result);
        values.Loaded += (_, _) => values.Focus();

        Bind(send, result, () =>
        {
            var id = ParseByte(slave.Text);
            var a = ParseWord(addr.Text);
            var df = FormDialog.Selected<DisplayFormat>(fmt);
            var registers = values.Text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .SelectMany(v => RegisterCodec.Parse(v, df)).ToArray();
            if (registers.Length == 0) throw new FormatException("Enter a value.");
            var multipleWrite = use16.IsChecked == true || registers.Length > 1;
            return client =>
            {
                if (multipleWrite)
                {
                    client.WriteMultipleRegisters(id, a, registers);
                    return $"Wrote {registers.Length} register(s): {string.Join(" ", registers.Select(r => r.ToString("X4")))} hex.";
                }
                client.WriteSingleRegister(id, a, registers[0]);
                return $"Register written: 0x{registers[0]:X4}.";
            };
        });
        f.ShowDialog();
    }

    /// <summary>Function 22.</summary>
    public static void MaskWrite(byte slaveId, ushort address)
    {
        var f = Runner("22: Mask Write Register", out var send, out var result);
        var slave = f.AddText("Slave ID", slaveId.ToString(), 90);
        var addr = f.AddText("Address", address.ToString(), 90);
        var and = f.AddText("AND mask", "0xFFFF", 90);
        var or = f.AddText("OR mask", "0x0000", 90);
        f.AddInfo("Result = (Current AND And_Mask) OR (Or_Mask AND (NOT And_Mask))");
        f.AddRow("", result);
        Bind(send, result, () =>
        {
            var id = ParseByte(slave.Text);
            var a = ParseWord(addr.Text);
            var andMask = ParseWord(and.Text);
            var orMask = ParseWord(or.Text);
            return client => { client.MaskWriteRegister(id, a, andMask, orMask); return "Mask write done."; };
        });
        f.ShowDialog();
    }

    /// <summary>Function 23 as a single request.</summary>
    public static void ReadWrite(byte slaveId, ushort address)
    {
        var f = Runner("23: Read/Write Multiple Registers", out var send, out var result);
        var slave = f.AddText("Slave ID", slaveId.ToString(), 90);
        var readAddr = f.AddText("Read address", address.ToString(), 90);
        var readCount = f.AddText("Read quantity", "1", 90);
        var writeAddr = f.AddText("Write address", address.ToString(), 90);
        var values = f.AddText("Write values (comma separated)", "0");
        f.AddRow("", result);
        Bind(send, result, () =>
        {
            var id = ParseByte(slave.Text);
            var ra = ParseWord(readAddr.Text);
            var rc = (int)Hex.ParseNumber(readCount.Text);
            var wa = ParseWord(writeAddr.Text);
            var regs = values.Text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(ParseWord).ToArray();
            return client => "Read: " + string.Join(", ", client.ReadWriteMultipleRegisters(id, ra, rc, wa, regs));
        });
        f.ShowDialog();
    }

    /// <summary>Function 17.</summary>
    public static void ReportServerId(byte slaveId)
    {
        var f = Runner("17: Report Server ID", out var send, out var result);
        var slave = f.AddText("Slave ID", slaveId.ToString(), 90);
        f.AddRow("", result);
        Bind(send, result, () =>
        {
            var id = ParseByte(slave.Text);
            return client =>
            {
                var data = client.ReportServerId(id);
                var text = new string(data.Select(b => b is >= 0x20 and <= 0x7E ? (char)b : '.').ToArray());
                return $"Hex: {Hex.Format(data)}\nText: {text}";
            };
        });
        f.ShowDialog();
    }

    /// <summary>Function 08.</summary>
    public static void Diagnostics(byte slaveId)
    {
        var f = Runner("08: Diagnostics", out var send, out var result);
        var slave = f.AddText("Slave ID", slaveId.ToString(), 90);
        var sub = f.AddCombo("Sub-function", DiagnosticSubFunctions.Select(s => new Choice<ushort>(ushort.Parse(s[..2]), s)), (ushort)0);
        var data = f.AddText("Data", "0x0000", 90);
        f.AddInfo("Sub-function 01: data must be 0x0000 or 0xFF00 (0xFF00 also clears the event log). Sub-function 04 stops the slave from answering until it is restarted.");
        f.AddRow("", result);
        Bind(send, result, () =>
        {
            var id = ParseByte(slave.Text);
            var s = FormDialog.Selected<ushort>(sub);
            var d = ParseWord(data.Text);
            if (s == 1 && d is not (0x0000 or 0xFF00)) throw new FormatException("Valid values are 0x0000 or 0xFF00 for sub-function 01.");
            return client =>
            {
                var r = client.Diagnostics(id, s, d);
                return id == 0 ? "Broadcast sent (no response)." : $"Response data: {r} (0x{r:X4})";
            };
        });
        f.ShowDialog();
    }

    // ---- helpers ----

    private static FormDialog Runner(string title, out Button send, out TextBlock result)
    {
        var f = new FormDialog(title, "functions", okCancel: false);
        send = f.AddButton("Send", () => { }, isDefault: true);
        result = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 420, MinHeight = 34, Margin = new Thickness(0, 8, 0, 0) };
        return f;
    }

    /// <summary>
    /// <paramref name="prepare"/> runs on the UI thread (reads and validates the fields) and returns the
    /// request, which then runs on a worker thread so the dialog stays responsive.
    /// </summary>
    private static void Bind(Button send, TextBlock result, Func<Func<ModbusClient, string>> prepare)
    {
        send.Click += async (_, _) =>
        {
            var client = AppState.Current.Client;
            if (client is null)
            {
                Show(result, "No connection. Connect first (Connection > Connect, F3).", error: true);
                return;
            }
            send.IsEnabled = false;
            try
            {
                var request = prepare();
                Show(result, "Sending...", error: false);
                var text = await Task.Run(() => request(client));
                Show(result, text, error: false);
            }
            catch (Exception ex) when (ex is ModbusSlaveException or ModbusCommunicationException or FormatException
                                           or OverflowException or ArgumentException or InvalidOperationException)
            {
                Show(result, ex.Message, error: true);
            }
            finally
            {
                send.IsEnabled = true;
            }
        };
    }

    private static void Show(TextBlock result, string text, bool error)
    {
        result.Text = text;
        result.Foreground = error ? Brushes.Firebrick : Brushes.DarkGreen;
    }

    internal static byte ParseByte(string text)
    {
        var n = Hex.ParseNumber(text);
        if (n is < 0 or > 255) throw new FormatException("Slave ID must be 0 to 255.");
        return (byte)n;
    }

    internal static ushort ParseWord(string text)
    {
        var n = Hex.ParseNumber(text);
        if (n is < 0 or > 65535) throw new FormatException("Value must be 0 to 65535.");
        return (ushort)n;
    }
}
