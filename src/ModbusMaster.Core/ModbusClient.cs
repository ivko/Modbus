using System.Buffers.Binary;
using System.Diagnostics;
using ModbusMaster.Core.Channels;
using ModbusMaster.Core.Framing;
using ModbusMaster.Core.Protocol;

namespace ModbusMaster.Core;

/// <summary>
/// Modbus master over one connection. Thread-safe: requests from several threads are serialized,
/// which is what a shared serial bus needs anyway.
/// </summary>
public sealed class ModbusClient : IDisposable
{
    private readonly Func<IByteChannel> _open;
    private readonly IFramer _framer;
    private readonly object _gate = new();
    private readonly Stopwatch _sinceLast = Stopwatch.StartNew();
    private IByteChannel? _channel;
    private ushort _transactionId;
    private long _txCount;
    private long _rxCount;
    private long _errorCount;
    private bool _disposed;

    public ModbusClient(Func<IByteChannel> open, IFramer framer)
    {
        _open = open;
        _framer = framer;
    }

    public TimeSpan ResponseTimeout { get; set; } = TimeSpan.FromMilliseconds(1000);

    /// <summary>Minimum silence between the previous response and the next request.</summary>
    public TimeSpan DelayBetweenPolls { get; set; } = TimeSpan.FromMilliseconds(20);

    public string Description { get; init; } = "";

    public event Action<TrafficEntry>? Traffic;

    public long TxCount => Interlocked.Read(ref _txCount);
    public long RxCount => Interlocked.Read(ref _rxCount);
    public long ErrorCount => Interlocked.Read(ref _errorCount);

    public bool IsOpen
    {
        get { lock (_gate) return _channel is not null; }
    }

    /// <summary>Opens the connection now instead of on the first request.</summary>
    public void Open()
    {
        lock (_gate) EnsureOpen();
    }

    public void Close()
    {
        lock (_gate) Drop();
    }

    public void ResetCounters()
    {
        Interlocked.Exchange(ref _txCount, 0);
        Interlocked.Exchange(ref _rxCount, 0);
        Interlocked.Exchange(ref _errorCount, 0);
    }

