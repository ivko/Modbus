using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using ModbusMaster.Core.Channels;
using ModbusMaster.Core.Framing;
using ModbusMaster.Core.Protocol;

namespace ModbusMaster.Core.Server;

/// <summary>Memory of a simulated slave: 65536 of each table.</summary>
public sealed class ModbusDataStore
{
    public bool[] Coils { get; } = new bool[65536];
    public bool[] DiscreteInputs { get; } = new bool[65536];
    public ushort[] HoldingRegisters { get; } = new ushort[65536];
    public ushort[] InputRegisters { get; } = new ushort[65536];
    public object SyncRoot { get; } = new();
}

/// <summary>Answers request PDUs from a <see cref="ModbusDataStore"/>.</summary>
public sealed class ModbusRequestHandler
{
    private readonly ModbusDataStore _store;

    public ModbusRequestHandler(ModbusDataStore store) => _store = store;

    public string ServerId { get; set; } = "Modbus Master simulator";

    /// <summary>Optional limit: addresses at or above this return Illegal Data Address.</summary>
    public int AddressLimit { get; set; } = 65536;

    public byte[] Handle(ReadOnlySpan<byte> pdu)
    {
        if (pdu.Length == 0) return Exception(0, 1);
        var f = pdu[0];
        try
        {
            lock (_store.SyncRoot)
            {
                return f switch
                {
                    1 => ReadBits(pdu, _store.Coils, 2000),
                    2 => ReadBits(pdu, _store.DiscreteInputs, 2000),
                    3 => ReadRegisters(pdu, _store.HoldingRegisters),
                    4 => ReadRegisters(pdu, _store.InputRegisters),
                    5 => WriteSingleCoil(pdu),
                    6 => WriteSingleRegister(pdu),
                    8 => Diagnostics(pdu),
                    15 => WriteMultipleCoils(pdu),
                    16 => WriteMultipleRegisters(pdu),
                    17 => ReportServerId(),
                    22 => MaskWrite(pdu),
                    23 => ReadWrite(pdu),
                    _ => Exception(f, 1),
                };
            }
        }
        catch (IndexOutOfRangeException)
        {
            return Exception(f, 3);
        }
        catch (ArgumentOutOfRangeException)
        {
            return Exception(f, 3);
        }
    }

    private static ushort U16(ReadOnlySpan<byte> s, int offset) => BinaryPrimitives.ReadUInt16BigEndian(s[offset..]);

    private static byte[] Exception(byte function, byte code) => [(byte)(function | 0x80), code];

    private bool OutOfRange(int address, int count) => address + count > AddressLimit;

    private byte[] ReadBits(ReadOnlySpan<byte> pdu, bool[] table, int max)
    {
        if (pdu.Length != 5) return Exception(pdu[0], 3);
        int address = U16(pdu, 1), count = U16(pdu, 3);
        if (count < 1 || count > max) return Exception(pdu[0], 3);
        if (OutOfRange(address, count)) return Exception(pdu[0], 2);
        var bytes = (count + 7) / 8;
        var r = new byte[2 + bytes];
        r[0] = pdu[0];
        r[1] = (byte)bytes;
        for (var i = 0; i < count; i++)
            if (table[address + i]) r[2 + i / 8] |= (byte)(1 << (i % 8));
        return r;
    }

