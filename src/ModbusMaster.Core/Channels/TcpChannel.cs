using System.Net;
using System.Net.Sockets;

namespace ModbusMaster.Core.Channels;

public sealed class TcpChannel : IByteChannel
{
    private readonly Socket _socket;

    public TcpChannel(Socket socket, string description)
    {
        _socket = socket;
        Description = description;
    }

    public string Description { get; }
    public bool FaultsOnTimeout => false;
    public Socket Socket => _socket;

    public static TcpChannel Connect(string host, int port, TimeSpan timeout, AddressFamily family = AddressFamily.Unspecified)
    {
        var socket = ConnectSocket(host, port, timeout, family);
        return new TcpChannel(socket, $"{host}:{port}");
    }

    internal static Socket ConnectSocket(string host, int port, TimeSpan timeout, AddressFamily family)
    {
        IPAddress address;
        try
        {
            address = ResolveAddress(host, family);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            throw new ModbusConnectionException($"Invalid address or server not found: {host}. {ex.Message}", 21, ex);
        }

        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            socket.ConnectAsync(new IPEndPoint(address, port), cts.Token).AsTask().GetAwaiter().GetResult();
            return socket;
        }
        catch (OperationCanceledException)
        {
            socket.Dispose();
            throw new ModbusConnectionException(
                $"Connection timeout. No answer from {host}:{port} within {timeout.TotalMilliseconds:0} ms. Check the IP address and the port (default 502).", 14);
        }
        catch (SocketException ex)
        {
            socket.Dispose();
            throw new ModbusConnectionException($"Connection to {host}:{port} failed: {ex.Message}", 13, ex);
        }
    }

    internal static IPAddress ResolveAddress(string host, AddressFamily family)
    {
        if (IPAddress.TryParse(host, out var ip)) return ip;
        var all = Dns.GetHostAddresses(host);
        var match = all.FirstOrDefault(a => family == AddressFamily.Unspecified
                                                ? a.AddressFamily == AddressFamily.InterNetwork
                                                : a.AddressFamily == family)
                    ?? all.FirstOrDefault();
        return match ?? throw new SocketException((int)SocketError.HostNotFound);
    }

    public void Write(byte[] data)
    {
        try
        {
            var sent = 0;
            while (sent < data.Length) sent += _socket.Send(data, sent, data.Length - sent, SocketFlags.None);
        }
        catch (SocketException ex)
        {
            throw new IOException($"Send failed: {ex.Message}", ex);
        }
        catch (ObjectDisposedException ex)
        {
            throw new IOException("The connection is closed.", ex);
        }
    }

    public int Read(byte[] buffer, int offset, int count, TimeSpan timeout)
    {
        try
        {
            _socket.ReceiveTimeout = ChannelExtensions.ToTimeoutMs(timeout);
            var n = _socket.Receive(buffer, offset, count, SocketFlags.None);
            if (n == 0) throw new IOException("Connection closed by server.");
            return n;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
        {
            throw new TimeoutException();
        }
        catch (SocketException ex)
        {
            throw new IOException($"Receive failed: {ex.Message}", ex);
        }
        catch (ObjectDisposedException ex)
        {
            throw new IOException("The connection is closed.", ex);
        }
    }

    public void DiscardInput()
    {
        try
        {
            while (_socket.Available > 0)
            {
                var tmp = new byte[_socket.Available];
                _socket.Receive(tmp);
            }
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            // The next read reports the broken connection.
        }
    }

    public void Dispose()
    {
        try { _socket.Shutdown(SocketShutdown.Both); } catch { /* already closed */ }
        _socket.Dispose();
    }
}
