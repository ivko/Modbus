namespace ModbusMaster.Core.Channels;

/// <summary>A byte pipe with timed reads. All calls come from one thread at a time.</summary>
public interface IByteChannel : IDisposable
{
    string Description { get; }

    /// <summary>True when a read timeout leaves the channel unusable (TLS), so the client must reopen it.</summary>
    bool FaultsOnTimeout { get; }

    void Write(byte[] data);

    /// <summary>Reads 1..count bytes. Throws <see cref="TimeoutException"/> when nothing arrives in time
    /// and <see cref="IOException"/> when the connection is closed or broken.</summary>
    int Read(byte[] buffer, int offset, int count, TimeSpan timeout);

    /// <summary>Drops any bytes already received but not read.</summary>
    void DiscardInput();
}

public static class ChannelExtensions
{
    public static byte[] ReadExact(this IByteChannel channel, int count, DateTime deadlineUtc)
    {
        var buffer = new byte[count];
        var got = 0;
        while (got < count)
        {
            var remaining = deadlineUtc - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) throw new TimeoutException();
            var n = channel.Read(buffer, got, count - got, remaining);
            if (n <= 0) throw new IOException("The connection was closed.");
            got += n;
        }
        return buffer;
    }

    internal static int ToTimeoutMs(TimeSpan timeout)
    {
        if (timeout.TotalMilliseconds >= int.MaxValue) return 0; // 0 = infinite for sockets
        return Math.Max(1, (int)Math.Ceiling(timeout.TotalMilliseconds));
    }
}
