using System.IO.Ports;
using System.Net.Sockets;
using System.Text.Json.Serialization;

namespace ModbusMaster.Core;

/// <summary>Connection types. Numbers match the automation property Application.Connection.</summary>
public enum ConnectionKind
{
    Serial = 0,
    Tcp = 1,
    Udp = 2,
    RtuAsciiOverTcp = 3,
    RtuAsciiOverUdp = 4,
    TcpSecurity = 5,
}

/// <summary>Serial framing. Numbers match the automation property Application.Mode.</summary>
public enum SerialMode
{
    Rtu = 0,
    Ascii = 1,
}

public sealed class ConnectionSettings
{
    public static readonly int[] StandardBaudRates =
        [300, 600, 1200, 2400, 4800, 9600, 14400, 19200, 38400, 56000, 57600, 115200, 128000, 153600, 230400, 256000, 460800, 921600];

    public ConnectionKind Kind { get; set; } = ConnectionKind.Tcp;

    // Network
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 502;
    /// <summary>4 or 6.</summary>
    public int IpVersion { get; set; } = 4;

    // Serial
    public string SerialPort { get; set; } = "COM1";
    public int BaudRate { get; set; } = 9600;
    public int DataBits { get; set; } = 8;
    public Parity Parity { get; set; } = Parity.Even;
    public StopBits StopBits { get; set; } = StopBits.One;
    public bool RemoveEcho { get; set; }
    public bool DtrEnable { get; set; }
    public bool RtsEnable { get; set; }

    /// <summary>RTU or ASCII; used by Serial and by RTU/ASCII over TCP/UDP.</summary>
    public SerialMode Mode { get; set; } = SerialMode.Rtu;

    // Timing
    public int ConnectTimeoutMs { get; set; } = 1000;
    public int ResponseTimeoutMs { get; set; } = 1000;
    public int DelayBetweenPollsMs { get; set; } = 20;

    // Modbus/TCP Security
    public string? ClientCertificatePath { get; set; }
    public string? ClientCertificatePassword { get; set; }
    /// <summary>Private key file (PEM) when the client certificate is a PEM/CRT file instead of PFX.</summary>
    public string? ClientKeyPath { get; set; }
    public string? TrustedCaPath { get; set; }
    public bool AllowUntrustedServer { get; set; }
    public string? TlsServerName { get; set; }

    [JsonIgnore]
    public AddressFamily AddressFamily => IpVersion == 6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork;

    [JsonIgnore]
    public bool IsSerialFraming => Kind is ConnectionKind.Serial or ConnectionKind.RtuAsciiOverTcp or ConnectionKind.RtuAsciiOverUdp;

    public ConnectionSettings Clone() => (ConnectionSettings)MemberwiseClone();

    public string Describe() => Kind switch
    {
        ConnectionKind.Serial => $"{SerialPort}: {BaudRate}-{DataBits}-{ParityLetter}-{(StopBits == StopBits.Two ? 2 : 1)} {Mode.ToString().ToUpperInvariant()}",
        ConnectionKind.Tcp => $"Modbus TCP {Host}:{Port}",
        ConnectionKind.Udp => $"Modbus UDP {Host}:{Port}",
        ConnectionKind.RtuAsciiOverTcp => $"{Mode.ToString().ToUpperInvariant()} over TCP {Host}:{Port}",
        ConnectionKind.RtuAsciiOverUdp => $"{Mode.ToString().ToUpperInvariant()} over UDP {Host}:{Port}",
        ConnectionKind.TcpSecurity => $"Modbus/TCP Security {Host}:{Port}",
        _ => Kind.ToString(),
    };

    private char ParityLetter => Parity switch
    {
        Parity.Even => 'E',
        Parity.Odd => 'O',
        Parity.Mark => 'M',
        Parity.Space => 'S',
        _ => 'N',
    };
}
