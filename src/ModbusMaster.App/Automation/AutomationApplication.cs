using ModbusMaster.Ui.Automation;
using ModbusMaster.Ui.Views;
using ModbusMaster.Ui.Dialogs;
using ModbusMaster.Ui;
using System.IO.Ports;
using System.Windows;
using ModbusMaster.App.Views;
using ModbusMaster.Core;

namespace ModbusMaster.App.Automation;

/// <summary>
/// ModbusMaster.Application: connection setup, same members and DISPIDs as the Modbus Poll
/// "IMBApplication" automation interface.
/// </summary>
public sealed class AutomationApplication : DispatchTarget
{
    private static ConnectionSettings Cfg => AppState.Current.Settings;

    private static DispMember Prop(string name, int id, Func<object?> get, Action<object?> set) => new()
    {
        Name = name, DispId = id, Kind = DispKind.Property, Get = _ => get(), Set = (_, v) => set(v),
    };

    protected override IEnumerable<DispMember> CreateMembers() =>
    [
        Prop("BaudRate", 1, () => Cfg.BaudRate, v => Cfg.BaudRate = Range(I(v), 50, 4_000_000, "BaudRate")),
        Prop("DataBits", 2, () => (byte)Cfg.DataBits, v => Cfg.DataBits = I(v) is 7 or 8 ? I(v) : throw new AutomationException("DataBits must be 7 or 8.")),
        Prop("StopBits", 3, () => (short)(Cfg.StopBits == StopBits.Two ? 2 : 1), v => Cfg.StopBits = I(v) == 2 ? StopBits.Two : StopBits.One),
        Prop("Parity", 4, () => (short)(Cfg.Parity switch { Parity.Odd => 1, Parity.Even => 2, _ => 0 }),
            v => Cfg.Parity = I(v) switch { 0 => Parity.None, 1 => Parity.Odd, 2 => Parity.Even, _ => throw new AutomationException("Parity: 0 = None, 1 = Odd, 2 = Even.") }),
        Prop("Mode", 5, () => (short)Cfg.Mode, v => Cfg.Mode = I(v) == 1 ? SerialMode.Ascii : SerialMode.Rtu),
        Prop("ResponseTimeout", 6, () => (short)Math.Min(short.MaxValue, Cfg.ResponseTimeoutMs), v => Cfg.ResponseTimeoutMs = Range(I(v), 50, 100_000, "ResponseTimeout")),
        Prop("DelayBetweenPolls", 7, () => (short)Cfg.DelayBetweenPollsMs, v => Cfg.DelayBetweenPollsMs = Range(I(v), 0, 1000, "DelayBetweenPolls")),
        Prop("IPAddress", 8, () => Cfg.Host, v => Cfg.Host = S(v)),
        Prop("ServerPort", 9, () => Cfg.Port, v => Cfg.Port = Range(I(v), 0, 65535, "ServerPort")),
        Prop("SerialPort", 10, () => (short)PortNumber(Cfg.SerialPort), v => Cfg.SerialPort = $"COM{Range(I(v), 1, 255, "SerialPort")}"),
        Prop("ConnectTimeout", 11, () => (short)Math.Min(short.MaxValue, Cfg.ConnectTimeoutMs), v => Cfg.ConnectTimeoutMs = Range(I(v), 100, 30_000, "ConnectTimeout")),
        Prop("IPVersion", 12, () => (short)Cfg.IpVersion, v => Cfg.IpVersion = I(v) == 6 ? 6 : 4),
        Prop("Connection", 13, () => (short)Cfg.Kind, v => Cfg.Kind = Enum.IsDefined(typeof(ConnectionKind), I(v)) ? (ConnectionKind)I(v)
            : throw new AutomationException("Connection must be 0 to 5.")),
        new DispMember { Name = "OpenConnection", DispId = 14, Get = _ => OpenConnection() },
        new DispMember { Name = "CloseConnection", DispId = 15, Get = _ => { OnUi(() => AppState.Current.Disconnect()); return (short)0; } },
        Prop("RemoveEcho", 16, () => (short)(Cfg.RemoveEcho ? 1 : 0), v => Cfg.RemoveEcho = I(v) != 0),
        new DispMember { Name = "ShowCommunicationTraffic", DispId = 17, ReturnsValue = false, Get = _ => { OnUi(() => TrafficWindow.ShowSingle(AppState.Current)); return null; } },
        new DispMember { Name = "CloseCommunicationTraffic", DispId = 18, ReturnsValue = false, Get = _ => { OnUi(TrafficWindow.CloseSingle); return null; } },
        Prop("CertFilePath", 19, () => Cfg.ClientCertificatePath ?? "", v => Cfg.ClientCertificatePath = S(v).Length == 0 ? null : S(v)),
        Prop("KeyFilePath", 20, () => Cfg.ClientKeyPath ?? "", v => Cfg.ClientKeyPath = S(v).Length == 0 ? null : S(v)),

        // Extensions (not in Modbus Poll): show or hide the main window.
        Prop("Visible", 100, () => Application.Current.MainWindow?.IsVisible == true, v => OnUi(() =>
        {
            if (B(v)) App.ShowMainWindow(); else Application.Current.MainWindow?.Hide();
        })),
    ];

    /// <summary>Returns the OpenConnection error numbers from the automation reference.</summary>
    private static short OpenConnection()
    {
        if (AppState.Current.IsConnected) return 255;
        short result = 0;
        OnUi(() =>
        {
            try
            {
                AppState.Current.Connect();
            }
            catch (ModbusConnectionException ex)
            {
                result = (short)(ex.OpenError == 0 ? 13 : ex.OpenError);
            }
        });
        return result;
    }

    private static int PortNumber(string name) =>
        int.TryParse(new string(name.Where(char.IsDigit).ToArray()), out var n) ? n : 1;

    private static int Range(int value, int min, int max, string name) =>
        value >= min && value <= max ? value : throw new AutomationException($"{name} must be {min} to {max}.");

    internal static void OnUi(Action action)
    {
        var dispatcher = Application.Current.Dispatcher;
        if (dispatcher.CheckAccess()) action();
        else dispatcher.Invoke(action);
    }
}
