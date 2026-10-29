using System.IO.Ports;
using System.Windows;
using Modbus.Core;
using Modbus.Core.Data;
using Modbus.Core.Polling;
using Modbus.Core.Protocol;
using Modbus.Core.Slave;
using Modbus.Ui.Automation;
using Modbus.Ui.Views;

namespace Modbus.Slave.Automation;

internal static class Ui
{
    public static void Run(Action action)
    {
        var d = Application.Current.Dispatcher;
        if (d.CheckAccess()) action(); else d.Invoke(action);
    }

    public static T Run<T>(Func<T> f)
    {
        var d = Application.Current.Dispatcher;
        return d.CheckAccess() ? f() : d.Invoke(f);
    }
}

/// <summary>ModbusSlave.Application: the same members as the Modbus Slave "Mbslave.Application" automation object.</summary>
public sealed class SlaveAutomationApplication : DispatchTarget
{
    private static ConnectionSettings C => SlaveState.Current.Settings.Connection;
    private static ServerSettings Srv => SlaveState.Current.Settings;

    private static DispMember Prop(string name, int id, Func<object?> get, Action<object?> set) =>
        new() { Name = name, DispId = id, Kind = DispKind.Property, Get = _ => get(), Set = (_, v) => set(v) };

    protected override IEnumerable<DispMember> CreateMembers() =>
    [
        Prop("Connection", 1, () => (short)C.Kind, v => C.Kind = Enum.IsDefined(typeof(ConnectionKind), I(v)) ? (ConnectionKind)I(v)
            : throw new AutomationException("Connection must be 0 to 5.")),
        Prop("BaudRate", 2, () => C.BaudRate, v => C.BaudRate = I(v)),
        Prop("DataBits", 3, () => (short)C.DataBits, v => C.DataBits = I(v) is 7 or 8 ? I(v) : throw new AutomationException("DataBits must be 7 or 8.")),
        Prop("Parity", 4, () => (short)(C.Parity switch { Parity.Odd => 1, Parity.Even => 2, _ => 0 }),
            v => C.Parity = I(v) switch { 0 => Parity.None, 1 => Parity.Odd, 2 => Parity.Even, _ => throw new AutomationException("Parity: 0 None, 1 Odd, 2 Even.") }),
        Prop("StopBits", 5, () => (short)(C.StopBits == StopBits.Two ? 2 : 1), v => C.StopBits = I(v) == 2 ? StopBits.Two : StopBits.One),
        Prop("SerialPort", 6, () => (short)(int.TryParse(new string(C.SerialPort.Where(char.IsDigit).ToArray()), out var n) ? n : 1),
            v => C.SerialPort = $"COM{I(v)}"),
        Prop("Mode", 7, () => (short)C.Mode, v => C.Mode = I(v) == 1 ? SerialMode.Ascii : SerialMode.Rtu),
        Prop("ServerPort", 8, () => C.Port, v => C.Port = I(v) is >= 0 and <= 65535 ? I(v) : throw new AutomationException("ServerPort must be 0 to 65535.")),
        Prop("IPVersion", 9, () => (short)C.IpVersion, v => C.IpVersion = I(v) == 6 ? 6 : 4),
        Prop("IPAddress", 10, () => C.Host, v => C.Host = S(v)),
        Prop("AnyAddress", 11, () => (short)(Srv.AnyAddress ? 1 : 0), v => Srv.AnyAddress = I(v) != 0),
        Prop("IgnoreUnitID", 12, () => (short)(Srv.IgnoreUnitId ? 1 : 0), v => { Srv.IgnoreUnitId = I(v) != 0; SlaveState.Current.ApplyEngineOptions(); }),
        new DispMember { Name = "OpenConnection", DispId = 13, Get = _ => OpenConnection() },
        new DispMember { Name = "CloseConnection", DispId = 14, Get = _ => { Ui.Run(SlaveState.Current.Stop); return (short)0; } },
        new DispMember { Name = "ShowCommunicationTraffic", DispId = 15, ReturnsValue = false, Get = _ => { Ui.Run(() => TrafficWindow.ShowSingle(SlaveState.Current.Traffic)); return null; } },
        new DispMember { Name = "CloseCommunicationTraffic", DispId = 16, ReturnsValue = false, Get = _ => { Ui.Run(TrafficWindow.CloseSingle); return null; } },

        // Extensions (not in Modbus Slave).
        Prop("StrictAddressing", 100, () => Srv.StrictAddressing, v => { Srv.StrictAddressing = B(v); SlaveState.Current.ApplyEngineOptions(); }),
        Prop("Visible", 101, () => Application.Current.MainWindow?.IsVisible == true, v => Ui.Run(() =>
        {
            if (B(v)) App.ShowMainWindow(); else Application.Current.MainWindow?.Hide();
        })),
        Prop("CertFilePath", 102, () => C.ClientCertificatePath ?? "", v => C.ClientCertificatePath = S(v).Length == 0 ? null : S(v)),
        Prop("KeyFilePath", 103, () => C.ClientKeyPath ?? "", v => C.ClientKeyPath = S(v).Length == 0 ? null : S(v)),
    ];

