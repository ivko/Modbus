using System.Buffers.Binary;
using System.Text;
using Modbus.Core.Protocol;

namespace Modbus.Core.Server;

/// <summary>Memory of a simulated slave: 65536 of each table.</summary>
public sealed class ModbusDataStore
{
    public bool[] Coils { get; } = new bool[65536];
    public bool[] DiscreteInputs { get; } = new bool[65536];
    public ushort[] HoldingRegisters { get; } = new ushort[65536];
    public ushort[] InputRegisters { get; } = new ushort[65536];
    public object SyncRoot { get; } = new();
}

/// <summary>Objects returned by function 43/14 Read Device Identification (IDs 0 to 6).</summary>
public sealed class DeviceIdentification
{
    public string VendorName { get; set; } = "Modbus Master";
    public string ProductCode { get; set; } = "MM-SLAVE";
    public string Revision { get; set; } = "1.0";
    public string VendorUrl { get; set; } = "";
    public string ProductName { get; set; } = "Modbus Slave simulator";
    public string ModelName { get; set; } = "";
    public string UserApplicationName { get; set; } = "";

    public string[] Objects() => [VendorName, ProductCode, Revision, VendorUrl, ProductName, ModelName, UserApplicationName];

    public DeviceIdentification Clone() => (DeviceIdentification)MemberwiseClone();
}

/// <summary>Serial line diagnostic counters reported by function 08 sub-functions 11 to 18.</summary>
public sealed class DiagnosticCounters
{
    private long _busMessages, _busCommErrors, _busExceptions, _serverMessages, _serverNoResponse;
    private int _eventCount;

    public long BusMessages => Interlocked.Read(ref _busMessages);
    public long BusCommunicationErrors => Interlocked.Read(ref _busCommErrors);
    public long BusExceptionErrors => Interlocked.Read(ref _busExceptions);
    public long ServerMessages => Interlocked.Read(ref _serverMessages);
    public long ServerNoResponses => Interlocked.Read(ref _serverNoResponse);
    public int EventCount => Volatile.Read(ref _eventCount);

    internal void BusMessage() => Interlocked.Increment(ref _busMessages);
    internal void CommunicationError() => Interlocked.Increment(ref _busCommErrors);
    internal void Exception() => Interlocked.Increment(ref _busExceptions);
    internal void ServerMessage() => Interlocked.Increment(ref _serverMessages);
    public void NoResponse() => Interlocked.Increment(ref _serverNoResponse);
    internal void Event() => Interlocked.Increment(ref _eventCount);

    public void Clear()
    {
        Interlocked.Exchange(ref _busMessages, 0);
        Interlocked.Exchange(ref _busCommErrors, 0);
        Interlocked.Exchange(ref _busExceptions, 0);
        Interlocked.Exchange(ref _serverMessages, 0);
        Interlocked.Exchange(ref _serverNoResponse, 0);
        Interlocked.Exchange(ref _eventCount, 0);
    }
}

/// <summary>Answers request PDUs from one <see cref="ModbusDataStore"/>.</summary>
public sealed class ModbusRequestHandler : IServerHandler
{
    private readonly ModbusDataStore _store;

    public ModbusRequestHandler(ModbusDataStore store) => _store = store;

    public ModbusDataStore Store => _store;

    public string ServerId { get; set; } = "Modbus Master simulator";
    public bool RunIndicator { get; set; } = true;
    public DeviceIdentification Identification { get; set; } = new();
    public DiagnosticCounters Counters { get; } = new();

    /// <summary>Addresses at or above this return Illegal Data Address.</summary>
    public int AddressLimit { get; set; } = 65536;

    /// <summary>
    /// Optional address check per table (function 1 = coils, 2 = discrete inputs, 3 = holding, 4 = input registers).
    /// Returns false for ranges that do not exist; the request then gets Illegal Data Address.
    /// </summary>
    public Func<FunctionCode, int, int, bool>? IsValidRange { get; set; }

    /// <summary>Set by diagnostics sub-function 04; cleared by sub-function 01 (restart communications).</summary>
    public bool ListenOnly { get; private set; }

    ServerReply IServerHandler.Handle(byte unit, byte[] pdu)
    {
        var response = Handle(pdu);
        return response is null ? ServerReply.NoResponse : new ServerReply(response);
    }

    void IServerHandler.OnFrame(bool valid)
    {
        Counters.BusMessage();
        if (!valid) Counters.CommunicationError();
    }

