using System.Net;
using System.Net.Sockets;

namespace Modbus.Core.Channels;

/// <summary>Connected UDP socket. Datagrams are buffered so framers can read byte ranges.</summary>
public sealed class UdpChannel : IByteChannel
{
    private readonly Socket _socket;
    private byte[] _pending = [];
    private int _position;

    private UdpChannel(Socket socket, string description)
    {
        _socket = socket;
        Description = description;
    }

    public string Description { get; }
    public bool FaultsOnTimeout => false;

    public static UdpChannel Connect(string host, int port, AddressFamily family = AddressFamily.Unspecified)
    {
        IPAddress address;
        try
        {
            address = TcpChannel.ResolveAddress(host, family);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            throw new ModbusConnectionException($"Invalid address or server not found: {host}. {ex.Message}", 21, ex);
        }

        var socket = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.Connect(new IPEndPoint(address, port));
        }
        catch (SocketException ex)
        {
            socket.Dispose();
            throw new ModbusConnectionException($"UDP socket error for {host}:{port}: {ex.Message}", 17, ex);
        }
        return new UdpChannel(socket, $"udp://{host}:{port}");
    }

    public void Write(byte[] data)
    {
        try
        {
            _socket.Send(data);
        }
        catch (SocketException ex)
        {
            throw new IOException($"Send failed: {ex.Message}", ex);
        }
    }

    public int Read(byte[] buffer, int offset, int count, TimeSpan timeout)
    {
        if (_position >= _pending.Length)
        {
            try
            {
                _socket.ReceiveTimeout = ChannelExtensions.ToTimeoutMs(timeout);
                var datagram = new byte[65535];
                var n = _socket.Receive(datagram);
                _pending = datagram[..n];
                _position = 0;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
            {
                throw new TimeoutException();
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                // ICMP port unreachable: nobody listens on the remote port. Treat as no response.
                throw new TimeoutException();
            }
            catch (SocketException ex)
            {
                throw new IOException($"Receive failed: {ex.Message}", ex);
            }
        }

        var take = Math.Min(count, _pending.Length - _position);
        Array.Copy(_pending, _position, buffer, offset, take);
        _position += take;
        return take;
    }

    public void DiscardInput()
    {
        _pending = [];
        _position = 0;
        try
        {
            var tmp = new byte[65535];
            while (_socket.Available > 0) _socket.Receive(tmp);
        }
        catch (SocketException)
        {
            // Ignore; the next read reports real errors.
        }
    }

    public void Dispose() => _socket.Dispose();
}