    private byte[] ReadRegisters(ReadOnlySpan<byte> pdu, ushort[] table)
    {
        if (pdu.Length != 5) return Exception(pdu[0], 3);
        int address = U16(pdu, 1), count = U16(pdu, 3);
        if (count < 1 || count > 125) return Exception(pdu[0], 3);
        if (OutOfRange(address, count)) return Exception(pdu[0], 2);
        var r = new byte[2 + count * 2];
        r[0] = pdu[0];
        r[1] = (byte)(count * 2);
        for (var i = 0; i < count; i++) BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(2 + i * 2), table[address + i]);
        return r;
    }

    private byte[] WriteSingleCoil(ReadOnlySpan<byte> pdu)
    {
        if (pdu.Length != 5) return Exception(5, 3);
        int address = U16(pdu, 1), value = U16(pdu, 3);
        if (value != 0x0000 && value != 0xFF00) return Exception(5, 3);
        if (OutOfRange(address, 1)) return Exception(5, 2);
        _store.Coils[address] = value == 0xFF00;
        return pdu.ToArray();
    }

    private byte[] WriteSingleRegister(ReadOnlySpan<byte> pdu)
    {
        if (pdu.Length != 5) return Exception(6, 3);
        int address = U16(pdu, 1);
        if (OutOfRange(address, 1)) return Exception(6, 2);
        _store.HoldingRegisters[address] = U16(pdu, 3);
        return pdu.ToArray();
    }

    private byte[] WriteMultipleCoils(ReadOnlySpan<byte> pdu)
    {
        if (pdu.Length < 6) return Exception(15, 3);
        int address = U16(pdu, 1), count = U16(pdu, 3), bytes = pdu[5];
        if (count < 1 || count > 1968 || bytes != (count + 7) / 8 || pdu.Length != 6 + bytes) return Exception(15, 3);
        if (OutOfRange(address, count)) return Exception(15, 2);
        for (var i = 0; i < count; i++) _store.Coils[address + i] = (pdu[6 + i / 8] & (1 << (i % 8))) != 0;
        return pdu[..5].ToArray();
    }

    private byte[] WriteMultipleRegisters(ReadOnlySpan<byte> pdu)
    {
        if (pdu.Length < 6) return Exception(16, 3);
        int address = U16(pdu, 1), count = U16(pdu, 3), bytes = pdu[5];
        if (count < 1 || count > 123 || bytes != count * 2 || pdu.Length != 6 + bytes) return Exception(16, 3);
        if (OutOfRange(address, count)) return Exception(16, 2);
        for (var i = 0; i < count; i++) _store.HoldingRegisters[address + i] = U16(pdu, 6 + i * 2);
        return pdu[..5].ToArray();
    }

    private byte[] MaskWrite(ReadOnlySpan<byte> pdu)
    {
        if (pdu.Length != 7) return Exception(22, 3);
        int address = U16(pdu, 1);
        if (OutOfRange(address, 1)) return Exception(22, 2);
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
        if (OutOfRange(ra, rc) || OutOfRange(wa, wc)) return Exception(23, 2);
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
        var r = new byte[3 + id.Length];
        r[0] = 17;
        r[1] = (byte)(id.Length + 1);
        id.CopyTo(r, 2);
        r[^1] = 0xFF; // run indicator: ON
        return r;
    }

    private static byte[] Diagnostics(ReadOnlySpan<byte> pdu)
    {
        if (pdu.Length != 5) return Exception(8, 3);
        var sub = U16(pdu, 1);
        return sub switch
        {
            0 => pdu.ToArray(),                          // Return Query Data
            10 => pdu.ToArray(),                         // Clear counters
            11 or 12 or 13 or 14 or 15 or 16 or 17 or 18 => [8, pdu[1], pdu[2], 0, 0],
            _ => Exception(8, 1),
        };
    }
}

public enum SimulatorFraming { Mbap, Rtu, Ascii }

/// <summary>TCP slave for tests and demos: MBAP (Modbus TCP) or RTU/ASCII frames over TCP.</summary>
public sealed class ModbusTcpSimulator : IDisposable
{
    private readonly TcpListener _listener;
    private readonly SimulatorFraming _framing;
    private readonly ConcurrentDictionary<Socket, byte> _clients = new();
    private volatile bool _stopped;

    public ModbusTcpSimulator(IPAddress bind, int port, SimulatorFraming framing, ModbusRequestHandler handler)
    {
        _listener = new TcpListener(bind, port);
        _framing = framing;
        Handler = handler;
    }

    public ModbusRequestHandler Handler { get; }

    /// <summary>Answer only these slave IDs; null = answer every ID.</summary>
    public HashSet<byte>? UnitIds { get; set; }

    public int Port { get; private set; }

    /// <summary>When set, clients must use TLS (Modbus/TCP Security).</summary>
    public X509Certificate2? ServerCertificate { get; set; }

    public bool RequireClientCertificate { get; set; }

    /// <summary>Subject of the last client certificate seen (TLS only).</summary>
    public string? LastClientCertificateSubject { get; private set; }

    public event Action<TrafficEntry>? Traffic;

