using System.Buffers.Binary;
using System.Text;
using ModbusMaster.Core.Channels;
using ModbusMaster.Core.Protocol;

namespace ModbusMaster.Core.Framing;

/// <summary>Wraps a PDU into an ADU for one transport (MBAP, RTU or ASCII) and reads one response.</summary>
public interface IFramer
{
    bool UsesTransactionId { get; }
    void WriteRequest(IByteChannel channel, ushort transactionId, byte unit, byte[] pdu, Action<TrafficEntry> log);
    byte[] ReadResponse(IByteChannel channel, ushort transactionId, byte unit, DateTime deadlineUtc, Action<TrafficEntry> log);
}

public sealed class MbapFramer : IFramer
{
    public bool UsesTransactionId => true;

    public static byte[] Encode(ushort transactionId, byte unit, ReadOnlySpan<byte> pdu)
    {
        var frame = new byte[7 + pdu.Length];
        BinaryPrimitives.WriteUInt16BigEndian(frame, transactionId);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), (ushort)(pdu.Length + 1));
        frame[6] = unit;
        pdu.CopyTo(frame.AsSpan(7));
        return frame;
    }

    public void WriteRequest(IByteChannel channel, ushort transactionId, byte unit, byte[] pdu, Action<TrafficEntry> log)
    {
        channel.DiscardInput();
        var frame = Encode(transactionId, unit, pdu);
        log(new TrafficEntry(DateTime.Now, TrafficDirection.Tx, frame));
        channel.Write(frame);
    }

    public byte[] ReadResponse(IByteChannel channel, ushort transactionId, byte unit, DateTime deadlineUtc, Action<TrafficEntry> log)
    {
        while (true)
        {
            var header = channel.ReadExact(7, deadlineUtc);
            var tid = BinaryPrimitives.ReadUInt16BigEndian(header);
            var protocol = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2));
            var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
            if (protocol != 0 || length < 2 || length > 254)
            {
                log(new TrafficEntry(DateTime.Now, TrafficDirection.Rx, header, Note: "invalid MBAP header"));
                channel.DiscardInput();
                throw new ModbusFrameException($"Invalid MBAP header (protocol {protocol}, length {length}).");
            }

            var pdu = channel.ReadExact(length - 1, deadlineUtc);
            var frame = new byte[7 + pdu.Length];
            header.CopyTo(frame, 0);
            pdu.CopyTo(frame, 7);
            if (tid != transactionId)
            {
                log(new TrafficEntry(DateTime.Now, TrafficDirection.Rx, frame, Note: $"late response, transaction {tid} ignored"));
                continue;
            }

            log(new TrafficEntry(DateTime.Now, TrafficDirection.Rx, frame));
            if (header[6] != unit)
                throw new ModbusFrameException($"Response from unit ID {header[6]}, expected {unit}.");
            return pdu;
        }
    }
}

public sealed class RtuFramer : IFramer
{
    public bool UsesTransactionId => false;

    public static byte[] Encode(byte unit, ReadOnlySpan<byte> pdu)
    {
        var frame = new byte[pdu.Length + 3];
        frame[0] = unit;
        pdu.CopyTo(frame.AsSpan(1));
        var crc = Checksums.Crc16(frame.AsSpan(0, frame.Length - 2));
        frame[^2] = (byte)crc;
        frame[^1] = (byte)(crc >> 8);
        return frame;
    }

