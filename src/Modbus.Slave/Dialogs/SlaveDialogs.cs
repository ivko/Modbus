using System.Globalization;
using System.IO.Ports;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Modbus.Core;
using Modbus.Core.Channels;
using Modbus.Core.Data;
using Modbus.Core.Polling;
using Modbus.Core.Protocol;
using Modbus.Core.Server;
using Modbus.Core.Slave;
using Modbus.Ui;

namespace Modbus.Slave.Dialogs;

public static class SlaveDialogs
{
    private static string N(double v) => v.ToString(CultureInfo.InvariantCulture);
    private static double D(string s) => double.Parse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture);

    public static readonly FunctionCode[] Tables =
        [FunctionCode.ReadCoils, FunctionCode.ReadDiscreteInputs, FunctionCode.ReadHoldingRegisters, FunctionCode.ReadInputRegisters];

    public static string TableName(FunctionCode f) => f switch
    {
        FunctionCode.ReadCoils => "01 Coil Status (0x)",
        FunctionCode.ReadDiscreteInputs => "02 Input Status (1x)",
        FunctionCode.ReadHoldingRegisters => "03 Holding Registers (4x)",
        _ => "04 Input Registers (3x)",
    };

    // ---- connection (F3) ----

    public static ServerSettings? Connection(ServerSettings current)
    {
        var s = current.Clone();
        var c = s.Connection;
        var f = new FormDialog("Connection Setup", "connection");
        var kind = f.AddCombo("Connection", new[]
        {
            new Choice<ConnectionKind>(ConnectionKind.Serial, "Serial port"),
            new Choice<ConnectionKind>(ConnectionKind.Tcp, "Modbus TCP/IP"),
            new Choice<ConnectionKind>(ConnectionKind.Udp, "Modbus UDP/IP"),
            new Choice<ConnectionKind>(ConnectionKind.RtuAsciiOverTcp, "Modbus RTU/ASCII over TCP/IP"),
            new Choice<ConnectionKind>(ConnectionKind.RtuAsciiOverUdp, "Modbus RTU/ASCII over UDP/IP"),
            new Choice<ConnectionKind>(ConnectionKind.TcpSecurity, "Modbus/TCP Security (TLS)"),
        }, c.Kind);

        f.AddSection("Serial settings");
        var ports = SerialChannel.AvailablePorts().Select(p => new Choice<string>(p, p)).ToList();
        if (!ports.Any(p => p.Value.Equals(c.SerialPort, StringComparison.OrdinalIgnoreCase))) ports.Insert(0, new Choice<string>(c.SerialPort, c.SerialPort));
        var port = f.AddCombo("Serial port", ports, c.SerialPort, editable: true);
        var baud = f.AddCombo("Baud rate", ConnectionSettings.StandardBaudRates.Select(b => new Choice<int>(b, b.ToString())), c.BaudRate, editable: true);
        var dataBits = f.AddCombo("Data bits", new[] { new Choice<int>(7, "7"), new Choice<int>(8, "8") }, c.DataBits);
        var parity = f.AddCombo("Parity", new[] { new Choice<Parity>(Parity.None, "None"), new Choice<Parity>(Parity.Even, "Even"), new Choice<Parity>(Parity.Odd, "Odd") }, c.Parity);
        var stopBits = f.AddCombo("Stop bits", new[] { new Choice<StopBits>(StopBits.One, "1"), new Choice<StopBits>(StopBits.Two, "2") }, c.StopBits);
        var mode = f.AddCombo("Mode", new[] { new Choice<SerialMode>(SerialMode.Rtu, "RTU"), new Choice<SerialMode>(SerialMode.Ascii, "ASCII") }, c.Mode);
        var dtr = f.AddCheck("DTR on", c.DtrEnable);
        var rts = f.AddCheck("RTS on", c.RtsEnable);

        f.AddSection("TCP/IP server");
        var any = f.AddCheck("Any address (listen on all network interfaces)", s.AnyAddress);
        var host = f.AddText("IP address to listen on", c.Host);
        var tcpPort = f.AddText("Port", c.Port.ToString(), 90);
        var ipv6 = f.AddCheck("IPv6", c.IpVersion == 6);
        var ignore = f.AddCheck("Ignore unit ID (answer with the first slave for unknown IDs)", s.IgnoreUnitId);

        f.AddSection("Modbus/TCP Security");
        var cert = Browse(f, "Server certificate", c.ClientCertificatePath, "Certificates (*.pfx;*.p12;*.pem;*.crt)|*.pfx;*.p12;*.pem;*.crt|All files|*.*");
        var key = Browse(f, "Private key (.pem, if separate)", c.ClientKeyPath, "Keys (*.pem;*.key)|*.pem;*.key|All files|*.*");
        var password = f.AddPassword("Certificate or key password", c.ClientCertificatePassword);
        var ca = Browse(f, "Trusted client CA (optional)", c.TrustedCaPath, "Certificates (*.pem;*.cer;*.crt)|*.pem;*.cer;*.crt|All files|*.*");
        var requireClient = f.AddCheck("Require a client certificate", s.RequireClientCertificate);

        var serial = new FrameworkElement[] { port, baud, dataBits, parity, stopBits, dtr, rts };
        var net = new FrameworkElement[] { any, host, tcpPort, ipv6, ignore };
        var tls = new FrameworkElement[] { cert, key, password, ca, requireClient };
        void Update()
        {
            var k = FormDialog.Selected<ConnectionKind>(kind);
            foreach (var e in serial) e.IsEnabled = k == ConnectionKind.Serial;
            foreach (var e in net) e.IsEnabled = k != ConnectionKind.Serial;
            foreach (var e in tls) e.IsEnabled = k == ConnectionKind.TcpSecurity;
            mode.IsEnabled = k is ConnectionKind.Serial or ConnectionKind.RtuAsciiOverTcp or ConnectionKind.RtuAsciiOverUdp;
            host.IsEnabled = k != ConnectionKind.Serial && any.IsChecked != true;
        }
        kind.SelectionChanged += (_, _) =>
        {
            var k = FormDialog.Selected<ConnectionKind>(kind);
            if (k == ConnectionKind.TcpSecurity && tcpPort.Text == "502") tcpPort.Text = "802";
            if (k != ConnectionKind.TcpSecurity && tcpPort.Text == "802") tcpPort.Text = "502";
            Update();
        };
        any.Click += (_, _) => Update();
        Update();

        f.Validate = () =>
        {
            c.Kind = FormDialog.Selected<ConnectionKind>(kind);
            c.SerialPort = (port.SelectedItem as Choice<string>)?.Value ?? port.Text.Trim().ToUpperInvariant();
            c.BaudRate = baud.SelectedItem is Choice<int> b ? b.Value : int.Parse(baud.Text, CultureInfo.InvariantCulture);
            c.DataBits = FormDialog.Selected<int>(dataBits);
            c.Parity = FormDialog.Selected<Parity>(parity);
            c.StopBits = FormDialog.Selected<StopBits>(stopBits);
            c.Mode = FormDialog.Selected<SerialMode>(mode);
            c.DtrEnable = dtr.IsChecked == true;
            c.RtsEnable = rts.IsChecked == true;
            s.AnyAddress = any.IsChecked == true;
            c.Host = host.Text.Trim();
            c.Port = int.Parse(tcpPort.Text, CultureInfo.InvariantCulture);
            c.IpVersion = ipv6.IsChecked == true ? 6 : 4;
            s.IgnoreUnitId = ignore.IsChecked == true;
            c.ClientCertificatePath = Blank(cert.Text);
            c.ClientKeyPath = Blank(key.Text);
            c.ClientCertificatePassword = Blank(password.Password);
            c.TrustedCaPath = Blank(ca.Text);
            s.RequireClientCertificate = requireClient.IsChecked == true;
            if (c.Port is < 1 or > 65535) return "The port must be 1 to 65535.";
            if (c.Kind == ConnectionKind.TcpSecurity && c.ClientCertificatePath is null) return "Modbus/TCP Security needs a server certificate.";
            if (c.Kind != ConnectionKind.Serial && !s.AnyAddress && !System.Net.IPAddress.TryParse(c.Host, out _)) return "Enter an IP address of this computer, or choose Any address.";
            return null;
        };
        return f.ShowModal() ? s : null;
    }

    // ---- slave definition (F8) ----

    public static (PollDefinition Definition, string Title)? Definition(PollDefinition current, string title)
    {
        var d = current.Clone();
        var f = new FormDialog("Slave Definition", "definition");
        var name = f.AddText("Window name", title);
        f.AddInfo("Leave the name empty to name the tab after its definition, e.g. \"ID 1 · 03 Holding 0–9\".");
        var slave = f.AddText("Slave ID", d.SlaveId.ToString(), 90);
        f.AddInfo("1 to 247 on a serial line (255 allowed). On TCP many masters send 0 or 255; turn on Ignore unit ID in the connection setup to answer them.");
        var table = f.AddCombo("Function", Tables.Select(t => new Choice<FunctionCode>(t, TableName(t))), d.Function);
        f.AddInfo("01 also accepts writes 05 and 15. 03 also accepts 06, 16, 22 and 23.");
        var address = f.AddText("Address", d.Address.ToString(), 90);
        var plc = f.AddCheck("PLC addresses (base 1: 40001...)", d.PlcAddresses);
        var quantity = f.AddText("Quantity (1 to 10000)", d.Quantity.ToString(), 90);
        var disabled = f.AddCheck("Disabled (the slave does not answer requests for this window)", d.Disabled);
        if (d.PlcAddresses) address.Text = (d.Address + FunctionCodes.PlcBase(d.Function)).ToString();
        f.Validate = () =>
        {
            var id = Hex.ParseNumber(slave.Text);
            if (id is < 0 or > 255) return "Slave ID must be 0 to 255.";
            d.SlaveId = (byte)id;
            d.Function = FormDialog.Selected<FunctionCode>(table);
            d.PlcAddresses = plc.IsChecked == true;
            var a = Hex.ParseNumber(address.Text);
            if (d.PlcAddresses)
            {
                var plcBase = FunctionCodes.PlcBase(d.Function);
                a = a >= plcBase ? a - plcBase : a - 1;
            }
            if (a is < 0 or > 65535) return "Address must be 0 to 65535.";
            d.Address = (ushort)a;
            d.Quantity = (int)Hex.ParseNumber(quantity.Text);
            if (d.Quantity is < 1 or > 10000) return "Quantity must be 1 to 10000.";
            if (d.Address + d.Quantity > 65536) return "Address + quantity exceeds 65535.";
            d.Disabled = disabled.IsChecked == true;
            d.ScanRateMs = 0;
            return null;
        };
        return f.ShowModal() ? (d, name.Text.Trim()) : null;
    }

    // ---- error simulation ----

    public static bool ErrorSimulation(SlaveDevice device)
    {
        var e = device.Errors.Clone();
        var f = new FormDialog($"Error Simulation - Slave ID {device.Id}", "error-simulation");
        f.AddInfo("Make this slave misbehave to test how a master handles a bad device. Percentages are 0 to 100 and are applied per request.");
        var enabled = f.AddCheck("Error simulation on", e.Enabled);
        f.AddSection("Timing");
        var delay = f.AddText("Response delay [ms]", e.ResponseDelayMs.ToString(), 90);
        var jitter = f.AddText("Random extra delay up to [ms]", e.DelayJitterMs.ToString(), 90);
        f.AddSection("Missing answers");
        var skip = f.AddText("No response [%]", N(e.NoResponsePercent), 90);
        var everyN = f.AddText("No response every Nth request (0 = off)", e.NoResponseEveryN.ToString(), 90);
        f.AddSection("Damaged answers");
        var crc = f.AddText("Wrong CRC/LRC [%]", N(e.ChecksumErrorPercent), 90);
        f.AddInfo("On TCP/UDP, which has no checksum, the MBAP protocol identifier is made invalid instead.");
        var truncate = f.AddText("Truncated answer [%]", N(e.TruncatePercent), 90);
        var wrongId = f.AddText("Answer with another slave ID [%]", N(e.WrongIdPercent), 90);
        f.AddSection("Exception responses");
        var code = f.AddCombo("Exception", new[] { 0, 1, 2, 3, 4, 5, 6, 8, 10, 11 }.Select(x =>
            new Choice<byte>((byte)x, x == 0 ? "None" : $"{x:D2} {ModbusSlaveException.Describe((byte)x)}")), e.ExceptionCode);
        var function = f.AddText("Only for function (0 = all)", e.ExceptionFunction.ToString(), 90);
        var exceptionPercent = f.AddText("Exception [%]", N(e.ExceptionPercent), 90);

        f.Validate = () =>
        {
            e.Enabled = enabled.IsChecked == true;
            e.ResponseDelayMs = Range(int.Parse(delay.Text, CultureInfo.InvariantCulture), 0, 60000, "Response delay");
            e.DelayJitterMs = Range(int.Parse(jitter.Text, CultureInfo.InvariantCulture), 0, 60000, "Random delay");
            e.NoResponsePercent = Percent(skip.Text);
            e.NoResponseEveryN = Range(int.Parse(everyN.Text, CultureInfo.InvariantCulture), 0, 1_000_000, "Every Nth");
            e.ChecksumErrorPercent = Percent(crc.Text);
            e.TruncatePercent = Percent(truncate.Text);
            e.WrongIdPercent = Percent(wrongId.Text);
            e.ExceptionCode = FormDialog.Selected<byte>(code);
            e.ExceptionFunction = (byte)Range((int)Hex.ParseNumber(function.Text), 0, 127, "Function");
            e.ExceptionPercent = Percent(exceptionPercent.Text);
            return null;
        };
        if (!f.ShowModal()) return false;
        device.Errors = e;
        return true;
    }

    // ---- device identification ----

    public static bool Identification(SlaveDevice device)
    {
        var id = device.Handler.Identification.Clone();
        var f = new FormDialog($"Device Identification - Slave ID {device.Id}", "device-id");
        f.AddInfo("Answers to function 43/14 Read Device Identification (objects 0 to 6) and function 17 Report Server ID.");
        var vendor = f.AddText("0 Vendor name", id.VendorName);
        var product = f.AddText("1 Product code", id.ProductCode);
        var revision = f.AddText("2 Revision (major.minor)", id.Revision);
        var url = f.AddText("3 Vendor URL", id.VendorUrl);
        var productName = f.AddText("4 Product name", id.ProductName);
        var model = f.AddText("5 Model name", id.ModelName);
        var userApp = f.AddText("6 User application name", id.UserApplicationName);
        f.AddSection("Function 17");
        var serverId = f.AddText("Server ID text", device.Handler.ServerId);
        var run = f.AddCheck("Run indicator ON", device.Handler.RunIndicator);
        if (!f.ShowModal()) return false;
        device.Handler.Identification = new DeviceIdentification
        {
            VendorName = vendor.Text, ProductCode = product.Text, Revision = revision.Text, VendorUrl = url.Text,
            ProductName = productName.Text, ModelName = model.Text, UserApplicationName = userApp.Text,
        };
        device.Handler.ServerId = serverId.Text;
        device.Handler.RunIndicator = run.IsChecked == true;
        return true;
    }

    // ---- value simulation ----

    public static bool ValueSimulation(PollDocument doc, IReadOnlyList<int> cells)
    {
        var first = doc.PeekCell(cells[0]);
        var sim = first?.Simulation?.Clone() ?? new CellSimulation();
        var bits = doc.Definition.IsBits;
        var f = new FormDialog("Value Simulation", "value-simulation");
        f.AddInfo($"The slave changes {cells.Count} selected cell(s) by itself. Values use the cell's display format, so a float cell simulates a float.");
        var modes = Enum.GetValues<SimulationMode>().Select(m => new Choice<SimulationMode>(m, m switch
        {
            SimulationMode.None => "Off",
            SimulationMode.Increment => "Increment (wrap from max to min)",
            SimulationMode.Decrement => "Decrement (wrap from min to max)",
            SimulationMode.Random => "Random between min and max",
            SimulationMode.Sine => "Sine wave between min and max",
            SimulationMode.Ramp => "Ramp (saw tooth) from min to max",
            _ => bits ? "Toggle 0/1" : "Toggle between min and max",
        }));
        var mode = f.AddCombo("Mode", modes, first?.Simulation?.Mode ?? SimulationMode.Increment);
        var min = f.AddText("Min", N(sim.Min), 120);
        var max = f.AddText("Max", N(sim.Max), 120);
        var step = f.AddText("Step", N(sim.Step), 120);
        var interval = f.AddText("Update every [ms]", sim.IntervalMs.ToString(), 120);
        var period = f.AddText("Period [s] (sine, ramp)", N(sim.PeriodSeconds), 120);
        if (bits) { min.IsEnabled = max.IsEnabled = step.IsEnabled = false; }

        CellSimulation? result = null;
        f.Validate = () =>
        {
            var m = FormDialog.Selected<SimulationMode>(mode);
            if (m == SimulationMode.None) { result = null; return null; }
            result = new CellSimulation
            {
                Mode = m, Min = D(min.Text), Max = D(max.Text), Step = D(step.Text),
                IntervalMs = Range(int.Parse(interval.Text, CultureInfo.InvariantCulture), 50, 3_600_000, "Interval"),
                PeriodSeconds = D(period.Text),
            };
            return result.PeriodSeconds <= 0 ? "The period must be above 0." : null;
        };
        if (!f.ShowModal()) return false;
        foreach (var i in cells) doc.Cell(i).Simulation = result?.Clone();
        return true;
    }

    // ---- edit a value ----

    /// <summary>Returns the registers (or 0/1) to store at the cell, or null when cancelled.</summary>
    public static ushort[]? EditValue(PollDocument doc, int index)
    {
        var format = doc.PeekCell(index)?.Format ?? DisplayFormat.Signed;
        var f = new FormDialog("Enter Value", "values");
        var label = doc.Definition.IsBits ? "Value (1/0)" : $"Value ({format.Label()})";
        var current = doc.FormatCell(index);
        var value = f.AddText(label, current.Replace(" ", ""));
        ushort[]? parsed = null;
        f.Validate = () =>
        {
            parsed = doc.Definition.IsBits
                ? [value.Text.Trim().ToLowerInvariant() is "1" or "on" or "true" ? (ushort)1 : (ushort)0]
                : RegisterCodec.Parse(value.Text, format);
            return null;
        };
        value.Loaded += (_, _) => value.Focus();
        return f.ShowModal() ? parsed : null;
    }

    private static int Range(int v, int min, int max, string name) =>
        v >= min && v <= max ? v : throw new FormatException($"{name} must be {min} to {max}.");

    private static double Percent(string s)
    {
        var v = D(s);
        return v is >= 0 and <= 100 ? v : throw new FormatException("Percentages must be 0 to 100.");
    }

    private static string? Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static TextBox Browse(FormDialog f, string label, string? value, string filter)
    {
        var panel = new DockPanel();
        var button = new Button { Content = "...", Width = 28, Margin = new Thickness(4, 0, 0, 0) };
        DockPanel.SetDock(button, Dock.Right);
        var text = new TextBox { Text = value ?? "", Padding = new Thickness(2) };
        panel.Children.Add(button);
        panel.Children.Add(text);
        button.Click += (_, _) =>
        {
            var dlg = new OpenFileDialog { Filter = filter };
            if (dlg.ShowDialog() == true) text.Text = dlg.FileName;
        };
        f.AddRow(label, panel);
        return text;
    }
}
