using System.Net.Sockets;

namespace ModbusMaster.Core.Channels;

/// <summary>Any readable/writable stream with timeouts (used for the TLS side of the simulator).</summary>
public sealed class StreamChannel : IByteChannel
{
    private readonly Stream _stream;

    public StreamChannel(Stream stream, string description)
    {
        _stream = stream;
        Description = description;
    }

    public string Description { get; }
    public bool FaultsOnTimeout => true;

    public void Write(byte[] data)
    {
        try
        {
            _stream.Write(data);
            _stream.Flush();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            throw new IOException($"Send failed: {ex.Message}", ex);
        }
    }

    public int Read(byte[] buffer, int offset, int count, TimeSpan timeout)
    {
        try
        {
            var ms = ChannelExtensions.ToTimeoutMs(timeout);
            _stream.ReadTimeout = ms == 0 ? Timeout.Infinite : ms;
            var n = _stream.Read(buffer, offset, count);
            if (n == 0) throw new IOException("The connection was closed.");
            return n;
        }
        catch (IOException ex) when (ex.InnerException is SocketException { SocketErrorCode: SocketError.TimedOut })
        {
            throw new TimeoutException();
        }
        catch (ObjectDisposedException ex)
        {
            throw new IOException("The connection is closed.", ex);
        }
    }

    public void DiscardInput() { }

    public void Dispose() => _stream.Dispose();
}
