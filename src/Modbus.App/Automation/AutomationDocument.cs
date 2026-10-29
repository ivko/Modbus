using Modbus.Ui.Automation;
using Modbus.Ui.Views;
using Modbus.Ui.Dialogs;
using Modbus.Ui;
using System.IO;
using System.Windows;
using Modbus.App.Views;
using Modbus.Core;
using Modbus.Core.Data;
using Modbus.Core.Polling;
using Modbus.Core.Protocol;

namespace Modbus.App.Automation;

/// <summary>
/// ModbusMaster.Document: one data window, same members and DISPIDs as the Modbus Poll "IMbpoll"
/// automation interface. Properties without "Win" use an internal array: reads return the last values
/// read by the window, writes fill the array that the one-shot Write functions send. "Win" properties
/// read and change the data shown in the window itself.
/// </summary>
public sealed class AutomationDocument : DispatchTarget
{
    private const short True = 1;
    private const short False = 0;

    private readonly ushort[] _writeArray = new ushort[Pdu.MaxWriteBits];
    private readonly ConditionalColors _colors = new();
    private ModbusResult _writeResult = ModbusResult.DataUninitialized;
    private PollView _view = null!;

    public AutomationDocument()
    {
        AutomationApplication.OnUi(() =>
        {
            var main = App.Shell;
            var doc = new PollDocument
            {
                Title = $"Mbpoll{main.Views.Count() + 1}",
                Definition = new PollDefinition { Disabled = true },
            };
            _view = main.AddDocument(doc, select: !main.IsVisible);
        });
    }

    private PollDocument Doc => _view.Document;

    /// <summary>Read array: last values read, or the window data for write windows.</summary>
    private ushort[] ReadArray => Doc.Definition.IsWriteWindow ? Doc.WriteValues : Doc.Values;

    public ByteOrder ByteOrder { get; set; } = ByteOrder.BigEndian;

    public override void OnFinalRelease()
    {
        // A hidden window that no script references any more is closed; a shown one stays for the user.
        AutomationApplication.OnUi(() =>
        {
            if (!App.Shell.IsVisible) App.Shell.CloseView(_view);
        });
    }