    private static short OpenConnection()
    {
        if (SlaveState.Current.IsRunning) return 255;
        return Ui.Run(() =>
        {
            try
            {
                SlaveState.Current.Start();
                return (short)0;
            }
            catch (ModbusConnectionException ex)
            {
                return (short)(ex.OpenError == 0 ? 13 : ex.OpenError);
            }
        });
    }
}

/// <summary>
/// ModbusSlave.Document: one slave window, the same members as the Modbus Slave "Mbslave.Document" object.
/// Data properties read and change the slave memory shown in the window; the index counts from the window's first address.
/// </summary>
public sealed class SlaveAutomationDocument : DispatchTarget
{
    private PollView _view = null!;

    public SlaveAutomationDocument()
    {
        Ui.Run(() => _view = App.Shell.NewWindow(select: !App.Shell.IsVisible));
    }

    private PollDocument Doc => _view.Document;
    private SlaveEngine Engine => SlaveState.Current.Engine;
    private SlaveDevice Device => Engine.Device(Doc.Definition.SlaveId);

    public ByteOrder ByteOrder { get; set; } = ByteOrder.BigEndian;

    public override void OnFinalRelease() => Ui.Run(() =>
    {
        if (!App.Shell.IsVisible) App.Shell.CloseView(_view);
    });

    protected override IEnumerable<DispMember> CreateMembers()
    {
        var list = new List<DispMember>
        {
            Method("SetupCoils", 1, 3, a => Setup(FunctionCode.ReadCoils, a)),
            Method("SetupDiscreteInputs", 2, 3, a => Setup(FunctionCode.ReadDiscreteInputs, a)),
            Method("SetupHoldingRegisters", 3, 3, a => Setup(FunctionCode.ReadHoldingRegisters, a)),
            Method("SetupInputRegisters", 4, 3, a => Setup(FunctionCode.ReadInputRegisters, a)),
            Void("ShowWindow", 5, 0, _ => { App.ShowMainWindow(); App.Shell.SelectView(_view); }),
            Method("GetName", 6, 1, a => Doc.PeekCell(I(a[0]))?.Name ?? ""),
            Void("SetName", 7, 2, a => { Doc.Cell(I(a[0])).Name = S(a[1]); _view.Invalidate(); }),
            AutoSim("AutoIncrement", 8, SimulationMode.Increment),
            AutoSim("AutoDecrement", 9, SimulationMode.Decrement),
            Void("FormatAll", 10, 1, a => { var f = Format(a[0]); for (var i = 0; i < Doc.Definition.CellCount; i++) Doc.Cell(i).Format = f; _view.Invalidate(); }),
            Method("GetFormat", 11, 1, a => (short)(Doc.PeekCell(I(a[0]))?.Format ?? DisplayFormat.Signed)),
            Void("SetFormat", 12, 2, a => { Doc.Cell(I(a[0])).Format = Format(a[1]); _view.Invalidate(); }),
            Void("ResizeWindow", 13, 0, _ => { }),
            Void("ResizeAllColumns", 14, 0, _ => { }),
            Void("Rows", 15, 1, a => Doc.RowsMode = Math.Clamp(I(a[0]), 0, 4)),
            new() { Name = "EnableRefresh", DispId = 16, Kind = DispKind.Property, Get = _ => _view.RefreshEnabled, Set = (_, v) => _view.RefreshEnabled = B(v) },
            new() { Name = "ReadWriteDisabled", DispId = 17, Kind = DispKind.Property, Get = _ => Doc.Definition.Disabled, Set = (_, v) => { Doc.Definition.Disabled = B(v); _view.Invalidate(); } },
            new() { Name = "ByteOrder", DispId = 18, Kind = DispKind.Property, Get = _ => (short)ByteOrder,
                    Set = (_, v) => ByteOrder = Enum.IsDefined(typeof(ByteOrder), I(v)) ? (ByteOrder)I(v) : throw new AutomationException("ByteOrder must be 0 to 3.") },
            Bits("Coils", 19),
            Data("SRegisters", 20, ValueKind.Int16, v => (short)v),
            Data("URegisters", 21, ValueKind.UInt16, v => (int)v),
            Data("Ints_32", 22, ValueKind.Int32, v => v),
            Data("UInts_32", 23, ValueKind.UInt32, v => v),
            Data("Ints_64", 24, ValueKind.Int64, v => v),
            Data("UInts_64", 25, ValueKind.UInt64, v => v),
            Data("Floats", 26, ValueKind.Float, v => (float)v),
            Data("Doubles", 27, ValueKind.Double, v => v),

            // Extensions (not in Modbus Slave): error simulation for this window's slave ID, counters, title.
            Prop("ErrorSimulation", 100, () => Device.Errors.Enabled, v => Device.Errors.Enabled = B(v)),
            Prop("ResponseDelay", 101, () => Device.Errors.ResponseDelayMs, v => Device.Errors.ResponseDelayMs = Math.Max(0, I(v))),
            Prop("NoResponsePercent", 102, () => Device.Errors.NoResponsePercent, v => Device.Errors.NoResponsePercent = Pct(v)),
            Prop("ChecksumErrorPercent", 103, () => Device.Errors.ChecksumErrorPercent, v => Device.Errors.ChecksumErrorPercent = Pct(v)),
            Prop("ExceptionCode", 104, () => (short)Device.Errors.ExceptionCode, v => Device.Errors.ExceptionCode = (byte)Math.Clamp(I(v), 0, 255)),
            Method("GetRequestCount", 105, 0, _ => (int)Doc.TxCount),
            Prop("Title", 106, () => Doc.Title, v => Doc.Title = S(v)),
            Void("Close", 107, 0, _ => App.Shell.CloseView(_view)),
        };
        return list;
    }