    /// <summary>Returns the response PDU, or null when the slave must not answer (listen-only mode).</summary>
    public byte[]? Handle(ReadOnlySpan<byte> pdu)
    {
        if (pdu.Length == 0) return Exception(0, 1);
        var f = pdu[0];

        // In listen-only mode only "restart communications" is executed, and it is not answered either.
        if (ListenOnly)
        {
            if (f == 8 && pdu.Length == 5 && U16(pdu, 1) == 1) ListenOnly = false;
            Counters.NoResponse();
            return null;
        }

        Counters.ServerMessage();
        byte[]? response;
        try
        {
            lock (_store.SyncRoot)
            {
                response = f switch
                {
                    1 => ReadBits(pdu, FunctionCode.ReadCoils, _store.Coils),
                    2 => ReadBits(pdu, FunctionCode.ReadDiscreteInputs, _store.DiscreteInputs),
                    3 => ReadRegisters(pdu, FunctionCode.ReadHoldingRegisters, _store.HoldingRegisters),
                    4 => ReadRegisters(pdu, FunctionCode.ReadInputRegisters, _store.InputRegisters),
                    5 => WriteSingleCoil(pdu),
                    6 => WriteSingleRegister(pdu),
                    8 => Diagnostics(pdu),
                    11 => [11, 0, 0, (byte)(Counters.EventCount >> 8), (byte)Counters.EventCount],
                    15 => WriteMultipleCoils(pdu),
                    16 => WriteMultipleRegisters(pdu),
                    17 => ReportServerId(),
                    22 => MaskWrite(pdu),
                    23 => ReadWrite(pdu),
                    43 => ReadDeviceIdentification(pdu),
                    _ => Exception(f, 1),
                };
            }
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            response = Exception(f, 3);
        }

        if (response is null)
        {
            Counters.NoResponse();
            return null;
        }
        if ((response[0] & 0x80) != 0) Counters.Exception();
        else if (f is not (11 or 8)) Counters.Event();
        return response;
    }

    private static ushort U16(ReadOnlySpan<byte> s, int offset) => BinaryPrimitives.ReadUInt16BigEndian(s[offset..]);

    public static byte[] Exception(byte function, byte code) => [(byte)(function | 0x80), code];

    private bool OutOfRange(FunctionCode table, int address, int count) =>
        address + count > AddressLimit || (IsValidRange is { } valid && !valid(table, address, count));

    private byte[] ReadBits(ReadOnlySpan<byte> pdu, FunctionCode table, bool[] data)
    {
        if (pdu.Length != 5) return Exception(pdu[0], 3);
        int address = U16(pdu, 1), count = U16(pdu, 3);
        if (count < 1 || count > 2000) return Exception(pdu[0], 3);
        if (OutOfRange(table, address, count)) return Exception(pdu[0], 2);
        var bytes = (count + 7) / 8;
        var r = new byte[2 + bytes];
        r[0] = pdu[0];
        r[1] = (byte)bytes;
        for (var i = 0; i < count; i++)
            if (data[address + i]) r[2 + i / 8] |= (byte)(1 << (i % 8));
        return r;
    }