    protected override IEnumerable<DispMember> CreateMembers()
    {
        var list = new List<DispMember>
        {
            Method("WriteResult", 1, 0, 0, _ => (short)_writeResult),
            Method("ReadResult", 2, 0, 0, _ => (short)Doc.LastResult),
            Method("ReadHoldingRegisters", 3, 4, 4, a => Define(FunctionCode.ReadHoldingRegisters, a)),
            Method("ReadInputRegisters", 4, 4, 4, a => Define(FunctionCode.ReadInputRegisters, a)),
            Method("ReadCoils", 5, 4, 4, a => Define(FunctionCode.ReadCoils, a)),
            Method("ReadDiscreteInputs", 6, 4, 4, a => Define(FunctionCode.ReadDiscreteInputs, a)),
            Void("ShowWindow", 7, 0, 0, _ => { App.ShowMainWindow(); App.Shell.SelectView(_view); }),
            Method("WriteSingleCoil", 8, 2, 2, a => WriteOnce(FunctionCode.WriteSingleCoil, a)),
            Method("WriteSingleRegister", 9, 2, 2, a => WriteOnce(FunctionCode.WriteSingleRegister, a)),
            Method("WriteMultipleCoils", 10, 3, 3, a => WriteOnce(FunctionCode.WriteMultipleCoils, a)),
            Method("WriteMultipleRegisters", 11, 3, 3, a => WriteOnce(FunctionCode.WriteMultipleRegisters, a)),
            new() { Name = "ByteOrder", DispId = 12, Kind = DispKind.Property, Get = _ => (short)ByteOrder,
                    Set = (_, v) => ByteOrder = Enum.IsDefined(typeof(ByteOrder), I(v)) ? (ByteOrder)I(v) : throw new AutomationException("ByteOrder must be 0 to 3.") },
            new() { Name = "ReadWriteDisabled", DispId = 19, Kind = DispKind.Property, Get = _ => Doc.Definition.Disabled,
                    Set = (_, v) => { Doc.Definition.Disabled = B(v); Kick(); } },
            Void("ReadWriteOnce", 20, 0, 0, _ => { Doc.RequestOnce(); Kick(); }),
            Method("GetName", 34, 1, 1, a => Doc.PeekCell(I(a[0]))?.Name ?? ""),
            Void("SetName", 35, 2, 2, a => { Doc.Cell(I(a[0])).Name = S(a[1]); Refresh(); }),
            Method("GetFormat", 36, 1, 1, a => (short)(Doc.PeekCell(I(a[0]))?.Format ?? DisplayFormat.Signed)),
            Void("SetFormat", 37, 2, 2, a => { Doc.Cell(I(a[0])).Format = Format(a[1]); Refresh(); }),
            Void("FormatAll", 38, 1, 1, a => { var f = Format(a[0]); for (var i = 0; i < Doc.Definition.CellCount; i++) Doc.Cell(i).Format = f; Refresh(); }),
            Void("ResizeWindow", 39, 0, 0, _ => { }),
            Void("Rows", 40, 1, 1, a => Doc.RowsMode = Math.Clamp(I(a[0]), 0, 4)),
            Method("WriteSingleCoilWin", 41, 3, 3, a => DefineWrite(FunctionCode.WriteSingleCoil, a[0], a[1], 1, a[2])),
            Method("WriteSingleRegisterWin", 42, 3, 3, a => DefineWrite(FunctionCode.WriteSingleRegister, a[0], a[1], 1, a[2])),
            Method("WriteMultipleCoilsWin", 43, 4, 4, a => DefineWrite(FunctionCode.WriteMultipleCoils, a[0], a[1], I(a[2]), a[3])),
            Method("WriteMultipleRegistersWin", 44, 4, 4, a => DefineWrite(FunctionCode.WriteMultipleRegisters, a[0], a[1], I(a[2]), a[3])),
            Void("ResizeAllColumns", 45, 0, 0, _ => { }),
            Method("GetTxCount", 46, 0, 0, _ => (int)Doc.TxCount),
            Method("GetRxCount", 47, 0, 0, _ => (int)Doc.RxCount),
            new() { Name = "EnableRefresh", DispId = 48, Kind = DispKind.Property, Get = _ => _view.RefreshEnabled, Set = (_, v) => _view.RefreshEnabled = B(v) },
            Method("Save", 49, 1, 1, a => Save(S(a[0]))),
            Method("Open", 50, 1, 1, a => Open(S(a[0]))),
            Method("ColorsSetColors", 51, 4, 4, a => SetColor(I(a[0]), I(a[1]), I(a[2]), I(a[3]))),
            Method("ColorsSetRules", 52, 2, 2, a => { _colors.Rule1 = Rule(a[0]); _colors.Rule2 = Rule(a[1]); return 1; }),
            Method("ColorsSetCompares", 53, 2, 2, a => { _colors.Compare1 = D(a[0]); _colors.Compare2 = D(a[1]); return 1; }),
            Void("ColorsClear", 54, 0, 0, _ =>
            {
                var fresh = new ConditionalColors();
                _colors.Colors = fresh.Colors; _colors.Rule1 = fresh.Rule1; _colors.Rule2 = fresh.Rule2; _colors.Compare1 = 0; _colors.Compare2 = 0;
            }),
            Method("ColorsUpdateCell", 55, 1, 1, a => UpdateCellColors(I(a[0]))),
        };

        // Data properties: (name, dispid, value kind, registers per value, return type)
        list.Add(Bits("Coils", 13, win: false));
        list.Add(Bits("CoilsWin", 25, win: true));
        list.Add(Data("SRegisters", 14, ValueKind.Int16, win: false, v => (short)v));
        list.Add(Data("URegisters", 15, ValueKind.UInt16, win: false, v => (int)v));
        list.Add(Data("Longs", 16, ValueKind.Int32, win: false, v => (int)v));
        list.Add(Data("Floats", 17, ValueKind.Float, win: false, v => (float)v));
        list.Add(Data("Doubles", 18, ValueKind.Double, win: false, v => v));
        list.Add(Data("Ints_32", 21, ValueKind.Int32, win: false, v => v));
        list.Add(Data("UInts_32", 22, ValueKind.UInt32, win: false, v => v));
        list.Add(Data("Ints_64", 23, ValueKind.Int64, win: false, v => v));
        list.Add(Data("UInts_64", 24, ValueKind.UInt64, win: false, v => v));
        list.Add(Data("SRegistersWin", 26, ValueKind.Int16, win: true, v => (short)v));
        list.Add(Data("URegistersWin", 27, ValueKind.UInt16, win: true, v => (int)v));
        list.Add(Data("Ints_32Win", 28, ValueKind.Int32, win: true, v => v));
        list.Add(Data("UInts_32Win", 29, ValueKind.UInt32, win: true, v => v));
        list.Add(Data("Ints_64Win", 30, ValueKind.Int64, win: true, v => v));
        list.Add(Data("UInts_64Win", 31, ValueKind.UInt64, win: true, v => v));
        list.Add(Data("FloatsWin", 32, ValueKind.Float, win: true, v => (float)v));
        list.Add(Data("DoublesWin", 33, ValueKind.Double, win: true, v => v));

        // Extensions (not in Modbus Poll).
        list.Add(Void("Close", 101, 0, 0, _ => App.Shell.CloseView(_view)));
        list.Add(new DispMember { Name = "Title", DispId = 102, Kind = DispKind.Property, Get = _ => Doc.Title, Set = (_, v) => { Doc.Title = S(v); } });
        return list;
    }