    public void Start()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        new Thread(AcceptLoop) { IsBackground = true, Name = "Simulator accept" }.Start();
    }

    private void AcceptLoop()
    {
        while (!_stopped)
        {
            Socket socket;
            try
            {
                socket = _listener.AcceptSocket();
            }
            catch (Exception) when (_stopped)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }
            _clients[socket] = 0;
            new Thread(() => Serve(socket)) { IsBackground = true, Name = "Simulator client" }.Start();
        }
    }

    private bool Accepts(byte unit) => UnitIds is null || UnitIds.Contains(unit);

    private void Serve(Socket socket)
    {
        IByteChannel? channel = null;
        try
        {
            channel = ServerCertificate is null ? new TcpChannel(socket, socket.RemoteEndPoint?.ToString() ?? "client") : AcceptTls(socket);
            while (!_stopped)
            {
                switch (_framing)
                {
                    case SimulatorFraming.Mbap: ServeMbap(channel); break;
                    case SimulatorFraming.Rtu: ServeRtu(channel); break;
                    default: ServeAscii(channel); break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or ModbusFrameException or ObjectDisposedException or AuthenticationException)
        {
            // Client went away, failed the TLS handshake or sent garbage we cannot resynchronize on.
        }
        finally
        {
            channel?.Dispose();
            _clients.TryRemove(socket, out _);
        }
    }

    private StreamChannel AcceptTls(Socket socket)
    {
        var ssl = new SslStream(new NetworkStream(socket, ownsSocket: true), leaveInnerStreamOpen: false,
            (_, cert, _, _) => !RequireClientCertificate || cert is not null);
        ssl.AuthenticateAsServer(new SslServerAuthenticationOptions
        {
            ServerCertificate = ServerCertificate,
            ClientCertificateRequired = RequireClientCertificate,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        });
        LastClientCertificateSubject = ssl.RemoteCertificate?.Subject;
        return new StreamChannel(ssl, "tls client");
    }

    private void ServeMbap(IByteChannel channel)
    {
        var header = channel.ReadExact(7, DateTime.MaxValue);
        var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
        if (length < 2 || length > 254) throw new ModbusFrameException("bad MBAP length");
        var pdu = channel.ReadExact(length - 1, DateTime.MaxValue);
        Log(TrafficDirection.Rx, [.. header, .. pdu]);
        if (!Accepts(header[6])) return;
        var tid = BinaryPrimitives.ReadUInt16BigEndian(header);
        var response = MbapFramer.Encode(tid, header[6], Handler.Handle(pdu));
        Log(TrafficDirection.Tx, response);
        channel.Write(response);
    }

    private void ServeRtu(IByteChannel channel)
    {
        var frame = new List<byte>(channel.ReadExact(2, DateTime.MaxValue));
        int rest;
        switch (frame[1])
        {
            case 1: case 2: case 3: case 4: case 5: case 6: case 8: rest = 6; break;
            case 17: rest = 2; break;
            case 22: rest = 8; break;
            case 15: case 16:
                frame.AddRange(channel.ReadExact(5, DateTime.MaxValue));
                rest = frame[^1] + 2;
                break;
            case 23:
                frame.AddRange(channel.ReadExact(9, DateTime.MaxValue));
                rest = frame[^1] + 2;
                break;
            default:
                throw new ModbusFrameException("unknown function");
        }
        frame.AddRange(channel.ReadExact(rest, DateTime.MaxValue));
        var bytes = frame.ToArray();
        Log(TrafficDirection.Rx, bytes);
        if (!RtuFramer.CheckCrc(bytes) || !Accepts(bytes[0])) return;
        var responsePdu = Handler.Handle(bytes.AsSpan(1, bytes.Length - 3));
        if (bytes[0] == 0) return; // broadcast: no response
        var response = RtuFramer.Encode(bytes[0], responsePdu);
        Log(TrafficDirection.Tx, response);
        channel.Write(response);
    }

    private void ServeAscii(IByteChannel channel)
    {
        var frame = AsciiFramer.ReadFrame(channel, DateTime.MaxValue);
        Log(TrafficDirection.Rx, frame, ascii: true);
        byte[] raw;
        try { raw = AsciiFramer.Decode(frame); }
        catch (ModbusFrameException) { return; }
        if (!Accepts(raw[0])) return;
        var responsePdu = Handler.Handle(raw.AsSpan(1));
        if (raw[0] == 0) return;
        var response = AsciiFramer.Encode(raw[0], responsePdu);
        Log(TrafficDirection.Tx, response, ascii: true);
        channel.Write(response);
    }

    private void Log(TrafficDirection direction, byte[] data, bool ascii = false) =>
        Traffic?.Invoke(new TrafficEntry(DateTime.Now, direction, data, ascii));

    public void Dispose()
    {
        _stopped = true;
        _listener.Stop();
        foreach (var s in _clients.Keys)
        {
            try { s.Dispose(); } catch { /* closing */ }
        }
    }
}
