using System.Buffers.Binary;
using System.Text;
using Modbus.Core.Channels;
using Modbus.Core.Framing;

namespace Modbus.Core.Server;

public enum SimulatorFraming { Mbap, Rtu, Ascii }

/// <summary>Deliberate damage to a response frame, for testing how a master copes with bad answers.</summary>
public enum FrameFault
{
    None,
    /// <summary>Wrong CRC (RTU), wrong LRC (ASCII) or a wrong MBAP protocol identifier (TCP/UDP).</summary>
    BadChecksum,
    /// <summary>The end of the frame is cut off.</summary>
    Truncate,
    /// <summary>The answer carries another slave/unit ID.</summary>
    WrongId,
}

/// <summary>What a server sends back for one request.</summary>
public readonly record struct ServerReply(byte[]? Pdu, TimeSpan Delay = default, FrameFault Fault = FrameFault.None)
{
    public static readonly ServerReply NoResponse = new(null);
}

/// <summary>Decides the answer to one request. Implemented by a single data store or by the multi-slave engine.</summary>
public interface IServerHandler
{
    ServerReply Handle(byte unit, byte[] pdu);

    /// <summary>Called for every frame seen on the line; <paramref name="valid"/> is false for CRC/LRC/format errors.</summary>
    void OnFrame(bool valid);
}

/// <summary>Request parsing and reply encoding shared by the TCP, UDP and serial servers.</summary>
public static class ServerFrames
{
    /// <summary>Bytes after slave ID + function code in an RTU request, or -1 when the length depends on a byte count.</summary>
    private static int FixedRest(byte function) => function switch
    {
        1 or 2 or 3 or 4 or 5 or 6 or 8 => 6,
        11 or 17 => 2,
        22 => 8,
        43 => 5,
        _ => -1,
    };

    /// <summary>Reads one RTU request. The first byte may take until <paramref name="firstByteDeadline"/>; the rest must follow within <paramref name="frameTimeout"/>.</summary>
    public static byte[] ReadRtuRequest(IByteChannel channel, DateTime firstByteDeadline, TimeSpan frameTimeout)
    {
        var frame = new List<byte>(64) { channel.ReadExact(1, firstByteDeadline)[0] };
        var deadline = DateTime.UtcNow + frameTimeout;
        frame.Add(channel.ReadExact(1, deadline)[0]);
        var function = frame[1];
        var rest = FixedRest(function);
        if (rest < 0)
        {
            int header = function switch { 15 or 16 => 5, 23 => 9, _ => -1 };
            if (header < 0) throw new ModbusFrameException($"Unknown function code {function} in request.");
            frame.AddRange(channel.ReadExact(header, deadline));
            rest = frame[^1] + 2;
        }
        frame.AddRange(channel.ReadExact(rest, deadline));
        return frame.ToArray();
    }

    public static byte[] ReadMbapRequest(IByteChannel channel, DateTime deadline)
    {
        var header = channel.ReadExact(7, deadline);
        var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
        if (length < 2 || length > 254) throw new ModbusFrameException("Invalid MBAP length.");
        return [.. header, .. channel.ReadExact(length - 1, deadline)];
    }