    // ---- definitions ----

    private short Define(FunctionCode function, object?[] a)
    {
        try { return Apply(DefineRead(function, a)); }
        catch (AutomationException) { return False; }
    }

    private PollDefinition DefineRead(FunctionCode function, object?[] a)
    {
        return new PollDefinition
        {
            SlaveId = Slave(a[0], allowBroadcast: false), Function = function, Address = Address(a[1]), Quantity = I(a[2]),
            ScanRateMs = I(a[3]), Disabled = false, PlcAddresses = Doc.Definition.PlcAddresses,
        };
    }

    private short DefineWrite(FunctionCode function, object? slave, object? address, int quantity, object? scanRate)
    {
        try
        {
            return Apply(new PollDefinition
            {
                SlaveId = Slave(slave, allowBroadcast: true), Function = function, Address = Address(address), Quantity = quantity,
                ScanRateMs = I(scanRate), Disabled = false,
            });
        }
        catch (AutomationException) { return False; }
    }

    private short Apply(PollDefinition def)
    {
        if (def.Validate().Length > 0) return False;
        Doc.ApplyDefinition(def);
        _view.Rebuild();
        Kick();
        return True;
    }

    /// <summary>One-shot write from the internal array (does not create a window).</summary>
    private short WriteOnce(FunctionCode function, object?[] a)
    {
        var client = AppState.Current.Client;
        if (client is null)
        {
            _writeResult = ModbusResult.PortNotOpen;
            return False;
        }
        byte slave;
        ushort address;
        int quantity;
        try
        {
            slave = Slave(a[0], allowBroadcast: true);
            address = Address(a[1]);
            quantity = a.Length > 2 ? I(a[2]) : 1;
        }
        catch (AutomationException)
        {
            return False;
        }
        if (quantity < 1 || quantity > Pdu.MaxQuantity(function) || address + quantity > 65536) return False;

        var data = _writeArray[..quantity];
        _writeResult = ModbusResult.DataUninitialized;
        Task.Run(() =>
        {
            try
            {
                switch (function)
                {
                    case FunctionCode.WriteSingleCoil: client.WriteSingleCoil(slave, address, data[0] != 0); break;
                    case FunctionCode.WriteSingleRegister: client.WriteSingleRegister(slave, address, data[0]); break;
                    case FunctionCode.WriteMultipleCoils: client.WriteMultipleCoils(slave, address, data.Select(v => v != 0).ToArray()); break;
                    default: client.WriteMultipleRegisters(slave, address, data); break;
                }
                _writeResult = ModbusResult.Success;
            }
            catch (Exception ex) when (ex is ModbusSlaveException or ModbusCommunicationException)
            {
                _writeResult = ModbusResults.FromException(ex);
            }
        });
        return True;
    }

    // ---- data properties ----

    private DispMember Bits(string name, int id, bool win) => new()
    {
        Name = name, DispId = id, Kind = DispKind.IndexedProperty,
        Get = a =>
        {
            var i = I(a[0]);
            var source = win ? ReadArray : ReadOrWriteArray();
            return (short)(i >= 0 && i < source.Length && source[i] != 0 ? 1 : 0);
        },
        Set = (a, v) =>
        {
            var i = I(a[0]);
            var bit = (ushort)(I(v) != 0 ? 1 : 0);
            if (win) { Doc.SetWriteValue(i, bit); Refresh(); }
            else if (i >= 0 && i < _writeArray.Length) _writeArray[i] = bit;
            else throw new AutomationException($"Index must be 0 to {_writeArray.Length - 1}.");
        },
    };

