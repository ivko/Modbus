using Modbus.Ui.Automation;
using Modbus.Ui.Views;
using Modbus.Ui.Dialogs;
using Modbus.Ui;
using System.IO.Ports;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Modbus.Core;
using Modbus.Core.Channels;

namespace Modbus.App.Dialogs;

/// <summary>Connection setup (F3).</summary>
public static class ConnectionDialog
{
    public static ConnectionSettings? Show(ConnectionSettings current)
    {
        var s = current.Clone();
        var f = new FormDialog("Connection Setup", "connection");

        var kind = f.AddCombo("Connection", new[]
        {
            new Choice<ConnectionKind>(ConnectionKind.Serial, "Serial port"),
            new Choice<ConnectionKind>(ConnectionKind.Tcp, "Modbus TCP/IP"),
            new Choice<ConnectionKind>(ConnectionKind.Udp, "Modbus UDP/IP"),
            new Choice<ConnectionKind>(ConnectionKind.RtuAsciiOverTcp, "Modbus RTU/ASCII over TCP/IP"),
            new Choice<ConnectionKind>(ConnectionKind.RtuAsciiOverUdp, "Modbus RTU/ASCII over UDP/IP"),
            new Choice<ConnectionKind>(ConnectionKind.TcpSecurity, "Modbus/TCP Security (TLS)"),
        }, s.Kind);

        f.AddSection("Serial settings");
        var ports = SerialChannel.AvailablePorts().Select(p => new Choice<string>(p, p)).ToList();
        if (!ports.Any(p => p.Value.Equals(s.SerialPort, StringComparison.OrdinalIgnoreCase)))
            ports.Insert(0, new Choice<string>(s.SerialPort, s.SerialPort));
        var port = f.AddCombo("Serial port", ports, s.SerialPort, editable: true);
        var baud = f.AddCombo("Baud rate", ConnectionSettings.StandardBaudRates.Select(b => new Choice<int>(b, b.ToString())), s.BaudRate, editable: true);
        var dataBits = f.AddCombo("Data bits", new[] { new Choice<int>(7, "7"), new Choice<int>(8, "8") }, s.DataBits);
        var parity = f.AddCombo("Parity", new[]
        {
            new Choice<Parity>(Parity.None, "None"), new Choice<Parity>(Parity.Even, "Even"), new Choice<Parity>(Parity.Odd, "Odd"),
            new Choice<Parity>(Parity.Mark, "Mark"), new Choice<Parity>(Parity.Space, "Space"),
        }, s.Parity);
        var stopBits = f.AddCombo("Stop bits", new[] { new Choice<StopBits>(StopBits.One, "1"), new Choice<StopBits>(StopBits.Two, "2") }, s.StopBits);
        var mode = f.AddCombo("Mode", new[] { new Choice<SerialMode>(SerialMode.Rtu, "RTU"), new Choice<SerialMode>(SerialMode.Ascii, "ASCII") }, s.Mode);
        var echo = f.AddCheck("Remove echo (converter repeats the sent bytes)", s.RemoveEcho);
        var dtr = f.AddCheck("DTR on", s.DtrEnable);
        var rts = f.AddCheck("RTS on", s.RtsEnable);

        f.AddSection("Remote server");
        var host = f.AddText("IP address / host", s.Host);
        var tcpPort = f.AddText("Server port", s.Port.ToString(), 90);
        var ipv6 = f.AddCheck("IPv6", s.IpVersion == 6);
        var connectTimeout = f.AddText("Connect timeout [ms]", s.ConnectTimeoutMs.ToString(), 90);

        f.AddSection("Modbus/TCP Security");
        var cert = AddBrowse(f, "Client certificate", s.ClientCertificatePath, "Certificates (*.pfx;*.p12;*.pem;*.crt)|*.pfx;*.p12;*.pem;*.crt|All files|*.*");
        var key = AddBrowse(f, "Private key (.pem, if not in the certificate)", s.ClientKeyPath, "Keys (*.pem;*.key)|*.pem;*.key|All files|*.*");
        var certPassword = f.AddPassword("Certificate or key password", s.ClientCertificatePassword);
        var ca = AddBrowse(f, "Trusted CA (.pem/.cer)", s.TrustedCaPath, "Certificates (*.pem;*.cer;*.crt)|*.pem;*.cer;*.crt|All files|*.*");
        var serverName = f.AddText("Server name (if not host)", s.TlsServerName ?? "");
        var untrusted = f.AddCheck("Accept any server certificate (testing only)", s.AllowUntrustedServer);

        f.AddSection("Timing");
        var responseTimeout = f.AddText("Response timeout [ms]", s.ResponseTimeoutMs.ToString(), 90);
        var delay = f.AddText("Delay between polls [ms]", s.DelayBetweenPollsMs.ToString(), 90);

        var serialControls = new FrameworkElement[] { port, baud, dataBits, parity, stopBits, echo, dtr, rts };
        var netControls = new FrameworkElement[] { host, tcpPort, ipv6, connectTimeout };
        var tlsControls = new FrameworkElement[] { cert, key, certPassword, ca, serverName, untrusted };

        void Update()
        {
            var k = FormDialog.Selected<ConnectionKind>(kind);
            foreach (var c in serialControls) c.IsEnabled = k == ConnectionKind.Serial;
            foreach (var c in netControls) c.IsEnabled = k != ConnectionKind.Serial;
            foreach (var c in tlsControls) c.IsEnabled = k == ConnectionKind.TcpSecurity;
            mode.IsEnabled = k is ConnectionKind.Serial or ConnectionKind.RtuAsciiOverTcp or ConnectionKind.RtuAsciiOverUdp;
        }

        var previousKind = s.Kind;
        kind.SelectionChanged += (_, _) =>
        {
            var k = FormDialog.Selected<ConnectionKind>(kind);
            // Suggest the standard port when switching between plain and secure Modbus/TCP.
            if (k == ConnectionKind.TcpSecurity && tcpPort.Text == "502") tcpPort.Text = "802";
            if (previousKind == ConnectionKind.TcpSecurity && k != ConnectionKind.TcpSecurity && tcpPort.Text == "802") tcpPort.Text = "502";
            previousKind = k;
            Update();
        };
        Update();

        f.Validate = () =>
        {
            s.Kind = FormDialog.Selected<ConnectionKind>(kind);
            s.SerialPort = (port.SelectedItem as Choice<string>)?.Value ?? port.Text.Trim().ToUpperInvariant();
            s.BaudRate = baud.SelectedItem is Choice<int> b ? b.Value : int.Parse(baud.Text);
            s.DataBits = FormDialog.Selected<int>(dataBits);
            s.Parity = FormDialog.Selected<Parity>(parity);
            s.StopBits = FormDialog.Selected<StopBits>(stopBits);
            s.Mode = FormDialog.Selected<SerialMode>(mode);
            s.RemoveEcho = echo.IsChecked == true;
            s.DtrEnable = dtr.IsChecked == true;
            s.RtsEnable = rts.IsChecked == true;
            s.Host = host.Text.Trim();
            s.Port = int.Parse(tcpPort.Text);
            s.IpVersion = ipv6.IsChecked == true ? 6 : 4;
            s.ConnectTimeoutMs = int.Parse(connectTimeout.Text);
            s.ResponseTimeoutMs = int.Parse(responseTimeout.Text);
            s.DelayBetweenPollsMs = int.Parse(delay.Text);
            s.ClientCertificatePath = Blank(cert.Text);
            s.ClientCertificatePassword = Blank(certPassword.Password);
            s.ClientKeyPath = Blank(key.Text);
            s.TrustedCaPath = Blank(ca.Text);
            s.TlsServerName = Blank(serverName.Text);
            s.AllowUntrustedServer = untrusted.IsChecked == true;

            if (s.Kind == ConnectionKind.Serial && string.IsNullOrWhiteSpace(s.SerialPort)) return "Select a serial port.";
            if (s.Kind != ConnectionKind.Serial && string.IsNullOrWhiteSpace(s.Host)) return "Enter the IP address or host name.";
            if (s.Port is < 0 or > 65535) return "The server port must be 0 to 65535.";
            if (s.BaudRate is < 50 or > 4_000_000) return "The baud rate is out of range.";
            if (s.ResponseTimeoutMs is < 50 or > 100_000) return "Response timeout must be 50 to 100000 ms.";
            if (s.DelayBetweenPollsMs is < 0 or > 1000) return "Delay between polls must be 0 to 1000 ms.";
            if (s.ConnectTimeoutMs is < 100 or > 30_000) return "Connect timeout must be 100 to 30000 ms.";
            return null;
        };

        return f.ShowModal() ? s : null;
    }

    private static string? Blank(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static TextBox AddBrowse(FormDialog f, string label, string? value, string filter)
    {
        var panel = new DockPanel();
        var browse = new Button { Content = "...", Width = 28, Margin = new Thickness(4, 0, 0, 0) };
        DockPanel.SetDock(browse, Dock.Right);
        var text = new TextBox { Text = value ?? "", Padding = new Thickness(2) };
        panel.Children.Add(browse);
        panel.Children.Add(text);
        browse.Click += (_, _) =>
        {
            var dlg = new OpenFileDialog { Filter = filter };
            if (dlg.ShowDialog() == true) text.Text = dlg.FileName;
        };
        f.AddRow(label, panel);
        panel.IsEnabledChanged += (_, _) => text.IsEnabled = panel.IsEnabled;
        return text;
    }
}
