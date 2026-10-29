using Modbus.Core.Channels;
using Modbus.Core.Framing;

namespace Modbus.Core;

public static class ModbusConnection
{
    /// <summary>Creates a client for the settings. The connection opens on <see cref="ModbusClient.Open"/> or the first request.</summary>
    public static ModbusClient Create(ConnectionSettings settings)
    {
        var s = settings.Clone();
        IFramer framer = s.Kind switch
        {
            ConnectionKind.Tcp or ConnectionKind.Udp or ConnectionKind.TcpSecurity => new MbapFramer(),
            _ => s.Mode == SerialMode.Ascii ? new AsciiFramer() : new RtuFramer(),
        };

        Func<IByteChannel> open = s.Kind switch
        {
            ConnectionKind.Serial => () => SerialChannel.Open(s),
            ConnectionKind.Tcp or ConnectionKind.RtuAsciiOverTcp =>
                () => TcpChannel.Connect(s.Host, s.Port, TimeSpan.FromMilliseconds(s.ConnectTimeoutMs), s.AddressFamily),
            ConnectionKind.Udp or ConnectionKind.RtuAsciiOverUdp => () => UdpChannel.Connect(s.Host, s.Port, s.AddressFamily),
            ConnectionKind.TcpSecurity => () => TlsChannel.Connect(s),
            _ => throw new ArgumentOutOfRangeException(nameof(settings), $"Unknown connection type {s.Kind}."),
        };

        return new ModbusClient(open, framer)
        {
            ResponseTimeout = TimeSpan.FromMilliseconds(s.ResponseTimeoutMs),
            DelayBetweenPolls = TimeSpan.FromMilliseconds(s.DelayBetweenPollsMs),
            Description = s.Describe(),
        };
    }
}