    private byte[] ReadRegisters(ReadOnlySpan<byte> pdu, FunctionCode table, ushort[] data)
    {
        if (pdu.Length != 5) return Exception(pdu[0], 3);
        int address = U16(pdu, 1), count = U16(pdu, 3);
        if (count < 1 || count > 125) return Exception(pdu[0], 3);
        if (OutOfRange(table, address, count)) return Exception(pdu[0], 2);
        var r = new byte[2 + count * 2];
        r[0] = pdu[0];
        r[1] = (byte)(count * 2);
        for (var i = 0; i < count; i++) BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(2 + i * 2), data[address + i]);
        return r;
    }

    private byte[] WriteSingleCoil(ReadOnlySpan<byte> pdu)
    {
        if (pdu.Length != 5) return Exception(5, 3);
        int address = U16(pdu, 1), value = U16(pdu, 3);
        if (value != 0x0000 && value != 0xFF00) return Exception(5, 3);
        if (OutOfRange(FunctionCode.ReadCoils, address, 1)) return Exception(5, 2);
        _store.Coils[address] = value == 0xFF00;
        return pdu.ToArray();
    }

    private byte[] WriteSingleRegister(ReadOnlySpan<byte> pdu)
    {
        if (pdu.Length != 5) return Exception(6, 3);
        int address = U16(pdu, 1);
        if (OutOfRange(FunctionCode.ReadHoldingRegisters, address, 1)) return Exception(6, 2);
        _store.HoldingRegisters[address] = U16(pdu, 3);
        return pdu.ToArray();
    }

    private byte[] WriteMultipleCoils(ReadOnlySpan<byte> pdu)
    {
        if (pdu.Length < 6) return Exception(15, 3);
        int address = U16(pdu, 1), count = U16(pdu, 3), bytes = pdu[5];
        if (count < 1 || count > 1968 || bytes != (count + 7) / 8 || pdu.Length != 6 + bytes) return Exception(15, 3);
        if (OutOfRange(FunctionCode.ReadCoils, address, count)) return Exception(15, 2);
        for (var i = 0; i < count; i++) _store.Coils[address + i] = (pdu[6 + i / 8] & (1 << (i % 8))) != 0;
        return pdu[..5].ToArray();
    }

    private byte[] WriteMultipleRegisters(ReadOnlySpan<byte> pdu)
    {
        if (pdu.Length < 6) return Exception(16, 3);
        int address = U16(pdu, 1), count = U16(pdu, 3), bytes = pdu[5];
        if (count < 1 || count > 123 || bytes != count * 2 || pdu.Length != 6 + bytes) return Exception(16, 3);
        if (OutOfRange(FunctionCode.ReadHoldingRegisters, address, count)) return Exception(16, 2);
        for (var i = 0; i < count; i++) _store.HoldingRegisters[address + i] = U16(pdu, 6 + i * 2);
        return pdu[..5].ToArray();
    }

    private byte[] MaskWrite(ReadOnlySpan<byte> pdu)
    {
        if (pdu.Length != 7) return Exception(22, 3);
        int address = U16(pdu, 1);
        if (OutOfRange(FunctionCode.ReadHoldingRegisters, address, 1)) return Exception(22, 2);
        ushort and = U16(pdu, 3), or = U16(pdu, 5);
        var current = _store.HoldingRegisters[address];
        _store.HoldingRegisters[address] = (ushort)((current & and) | (or & ~and));
        return pdu.ToArray();
    }

    private byte[] ReadWrite(ReadOnlySpan<byte> pdu)
    {
        if (pdu.Length < 10) return Exception(23, 3);
        int ra = U16(pdu, 1), rc = U16(pdu, 3), wa = U16(pdu, 5), wc = U16(pdu, 7), bytes = pdu[9];
        if (rc < 1 || rc > 125 || wc < 1 || wc > 121 || bytes != wc * 2 || pdu.Length != 10 + bytes) return Exception(23, 3);
        if (OutOfRange(FunctionCode.ReadHoldingRegisters, ra, rc) || OutOfRange(FunctionCode.ReadHoldingRegisters, wa, wc)) return Exception(23, 2);
        for (var i = 0; i < wc; i++) _store.HoldingRegisters[wa + i] = U16(pdu, 10 + i * 2);
        var r = new byte[2 + rc * 2];
        r[0] = 23;
        r[1] = (byte)(rc * 2);
        for (var i = 0; i < rc; i++) BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(2 + i * 2), _store.HoldingRegisters[ra + i]);
        return r;
    }

    private byte[] ReportServerId()
    {
        var id = Encoding.ASCII.GetBytes(ServerId);
        if (id.Length > 240) id = id[..240];
        var r = new byte[3 + id.Length];
        r[0] = 17;
        r[1] = (byte)(id.Length + 1);
        id.CopyTo(r, 2);
        r[^1] = RunIndicator ? (byte)0xFF : (byte)0x00;
        return r;
    }

    private byte[]? Diagnostics(ReadOnlySpan<byte> pdu)
    {
        if (pdu.Length != 5) return Exception(8, 3);
        var sub = U16(pdu, 1);
        var data = U16(pdu, 3);
        byte s1 = pdu[1], s2 = pdu[2];
        byte[] Value(long v) => [8, s1, s2, (byte)(Math.Min(v, 0xFFFF) >> 8), (byte)Math.Min(v, 0xFFFF)];
        switch (sub)
        {
            case 0: return pdu.ToArray();                                  // Return Query Data
            case 1:                                                          // Restart Communications Option
                if (data is not (0x0000 or 0xFF00)) return Exception(8, 3);
                Counters.Clear();
                return pdu.ToArray();
            case 2: return Value(0);                                        // Diagnostic register
            case 3: return pdu.ToArray();                                  // Change ASCII input delimiter
            case 4: ListenOnly = true; return null;                        // Force listen only: no response
            case 10: Counters.Clear(); return pdu.ToArray();
            case 11: return Value(Counters.BusMessages);
            case 12: return Value(Counters.BusCommunicationErrors);
            case 13: return Value(Counters.BusExceptionErrors);
            case 14: return Value(Counters.ServerMessages);
            case 15: return Value(Counters.ServerNoResponses);
            case 16: case 17: case 18: return Value(0);                    // NAK, busy, overrun
            case 20: return pdu.ToArray();                                 // Clear overrun counter
            default: return Exception(8, 1);
        }
    }

    private byte[] ReadDeviceIdentification(ReadOnlySpan<byte> pdu)
    {
        if (pdu.Length != 4 || pdu[1] != 0x0E) return Exception(43, 1);
        var code = pdu[2];
        var objectId = pdu[3];
        var objects = Identification.Objects();
        if (code is < 1 or > 4) return Exception(43, 3);

        List<int> ids;
        if (code == 4)
        {
            if (objectId >= objects.Length) return Exception(43, 2);
            ids = [objectId];
        }
        else
        {
            var last = code == 1 ? 2 : objects.Length - 1;
            var first = objectId > last ? 0 : objectId;
            ids = Enumerable.Range(first, last - first + 1).ToList();
        }

        // Fit into one PDU (253 bytes); the rest follows with "more follows".
        var body = new List<byte>();
        var included = 0;
        byte nextId = 0;
        byte more = 0;
        foreach (var id in ids)
        {
            var value = Encoding.ASCII.GetBytes(objects[id]);
            if (value.Length > 245) value = value[..245];
            if (7 + body.Count + 2 + value.Length > 253 && included > 0)
            {
                more = 0xFF;
                nextId = (byte)id;
                break;
            }
            body.Add((byte)id);
            body.Add((byte)value.Length);
            body.AddRange(value);
            included++;
        }

        var r = new List<byte> { 43, 0x0E, code, 0x82, more, nextId, (byte)included };
        r.AddRange(body);
        return r.ToArray();
    }
}