    /// <summary>Sends one request PDU and returns the response PDU. Unit 0 on serial is a broadcast without response.</summary>
    public byte[] Execute(byte unit, byte[] pdu)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var channel = EnsureOpen();
            WaitForBusIdle();
            var tid = unchecked(++_transactionId);
            try
            {
                Interlocked.Increment(ref _txCount);
                _framer.WriteRequest(channel, tid, unit, pdu, Emit);
                if (unit == 0 && !_framer.UsesTransactionId)
                    return [];

                var deadline = DateTime.UtcNow + ResponseTimeout;
                byte[] response;
                try
                {
                    response = _framer.ReadResponse(channel, tid, unit, deadline, Emit);
                }
                catch (TimeoutException)
                {
                    if (channel.FaultsOnTimeout) Drop();
                    throw new ModbusTimeoutException($"Timeout error. No response within {ResponseTimeout.TotalMilliseconds:0} ms.");
                }

                Interlocked.Increment(ref _rxCount);
                if (response.Length == 0)
                    throw new ModbusFrameException("Empty response.", ModbusResult.InsufficientBytes);
                if ((response[0] & 0x7F) != pdu[0])
                    throw new ModbusFrameException($"Response for function {response[0] & 0x7F}, expected {pdu[0]}.");
                if ((response[0] & 0x80) != 0)
                    throw new ModbusSlaveException(pdu[0], response.Length > 1 ? response[1] : (byte)0);
                return response;
            }
            catch (IOException ex)
            {
                Interlocked.Increment(ref _errorCount);
                Drop();
                throw new ModbusConnectionException(ex.Message, 0, ex);
            }
            catch (Exception ex) when (ex is ModbusCommunicationException or ModbusSlaveException)
            {
                Interlocked.Increment(ref _errorCount);
                throw;
            }
            finally
            {
                _sinceLast.Restart();
            }
        }
    }

    /// <summary>
    /// Test Center: sends bytes exactly as given and collects whatever arrives until the line is idle
    /// for <paramref name="idleGap"/> or the response timeout expires.
    /// </summary>
    public byte[] SendRaw(byte[] bytes, TimeSpan idleGap)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var channel = EnsureOpen();
            WaitForBusIdle();
            try
            {
                channel.DiscardInput();
                Emit(new TrafficEntry(DateTime.Now, TrafficDirection.Tx, bytes, IsAscii(bytes)));
                Interlocked.Increment(ref _txCount);
                channel.Write(bytes);

                var received = new List<byte>();
                var buffer = new byte[512];
                var deadline = DateTime.UtcNow + ResponseTimeout;
                while (true)
                {
                    var wait = received.Count == 0 ? deadline - DateTime.UtcNow : idleGap;
                    if (wait <= TimeSpan.Zero) break;
                    try
                    {
                        var n = channel.Read(buffer, 0, buffer.Length, wait);
                        received.AddRange(buffer.AsSpan(0, n));
                    }
                    catch (TimeoutException)
                    {
                        break;
                    }
                }

                var data = received.ToArray();
                if (data.Length > 0)
                {
                    Interlocked.Increment(ref _rxCount);
                    Emit(new TrafficEntry(DateTime.Now, TrafficDirection.Rx, data, IsAscii(data)));
                }
                return data;
            }
            catch (IOException ex)
            {
                Interlocked.Increment(ref _errorCount);
                Drop();
                throw new ModbusConnectionException(ex.Message, 0, ex);
            }
            finally
            {
                _sinceLast.Restart();
            }
        }
    }

    // ---- Convenience API -------------------------------------------------------------

    public bool[] ReadCoils(byte unit, ushort address, int count) =>
        Pdu.ParseBits(Execute(unit, Pdu.Read(FunctionCode.ReadCoils, address, count)), count);

    public bool[] ReadDiscreteInputs(byte unit, ushort address, int count) =>
        Pdu.ParseBits(Execute(unit, Pdu.Read(FunctionCode.ReadDiscreteInputs, address, count)), count);

    public ushort[] ReadHoldingRegisters(byte unit, ushort address, int count) =>
        Pdu.ParseRegisters(Execute(unit, Pdu.Read(FunctionCode.ReadHoldingRegisters, address, count)), count);

    public ushort[] ReadInputRegisters(byte unit, ushort address, int count) =>
        Pdu.ParseRegisters(Execute(unit, Pdu.Read(FunctionCode.ReadInputRegisters, address, count)), count);

    /// <summary>Reads coils/inputs (as 0/1) or registers with function 1, 2, 3 or 4.</summary>
    public ushort[] Read(byte unit, FunctionCode function, ushort address, int count) => function switch
    {
        FunctionCode.ReadCoils => ReadCoils(unit, address, count).Select(b => (ushort)(b ? 1 : 0)).ToArray(),
        FunctionCode.ReadDiscreteInputs => ReadDiscreteInputs(unit, address, count).Select(b => (ushort)(b ? 1 : 0)).ToArray(),
        FunctionCode.ReadHoldingRegisters => ReadHoldingRegisters(unit, address, count),
        FunctionCode.ReadInputRegisters => ReadInputRegisters(unit, address, count),
        _ => throw new ArgumentException($"Function {(byte)function} is not a read function."),
    };

    public void WriteSingleCoil(byte unit, ushort address, bool value) => WriteEcho(unit, Pdu.WriteSingleCoil(address, value), 5);

    public void WriteSingleRegister(byte unit, ushort address, ushort value) => WriteEcho(unit, Pdu.WriteSingleRegister(address, value), 5);

    public void WriteMultipleCoils(byte unit, ushort address, IReadOnlyList<bool> values) => WriteEcho(unit, Pdu.WriteMultipleCoils(address, values), 5);

    public void WriteMultipleRegisters(byte unit, ushort address, IReadOnlyList<ushort> values) => WriteEcho(unit, Pdu.WriteMultipleRegisters(address, values), 5);

    public void MaskWriteRegister(byte unit, ushort address, ushort andMask, ushort orMask) => WriteEcho(unit, Pdu.MaskWriteRegister(address, andMask, orMask), 7);

    public ushort[] ReadWriteMultipleRegisters(byte unit, ushort readAddress, int readCount, ushort writeAddress, IReadOnlyList<ushort> values) =>
        Pdu.ParseRegisters(Execute(unit, Pdu.ReadWriteMultipleRegisters(readAddress, readCount, writeAddress, values)), readCount);

    /// <summary>Function 17. Returns the data after the byte count (server ID, run indicator, device specific data).</summary>
    public byte[] ReportServerId(byte unit)
    {
        var response = Execute(unit, Pdu.ReportServerId());
        if (response.Length < 2 || response.Length != 2 + response[1])
            throw new ModbusFrameException("Byte count error in Report Server ID response.", ModbusResult.ByteCountError);
        return response[2..];
    }

    /// <summary>Function 08. Returns the data field of the response.</summary>
    public ushort Diagnostics(byte unit, ushort subFunction, ushort data)
    {
        var request = Pdu.Diagnostics(subFunction, data);
        var response = Execute(unit, request);
        if (unit == 0 && response.Length == 0) return 0;
        Pdu.ExpectEcho(response, request, 3);
        if (response.Length < 5) throw new ModbusFrameException("Diagnostics response too short.", ModbusResult.InsufficientBytes);
        return BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(3));
    }

    /// <summary>Function 11. Returns (status word, event count). Status 0xFFFF means a previous command is still running.</summary>
    public (ushort Status, ushort EventCount) GetCommEventCounter(byte unit)
    {
        var response = Execute(unit, [(byte)FunctionCode.GetCommEventCounter]);
        if (response.Length != 5) throw new ModbusFrameException("Get Comm Event Counter response must have 4 data bytes.", ModbusResult.ByteCountError);
        return (BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(1)), BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(3)));
    }

    /// <summary>
    /// Function 43 / MEI 14 Read Device Identification. <paramref name="code"/>: 1 basic, 2 regular, 3 extended,
    /// 4 one specific object. Follows "more follows" until all objects of the category are read.
    /// </summary>
    public IReadOnlyList<(byte Id, string Value)> ReadDeviceIdentification(byte unit, byte code = 2, byte objectId = 0)
    {
        var result = new List<(byte, string)>();
        var next = objectId;
        for (var guard = 0; guard < 64; guard++)
        {
            var response = Execute(unit, [(byte)FunctionCode.EncapsulatedInterface, 0x0E, code, next]);
            if (response.Length < 7 || response[1] != 0x0E)
                throw new ModbusFrameException("Invalid Read Device Identification response.", ModbusResult.ByteCountError);
            var more = response[4];
            next = response[5];
            var count = response[6];
            var pos = 7;
            for (var i = 0; i < count; i++)
            {
                if (pos + 2 > response.Length) throw new ModbusFrameException("Truncated device identification object.", ModbusResult.InsufficientBytes);
                var id = response[pos];
                var len = response[pos + 1];
                if (pos + 2 + len > response.Length) throw new ModbusFrameException("Truncated device identification object.", ModbusResult.InsufficientBytes);
                result.Add((id, System.Text.Encoding.ASCII.GetString(response, pos + 2, len)));
                pos += 2 + len;
            }
            if (more != 0xFF || code == 4) break;
        }
        return result;
    }

    private void WriteEcho(byte unit, byte[] request, int echoLength)
    {
        var response = Execute(unit, request);
        if (unit == 0 && response.Length == 0) return;
        Pdu.ExpectEcho(response, request, echoLength);
    }

    // ---- internals --------------------------------------------------------------------

    private IByteChannel EnsureOpen()
    {
        if (_channel is not null) return _channel;
        _channel = _open();
        return _channel;
    }

    private void Drop()
    {
        _channel?.Dispose();
        _channel = null;
    }

    private void WaitForBusIdle()
    {
        var wait = DelayBetweenPolls - _sinceLast.Elapsed;
        if (wait > TimeSpan.Zero) Thread.Sleep(wait);
    }

    private void Emit(TrafficEntry entry) => Traffic?.Invoke(entry);

    private static bool IsAscii(byte[] data) => data.Length > 0 && data[0] == (byte)':' && data.All(b => b is >= 0x20 and < 0x7F or 0x0D or 0x0A);

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            Drop();
        }
    }
}