    private short Setup(FunctionCode table, object?[] a)
    {
        var id = I(a[0]);
        var address = I(a[1]);
        var quantity = I(a[2]);
        if (id is < 0 or > 255 || address is < 0 or > 65535 || quantity is < 1 or > 10000 || address + quantity > 65536) return 0;
        Doc.Definition = new PollDefinition
        {
            SlaveId = (byte)id, Function = table, Address = (ushort)address, Quantity = quantity, PlcAddresses = Doc.Definition.PlcAddresses,
        };
        Engine.WindowChanged(Doc);
        _view.Rebuild();
        return 1;
    }

    private DispMember AutoSim(string name, int id, SimulationMode mode) => new()
    {
        Name = name, DispId = id, Kind = DispKind.IndexedProperty,
        Get = a => Doc.PeekCell(I(a[0]))?.Simulation?.Mode == mode,
        Set = (a, v) =>
        {
            var i = I(a[0]);
            Doc.Cell(i).Simulation = B(v) ? MainWindow.AutoSimulation(Doc, i, mode) : null;
            Engine.WindowChanged(Doc);
        },
    };

    private DispMember Bits(string name, int id) => new()
    {
        Name = name, DispId = id, Kind = DispKind.IndexedProperty,
        Get = a =>
        {
            var i = I(a[0]);
            var values = Engine.Read(Doc);
            return (short)(i >= 0 && i < values.Length && values[i] != 0 ? 1 : 0);
        },
        Set = (a, v) => Engine.Write(Doc, Index(a[0], 1), [(ushort)(I(v) != 0 ? 1 : 0)]),
    };

    private DispMember Data(string name, int id, ValueKind kind, Func<double, object> box) => new()
    {
        Name = name, DispId = id, Kind = DispKind.IndexedProperty,
        Get = a =>
        {
            var i = I(a[0]);
            var n = RegisterCodec.RegisterCount(kind);
            var values = Engine.Read(Doc);
            if (i < 0 || i + n > values.Length) return box(0);
            return box(RegisterCodec.Decode(values.AsSpan(i, n), kind, ByteOrder));
        },
        Set = (a, v) =>
        {
            var regs = RegisterCodec.Encode(D(v), kind, ByteOrder);
            Engine.Write(Doc, Index(a[0], regs.Length), regs);
        },
    };

    private int Index(object? v, int registers)
    {
        var i = I(v);
        if (i < 0 || i + registers > Doc.Definition.Quantity)
            throw new AutomationException($"Index must be 0 to {Doc.Definition.Quantity - registers} for this window.");
        return i;
    }

    private static DisplayFormat Format(object? v)
    {
        var id = I(v);
        return DisplayFormats.IsDefined(id) ? (DisplayFormat)id : throw new AutomationException($"Unknown format {id}.");
    }

    private static double Pct(object? v) => Math.Clamp(D(v), 0, 100);

    private static DispMember Prop(string name, int id, Func<object?> get, Action<object?> set) =>
        new() { Name = name, DispId = id, Kind = DispKind.Property, Get = _ => get(), Set = (_, v) => set(v) };

    private static DispMember Method(string name, int id, int args, Func<object?[], object?> body) =>
        new() { Name = name, DispId = id, Kind = DispKind.Method, MinArgs = args, MaxArgs = args, Get = body };

    private static DispMember Void(string name, int id, int args, Action<object?[]> body) =>
        new() { Name = name, DispId = id, Kind = DispKind.Method, MinArgs = args, MaxArgs = args, ReturnsValue = false, Get = a => { body(a); return null; } };
}