    /// <summary>Splits a request frame into (transaction ID, unit, PDU). Returns false for damaged frames.</summary>
    public static bool TryParse(SimulatorFraming framing, byte[] frame, out ushort transactionId, out byte unit, out byte[] pdu)
    {
        transactionId = 0;
        unit = 0;
        pdu = [];
        switch (framing)
        {
            case SimulatorFraming.Mbap:
                if (frame.Length < 8) return false;
                var length = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(4));
                if (BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(2)) != 0 || length != frame.Length - 6) return false;
                transactionId = BinaryPrimitives.ReadUInt16BigEndian(frame);
                unit = frame[6];
                pdu = frame[7..];
                return true;
            case SimulatorFraming.Rtu:
                if (frame.Length < 4 || !RtuFramer.CheckCrc(frame)) return false;
                unit = frame[0];
                pdu = frame[1..^2];
                return true;
            default:
                try
                {
                    var raw = AsciiFramer.Decode(frame);
                    unit = raw[0];
                    pdu = raw[1..];
                    return true;
                }
                catch (ModbusFrameException)
                {
                    return false;
                }
        }
    }

    /// <summary>Encodes a reply PDU, applying a deliberate <paramref name="fault"/>.</summary>
    public static byte[] EncodeReply(SimulatorFraming framing, ushort transactionId, byte unit, byte[] pdu, FrameFault fault)
    {
        var replyUnit = fault == FrameFault.WrongId ? unchecked((byte)(unit + 1)) : unit;
        byte[] frame;
        switch (framing)
        {
            case SimulatorFraming.Mbap:
                frame = MbapFramer.Encode(transactionId, replyUnit, pdu);
                if (fault == FrameFault.BadChecksum) frame[3] = 0x01; // protocol identifier must be 0
                if (fault == FrameFault.Truncate) frame = frame[..Math.Max(7, frame.Length - Math.Max(1, pdu.Length / 2))];
                return frame;
            case SimulatorFraming.Rtu:
                frame = RtuFramer.Encode(replyUnit, pdu);
                if (fault == FrameFault.BadChecksum) frame[^1] ^= 0x5A;
                if (fault == FrameFault.Truncate) frame = frame[..Math.Max(2, frame.Length - 3)];
                return frame;
            default:
                frame = AsciiFramer.Encode(replyUnit, pdu);
                if (fault == FrameFault.BadChecksum)
                {
                    // Replace the LRC with a wrong one.
                    var text = Encoding.ASCII.GetString(frame);
                    var lrc = Convert.ToByte(text[^4..^2], 16);
                    frame = Encoding.ASCII.GetBytes(text[..^4] + ((byte)(lrc ^ 0x5A)).ToString("X2") + "\r\n");
                }
                if (fault == FrameFault.Truncate) frame = frame[..Math.Max(1, frame.Length - 5)];
                return frame;
        }
    }

    /// <summary>
    /// Reads, answers and logs one request on a stream channel. Returns false when nothing was read before
    /// <paramref name="firstByteDeadline"/> (serial idle) so the caller can check for shutdown.
    /// </summary>
    public static bool ServeOne(IByteChannel channel, SimulatorFraming framing, IServerHandler handler, Func<byte, bool> accepts,
        Action<TrafficEntry> log, DateTime firstByteDeadline, TimeSpan frameTimeout)
    {
        byte[] frame;
        try
        {
            frame = framing switch
            {
                SimulatorFraming.Mbap => ReadMbapRequest(channel, firstByteDeadline),
                SimulatorFraming.Rtu => ReadRtuRequest(channel, firstByteDeadline, frameTimeout),
                _ => AsciiFramer.ReadFrame(channel, firstByteDeadline),
            };
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (ModbusFrameException)
        {
            handler.OnFrame(valid: false);
            Thread.Sleep(20);
            channel.DiscardInput();
            return true;
        }

        Respond(frame, framing, handler, accepts, log, reply => channel.Write(reply));
        return true;
    }

    /// <summary>Parses one complete request frame and sends the reply through <paramref name="send"/>.</summary>
    public static void Respond(byte[] frame, SimulatorFraming framing, IServerHandler handler, Func<byte, bool> accepts,
        Action<TrafficEntry> log, Action<byte[]> send)
    {
        var ascii = framing == SimulatorFraming.Ascii;
        if (!TryParse(framing, frame, out var tid, out var unit, out var pdu))
        {
            log(new TrafficEntry(DateTime.Now, TrafficDirection.Rx, frame, ascii, "damaged request ignored"));
            handler.OnFrame(valid: false);
            return;
        }
        log(new TrafficEntry(DateTime.Now, TrafficDirection.Rx, frame, ascii));
        handler.OnFrame(valid: true);
        if (!accepts(unit)) return;

        var reply = handler.Handle(unit, pdu);
        if (reply.Pdu is null)
        {
            log(new TrafficEntry(DateTime.Now, TrafficDirection.Tx, [], ascii, $"no response (slave {unit})"));
            return;
        }
        if (unit == 0 && framing != SimulatorFraming.Mbap) return; // serial broadcast is never answered
        if (reply.Delay > TimeSpan.Zero) Thread.Sleep(reply.Delay);
        var bytes = EncodeReply(framing, tid, unit, reply.Pdu, reply.Fault);
        log(new TrafficEntry(DateTime.Now, TrafficDirection.Tx, bytes, ascii, reply.Fault == FrameFault.None ? null : $"simulated {reply.Fault}"));
        send(bytes);
    }
}