    private DispMember Data(string name, int id, ValueKind kind, bool win, Func<double, object> box) => new()
    {
        Name = name, DispId = id, Kind = DispKind.IndexedProperty,
        Get = a =>
        {
            var i = I(a[0]);
            var source = win ? ReadArray : ReadOrWriteArray();
            var n = RegisterCodec.RegisterCount(kind);
            if (i < 0 || i + n > source.Length) return box(0);
            return box(RegisterCodec.Decode(source.AsSpan(i, n), kind, win ? WinOrder(i, kind) : ByteOrder));
        },
        Set = (a, v) =>
        {
            var i = I(a[0]);
            var regs = RegisterCodec.Encode(D(v), kind, win ? WinOrder(i, kind) : ByteOrder);
            if (win)
            {
                Doc.SetWriteValues(i, regs);
                Refresh();
                return;
            }
            if (i < 0 || i + regs.Length > _writeArray.Length) throw new AutomationException($"Index must be 0 to {_writeArray.Length - regs.Length}.");
            regs.CopyTo(_writeArray, i);
        },
    };

    /// <summary>
    /// Non-Win getters return the read array once the window has read data; before that they return what
    /// the script stored in the write array, so a value can be set and read back.
    /// </summary>
    private ushort[] ReadOrWriteArray() => Doc.LastResult == ModbusResult.DataUninitialized ? _writeArray : ReadArray;

    /// <summary>Win properties follow the cell's display format byte order (big-endian otherwise).</summary>
    private ByteOrder WinOrder(int index, ValueKind kind)
    {
        var f = Doc.PeekCell(index)?.Format;
        return f is { } format && format.Kind() == kind ? format.Order() : ByteOrder.BigEndian;
    }

    // ---- colors, files ----

    private int SetColor(int item, int r, int g, int b)
    {
        if (item is < 0 or > 5) return 0;
        _colors.Colors[item] = (Math.Clamp(r, 0, 255) << 16) | (Math.Clamp(g, 0, 255) << 8) | Math.Clamp(b, 0, 255);
        return 1;
    }

    private int UpdateCellColors(int index)
    {
        if (index < 0 || index >= Doc.Definition.CellCount) return 0;
        var clean = new ConditionalColors();
        var isDefault = _colors.Rule1 == ColorRule.NotUsed && _colors.Rule2 == ColorRule.NotUsed && _colors.Colors.SequenceEqual(clean.Colors);
        Doc.Cell(index).Colors = isDefault ? null : _colors.Clone();
        Refresh();
        return 1;
    }

    private int Save(string path)
    {
        try
        {
            new Workspace { Windows = [Doc.CloneDefinitionOnly()] }.Save(path);
            return 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return 0;
        }
    }

    private int Open(string path)
    {
        try
        {
            var w = Workspace.Load(path).Windows.FirstOrDefault();
            if (w is null) return 0;
            Doc.Title = w.Title;
            Doc.Cells = w.Cells;
            Doc.RowsMode = w.RowsMode;
            Doc.ApplyDefinition(w.Definition);
            _view.Rebuild();
            Kick();
            return 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException or ArgumentException)
        {
            return 0;
        }
    }

    // ---- helpers ----

    private void Kick() => AppState.Current.Scheduler.Kick(Doc);

    private void Refresh() => _view.Invalidate();

    private static DisplayFormat Format(object? v)
    {
        var id = I(v);
        return DisplayFormats.IsDefined(id) ? (DisplayFormat)id : throw new AutomationException($"Unknown format {id}. See the format table in the manual.");
    }

    private static ColorRule Rule(object? v) =>
        Enum.IsDefined(typeof(ColorRule), I(v)) ? (ColorRule)I(v) : throw new AutomationException("Rule must be 0 to 6.");

    private static byte Slave(object? v, bool allowBroadcast)
    {
        var id = I(v);
        if (id < (allowBroadcast ? 0 : 1) || id > 255) throw new AutomationException("SlaveID must be 1 to 255.");
        return (byte)id;
    }

    private static ushort Address(object? v)
    {
        var a = I(v);
        if (a is < 0 or > 65535) throw new AutomationException("Address must be 0 to 65535.");
        return (ushort)a;
    }

    private static DispMember Method(string name, int id, int min, int max, Func<object?[], object?> body) =>
        new() { Name = name, DispId = id, Kind = DispKind.Method, MinArgs = min, MaxArgs = max, Get = body };

    private static DispMember Void(string name, int id, int min, int max, Action<object?[]> body) =>
        new() { Name = name, DispId = id, Kind = DispKind.Method, MinArgs = min, MaxArgs = max, ReturnsValue = false, Get = a => { body(a); return null; } };
}
