using System.Buffers.Binary;

namespace Modbus.Core.Protocol;

/// <summary>Builds request PDUs and validates/parses response PDUs (function code + data).</summary>
public static class Pdu
{
    public const int MaxReadBits = 2000;
    public const int MaxReadRegisters = 125;
    public const int MaxWriteBits = 1968;
    public const int MaxWriteRegisters = 123;
    public const int MaxReadWriteWriteRegisters = 121;

    public static int MaxQuantity(FunctionCode function) => function switch
    {
        FunctionCode.ReadCoils or FunctionCode.ReadDiscreteInputs => MaxReadBits,
        FunctionCode.ReadHoldingRegisters or FunctionCode.ReadInputRegisters => MaxReadRegisters,
        FunctionCode.WriteMultipleCoils => MaxWriteBits,
        FunctionCode.WriteMultipleRegisters => MaxWriteRegisters,
        FunctionCode.ReadWriteMultipleRegisters => MaxReadWriteWriteRegisters,
        _ => 1,
    };

    public static byte[] Read(FunctionCode function, ushort address, int count)
    {
        if (!FunctionCodes.IsRead(function))
            throw new ArgumentException($"Function {(byte)function} is not a read function.", nameof(function));
        CheckRange(address, count, MaxQuantity(function));
        var pdu = new byte[5];
        pdu[0] = (byte)function;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), address);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), (ushort)count);
        return pdu;
    }

    public static byte[] WriteSingleCoil(ushort address, bool value)
    {
        var pdu = new byte[5];
        pdu[0] = (byte)FunctionCode.WriteSingleCoil;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), address);
        pdu[3] = value ? (byte)0xFF : (byte)0x00;
        return pdu;
    }

    public static byte[] WriteSingleRegister(ushort address, ushort value)
    {
        var pdu = new byte[5];
        pdu[0] = (byte)FunctionCode.WriteSingleRegister;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), address);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), value);
        return pdu;
    }

    public static byte[] WriteMultipleCoils(ushort address, IReadOnlyList<bool> values)
    {
        CheckRange(address, values.Count, MaxWriteBits);
        var byteCount = (values.Count + 7) / 8;
        var pdu = new byte[6 + byteCount];
        pdu[0] = (byte)FunctionCode.WriteMultipleCoils;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), address);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), (ushort)values.Count);
        pdu[5] = (byte)byteCount;
        for (var i = 0; i < values.Count; i++)
            if (values[i]) pdu[6 + i / 8] |= (byte)(1 << (i % 8));
        return pdu;
    }

    public static byte[] WriteMultipleRegisters(ushort address, IReadOnlyList<ushort> values)
    {
        CheckRange(address, values.Count, MaxWriteRegisters);
        var pdu = new byte[6 + values.Count * 2];
        pdu[0] = (byte)FunctionCode.WriteMultipleRegisters;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), address);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), (ushort)values.Count);
        pdu[5] = (byte)(values.Count * 2);
        for (var i = 0; i < values.Count; i++)
            BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(6 + i * 2), values[i]);
        return pdu;
    }

    public static byte[] MaskWriteRegister(ushort address, ushort andMask, ushort orMask)
    {
        var pdu = new byte[7];
        pdu[0] = (byte)FunctionCode.MaskWriteRegister;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), address);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), andMask);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(5), orMask);
        return pdu;
    }

    public static byte[] ReadWriteMultipleRegisters(ushort readAddress, int readCount, ushort writeAddress, IReadOnlyList<ushort> values)
    {
        CheckRange(readAddress, readCount, MaxReadRegisters);
        CheckRange(writeAddress, values.Count, MaxReadWriteWriteRegisters);
        var pdu = new byte[10 + values.Count * 2];
        pdu[0] = (byte)FunctionCode.ReadWriteMultipleRegisters;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), readAddress);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), (ushort)readCount);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(5), writeAddress);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(7), (ushort)values.Count);
        pdu[9] = (byte)(values.Count * 2);
        for (var i = 0; i < values.Count; i++)
            BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(10 + i * 2), values[i]);
        return pdu;
    }

    public static byte[] ReportServerId() => [(byte)FunctionCode.ReportServerId];

    public static byte[] Diagnostics(ushort subFunction, ushort data)
    {
        var pdu = new byte[5];
        pdu[0] = (byte)FunctionCode.Diagnostics;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), subFunction);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), data);
        return pdu;
    }

    public static bool[] ParseBits(byte[] response, int count)
    {
        var expected = (count + 7) / 8;
        if (response.Length < 2 || response[1] != expected || response.Length != 2 + expected)
            throw new ModbusFrameException($"Byte count error: expected {expected} data bytes.", ModbusResult.ByteCountError);
        var result = new bool[count];
        for (var i = 0; i < count; i++)
            result[i] = (response[2 + i / 8] & (1 << (i % 8))) != 0;
        return result;
    }

    public static ushort[] ParseRegisters(byte[] response, int count)
    {
        var expected = count * 2;
        if (response.Length < 2 || response[1] != expected || response.Length != 2 + expected)
            throw new ModbusFrameException($"Byte count error: expected {expected} data bytes.", ModbusResult.ByteCountError);
        var result = new ushort[count];
        for (var i = 0; i < count; i++)
            result[i] = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(2 + i * 2));
        return result;
    }

    /// <summary>Checks that the first <paramref name="length"/> bytes of the response echo the request.</summary>
    public static void ExpectEcho(byte[] response, byte[] request, int length)
    {
        if (response.Length < length || !response.AsSpan(0, length).SequenceEqual(request.AsSpan(0, length)))
            throw new ModbusFrameException("The response does not echo the request (different address or quantity).");
    }

    private static void CheckRange(ushort address, int count, int max)
    {
        if (count < 1 || count > max)
            throw new ArgumentOutOfRangeException(nameof(count), $"Quantity must be 1 to {max}.");
        if (address + count > 65536)
            throw new ArgumentOutOfRangeException(nameof(address), "Address + quantity exceeds 65535.");
    }
}
