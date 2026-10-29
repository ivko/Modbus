using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using ModbusMaster.Core.Channels;

namespace ModbusMaster.Core.Server;

/// <summary>Common shape of the TCP, UDP and serial slave servers.</summary>
public interface IModbusServer : IDisposable
{
    string Description { get; }
    event Action<TrafficEntry>? Traffic;
    void Start();
}

/// <summary>
/// TCP slave: Modbus TCP (MBAP), RTU or ASCII frames over TCP, optionally inside TLS (Modbus/TCP Security).
/// Each client connection is served on its own thread.
/// </summary>
public sealed class ModbusTcpSimulator : IModbusServer
{
    private readonly TcpListener _listener;
    private readonly SimulatorFraming _framing;
    private readonly ConcurrentDictionary<Socket, byte> _clients = new();
    private volatile bool _stopped;

    public ModbusTcpSimulator(IPAddress bind, int port, SimulatorFraming framing, IServerHandler handler)
    {
        _listener = new TcpListener(bind, port);
        if (bind.Equals(IPAddress.IPv6Any)) _listener.Server.DualMode = true;
        _framing = framing;
        Handler = handler;
    }

    public IServerHandler Handler { get; }

    /// <summary>Answer only these slave IDs; null = let the handler decide.</summary>
    public HashSet<byte>? UnitIds { get; set; }

    public int Port { get; private set; }

    /// <summary>When set, clients must use TLS (Modbus/TCP Security).</summary>
    public X509Certificate2? ServerCertificate { get; set; }

    public bool RequireClientCertificate { get; set; }

    /// <summary>When set, client certificates must chain to this CA.</summary>
    public X509Certificate2? TrustedClientCa { get; set; }

    /// <summary>Subject of the last client certificate seen (TLS only).</summary>
    public string? LastClientCertificateSubject { get; private set; }

    public string Description => $"{(ServerCertificate is null ? "TCP" : "TLS")} {_framing} port {Port}";

    public int ClientCount => _clients.Count;

    public event Action<TrafficEntry>? Traffic;

    public void Start()
    {
        try
        {
            _listener.Start();
        }
        catch (SocketException ex)
        {
            throw new ModbusConnectionException($"Cannot listen on port {((IPEndPoint)_listener.LocalEndpoint).Port}: {ex.Message}", 13, ex);
        }
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        new Thread(AcceptLoop) { IsBackground = true, Name = "Modbus TCP server" }.Start();
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
            socket.NoDelay = true;
            _clients[socket] = 0;
            new Thread(() => Serve(socket)) { IsBackground = true, Name = "Modbus TCP client" }.Start();
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
                ServerFrames.ServeOne(channel, _framing, Handler, Accepts, Log, DateTime.MaxValue, TimeSpan.FromSeconds(2));
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or ModbusFrameException or ObjectDisposedException or AuthenticationException)
        {
            // Client went away, failed the TLS handshake or sent garbage we cannot resynchronize on.
        }
        finally
        {
            channel?.Dispose();
            if (channel is null) socket.Dispose();
            _clients.TryRemove(socket, out _);
        }
    }

    private StreamChannel AcceptTls(Socket socket)
    {
        var ssl = new SslStream(new NetworkStream(socket, ownsSocket: true), leaveInnerStreamOpen: false, ValidateClient);
        ssl.AuthenticateAsServer(new SslServerAuthenticationOptions
        {
            ServerCertificate = ServerCertificate,
            ClientCertificateRequired = RequireClientCertificate,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        });
        LastClientCertificateSubject = ssl.RemoteCertificate?.Subject;
        return new StreamChannel(ssl, "tls client");
    }

    private bool ValidateClient(object sender, X509Certificate? cert, X509Chain? chain, SslPolicyErrors errors)
    {
        if (cert is null) return !RequireClientCertificate;
        if (TrustedClientCa is null) return true; // simulator: accept any client certificate
        using var c = new X509Chain();
        c.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        c.ChainPolicy.CustomTrustStore.Add(TrustedClientCa);
        c.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        using var client = new X509Certificate2(cert);
        return c.Build(client);
    }

    private void Log(TrafficEntry e) => Traffic?.Invoke(e);

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