    public static bool CheckCrc(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 4) return false;
        var crc = Checksums.Crc16(frame[..^2]);
        return frame[^2] == (byte)crc && frame[^1] == (byte)(crc >> 8);
    }

    public void WriteRequest(IByteChannel channel, ushort transactionId, byte unit, byte[] pdu, Action<TrafficEntry> log)
    {
        channel.DiscardInput();
        var frame = Encode(unit, pdu);
        log(new TrafficEntry(DateTime.Now, TrafficDirection.Tx, frame));
        channel.Write(frame);
    }

    public byte[] ReadResponse(IByteChannel channel, ushort transactionId, byte unit, DateTime deadlineUtc, Action<TrafficEntry> log)
    {
        var frame = new List<byte>(260);
        try
        {
            frame.AddRange(channel.ReadExact(2, deadlineUtc));
            var function = frame[1];
            int rest;
            if ((function & 0x80) != 0)
            {
                rest = 3;
            }
            else
            {
                switch (function)
                {
                    case 1: case 2: case 3: case 4: case 17: case 23:
                        var count = channel.ReadExact(1, deadlineUtc);
                        frame.Add(count[0]);
                        rest = count[0] + 2;
                        break;
                    case 5: case 6: case 8: case 15: case 16:
                        rest = 6;
                        break;
                    case 22:
                        rest = 8;
                        break;
                    default:
                        log(new TrafficEntry(DateTime.Now, TrafficDirection.Rx, frame.ToArray(), Note: "unknown function code"));
                        channel.DiscardInput();
                        throw new ModbusFrameException($"Unexpected function code {function} in response.");
                }
            }
            frame.AddRange(channel.ReadExact(rest, deadlineUtc));
        }
        catch (TimeoutException) when (frame.Count > 0)
        {
            log(new TrafficEntry(DateTime.Now, TrafficDirection.Rx, frame.ToArray(), Note: "incomplete"));
            throw new ModbusTimeoutException($"Incomplete response: {frame.Count} bytes received.", ModbusResult.InsufficientBytes);
        }

        var bytes = frame.ToArray();
        log(new TrafficEntry(DateTime.Now, TrafficDirection.Rx, bytes));
        if (!CheckCrc(bytes))
        {
            channel.DiscardInput();
            throw new ModbusFrameException("CRC error in response.", ModbusResult.CrcError);
        }
        if (bytes[0] != unit)
            throw new ModbusFrameException($"Response from slave ID {bytes[0]}, expected {unit}.");
        return bytes[1..^2];
    }
}

public sealed class AsciiFramer : IFramer
{
    public bool UsesTransactionId => false;

    public static byte[] Encode(byte unit, ReadOnlySpan<byte> pdu)
    {
        var raw = new byte[pdu.Length + 2];
        raw[0] = unit;
        pdu.CopyTo(raw.AsSpan(1));
        raw[^1] = Checksums.Lrc(raw.AsSpan(0, raw.Length - 1));
        return Encoding.ASCII.GetBytes(":" + Convert.ToHexString(raw) + "\r\n");
    }

    /// <summary>Decodes ":AABB..LL\r\n" into unit+PDU (LRC checked and removed).</summary>
    public static byte[] Decode(ReadOnlySpan<byte> frame)
    {
        var text = Encoding.ASCII.GetString(frame);
        if (!text.StartsWith(':') || !text.EndsWith("\r\n"))
            throw new ModbusFrameException("Invalid ASCII frame: missing ':' or CR LF.");
        byte[] raw;
        try
        {
            raw = Convert.FromHexString(text[1..^2]);
        }
        catch (FormatException)
        {
            throw new ModbusFrameException("Invalid ASCII frame: not hexadecimal.");
        }
        if (raw.Length < 3) throw new ModbusFrameException("ASCII frame too short.", ModbusResult.InsufficientBytes);
        if (Checksums.Lrc(raw.AsSpan(0, raw.Length - 1)) != raw[^1])
            throw new ModbusFrameException("LRC error in response.", ModbusResult.CrcError);
        return raw[..^1];
    }

    public static byte[] ReadFrame(IByteChannel channel, DateTime deadlineUtc)
    {
        while (channel.ReadExact(1, deadlineUtc)[0] != (byte)':') { }
        var frame = new List<byte>(64) { (byte)':' };
        while (true)
        {
            var b = channel.ReadExact(1, deadlineUtc)[0];
            if (b == (byte)':') { frame.Clear(); frame.Add(b); continue; }
            frame.Add(b);
            if (b == (byte)'\n') return frame.ToArray();
            if (frame.Count > 520) throw new ModbusFrameException("ASCII frame too long.");
        }
    }

    public void WriteRequest(IByteChannel channel, ushort transactionId, byte unit, byte[] pdu, Action<TrafficEntry> log)
    {
        channel.DiscardInput();
        var frame = Encode(unit, pdu);
        log(new TrafficEntry(DateTime.Now, TrafficDirection.Tx, frame, IsAsciiFrame: true));
        channel.Write(frame);
    }

    public byte[] ReadResponse(IByteChannel channel, ushort transactionId, byte unit, DateTime deadlineUtc, Action<TrafficEntry> log)
    {
        var frame = ReadFrame(channel, deadlineUtc);
        log(new TrafficEntry(DateTime.Now, TrafficDirection.Rx, frame, IsAsciiFrame: true));
        var raw = Decode(frame);
        if (raw[0] != unit)
            throw new ModbusFrameException($"Response from slave ID {raw[0]}, expected {unit}.");
        return raw[1..];
    }
}