/// <summary>UDP slave: one request per datagram (Modbus UDP, or RTU/ASCII over UDP).</summary>
public sealed class ModbusUdpServer : IModbusServer
{
    private readonly Socket _socket;
    private readonly IPEndPoint _bind;
    private readonly SimulatorFraming _framing;
    private volatile bool _stopped;

    public ModbusUdpServer(IPAddress bind, int port, SimulatorFraming framing, IServerHandler handler)
    {
        _bind = new IPEndPoint(bind, port);
        _framing = framing;
        Handler = handler;
        _socket = new Socket(bind.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        if (bind.Equals(IPAddress.IPv6Any)) _socket.DualMode = true;
    }

    public IServerHandler Handler { get; }
    public int Port { get; private set; }
    public string Description => $"UDP {_framing} port {Port}";
    public event Action<TrafficEntry>? Traffic;

    public void Start()
    {
        try
        {
            _socket.Bind(_bind);
        }
        catch (SocketException ex)
        {
            throw new ModbusConnectionException($"Cannot listen on UDP port {_bind.Port}: {ex.Message}", 17, ex);
        }
        Port = ((IPEndPoint)_socket.LocalEndPoint!).Port;
        new Thread(Loop) { IsBackground = true, Name = "Modbus UDP server" }.Start();
    }

    private void Loop()
    {
        var buffer = new byte[65535];
        while (!_stopped)
        {
            EndPoint remote = new IPEndPoint(_bind.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
            int n;
            try
            {
                n = _socket.ReceiveFrom(buffer, ref remote);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                continue; // ICMP from an earlier reply; ignore
            }
            catch (Exception) when (_stopped)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }
            var frame = buffer[..n];
            var target = remote;
            ServerFrames.Respond(frame, _framing, Handler, _ => true, e => Traffic?.Invoke(e), reply =>
            {
                try { _socket.SendTo(reply, target); } catch (SocketException) { /* client gone */ }
            });
        }
    }

    public void Dispose()
    {
        _stopped = true;
        _socket.Dispose();
    }
}

/// <summary>Serial slave (RTU or ASCII) on one COM port.</summary>
public sealed class ModbusSerialServer : IModbusServer
{
    private readonly ConnectionSettings _settings;
    private SerialChannel? _channel;
    private volatile bool _stopped;
    private Thread? _thread;

    public ModbusSerialServer(ConnectionSettings settings, IServerHandler handler)
    {
        _settings = settings.Clone();
        Handler = handler;
    }

    public IServerHandler Handler { get; }
    public string Description => $"{_settings.SerialPort} {_settings.Mode.ToString().ToUpperInvariant()}";
    public event Action<TrafficEntry>? Traffic;

    public void Start()
    {
        _channel = SerialChannel.Open(_settings);
        _thread = new Thread(Loop) { IsBackground = true, Name = "Modbus serial server" };
        _thread.Start();
    }

    private void Loop()
    {
        var framing = _settings.Mode == SerialMode.Ascii ? SimulatorFraming.Ascii : SimulatorFraming.Rtu;
        // A frame must arrive in one piece: allow ~3 character times per byte for 260 bytes, at least 100 ms.
        var frameTimeout = TimeSpan.FromMilliseconds(Math.Max(100, 260 * 33_000.0 / _settings.BaudRate));
        while (!_stopped)
        {
            try
            {
                ServerFrames.ServeOne(_channel!, framing, Handler, _ => true, e => Traffic?.Invoke(e),
                    DateTime.UtcNow.AddMilliseconds(500), frameTimeout);
            }
            catch (TimeoutException)
            {
                // Started a frame but it never completed: count it and resynchronize on the next silence.
                Handler.OnFrame(valid: false);
                _channel!.DiscardInput();
            }
            catch (IOException)
            {
                if (_stopped) return;
                Thread.Sleep(500); // converter unplugged; keep trying so it recovers when plugged back in
            }
        }
    }

    public void Dispose()
    {
        _stopped = true;
        _channel?.Dispose();
        _thread?.Join(TimeSpan.FromSeconds(1));
    }
}
