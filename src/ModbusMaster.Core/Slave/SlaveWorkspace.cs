using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModbusMaster.Core.Polling;
using ModbusMaster.Core.Server;

namespace ModbusMaster.Core.Slave;

/// <summary>How the slave listens. Reuses <see cref="ConnectionSettings"/> for serial, port and certificate fields.</summary>
public sealed class ServerSettings
{
    /// <summary>Kind, serial settings, Port, IpVersion and the certificate fields. Host is the address to listen on.</summary>
    public ConnectionSettings Connection { get; set; } = new() { Host = "127.0.0.1", Port = 502 };

    /// <summary>Listen on every network interface instead of <see cref="ConnectionSettings.Host"/>.</summary>
    public bool AnyAddress { get; set; } = true;

    public bool IgnoreUnitId { get; set; }

    public bool StrictAddressing { get; set; } = true;

    /// <summary>Modbus/TCP Security: clients must present a certificate.</summary>
    public bool RequireClientCertificate { get; set; } = true;

    public ServerSettings Clone() => new()
    {
        Connection = Connection.Clone(), AnyAddress = AnyAddress, IgnoreUnitId = IgnoreUnitId,
        StrictAddressing = StrictAddressing, RequireClientCertificate = RequireClientCertificate,
    };

    public string Describe()
    {
        var c = Connection;
        var bind = AnyAddress ? (c.IpVersion == 6 ? "[::]" : "0.0.0.0") : c.Host;
        return c.Kind switch
        {
            ConnectionKind.Serial => c.Describe(),
            ConnectionKind.Tcp => $"Modbus TCP server {bind}:{c.Port}",
            ConnectionKind.Udp => $"Modbus UDP server {bind}:{c.Port}",
            ConnectionKind.RtuAsciiOverTcp => $"{c.Mode.ToString().ToUpperInvariant()} over TCP server {bind}:{c.Port}",
            ConnectionKind.RtuAsciiOverUdp => $"{c.Mode.ToString().ToUpperInvariant()} over UDP server {bind}:{c.Port}",
            _ => $"Modbus/TCP Security server {bind}:{c.Port}",
        };
    }

    /// <summary>Creates the server for these settings (not started).</summary>
    public IModbusServer CreateServer(IServerHandler handler)
    {
        var c = Connection;
        if (c.Kind == ConnectionKind.Serial) return new ModbusSerialServer(c, handler);

        IPAddress bind;
        if (AnyAddress) bind = c.IpVersion == 6 ? IPAddress.IPv6Any : IPAddress.Any;
        else if (!IPAddress.TryParse(c.Host, out bind!))
            throw new ModbusConnectionException($"'{c.Host}' is not an IP address of this computer.", 21);

        var serial = c.Mode == SerialMode.Ascii ? SimulatorFraming.Ascii : SimulatorFraming.Rtu;
        switch (c.Kind)
        {
            case ConnectionKind.Tcp: return new ModbusTcpSimulator(bind, c.Port, SimulatorFraming.Mbap, handler);
            case ConnectionKind.RtuAsciiOverTcp: return new ModbusTcpSimulator(bind, c.Port, serial, handler);
            case ConnectionKind.Udp: return new ModbusUdpServer(bind, c.Port, SimulatorFraming.Mbap, handler);
            case ConnectionKind.RtuAsciiOverUdp: return new ModbusUdpServer(bind, c.Port, serial, handler);
        }

        if (string.IsNullOrWhiteSpace(c.ClientCertificatePath))
            throw new ModbusConnectionException("Modbus/TCP Security needs a server certificate (PFX, or PEM with key).", 13);
        var server = new ModbusTcpSimulator(bind, c.Port, SimulatorFraming.Mbap, handler)
        {
            ServerCertificate = Channels.TlsChannel.LoadCertificate(c.ClientCertificatePath, c.ClientKeyPath, c.ClientCertificatePassword),
            RequireClientCertificate = RequireClientCertificate,
        };
        if (!string.IsNullOrWhiteSpace(c.TrustedCaPath)) server.TrustedClientCa = X509CertificateLoader.LoadCertificateFromFile(c.TrustedCaPath);
        return server;
    }
}

/// <summary>Per slave ID settings saved in the workspace.</summary>
public sealed class SlaveDeviceSettings
{
    public byte Id { get; set; }
    public ErrorSimulation Errors { get; set; } = new();
    public DeviceIdentification Identification { get; set; } = new();
    public string ServerId { get; set; } = "";
}

/// <summary>A saved slave session (*.mms): server settings, windows, per-slave settings and optionally the data.</summary>
public sealed class SlaveWorkspace
{
    public int Version { get; set; } = 1;
    public ServerSettings Server { get; set; } = new();
    public List<PollDocument> Windows { get; set; } = [];
    public List<SlaveDeviceSettings> Devices { get; set; } = [];

    /// <summary>Values of every window when saved, keyed by window index.</summary>
    public Dictionary<int, ushort[]> Data { get; set; } = [];

    public void Save(string path)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Workspace.JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }

    public static SlaveWorkspace Load(string path)
    {
        var ws = JsonSerializer.Deserialize<SlaveWorkspace>(File.ReadAllText(path), Workspace.JsonOptions)
                 ?? throw new InvalidDataException("Empty workspace file.");
        if (ws.Version > 1) throw new InvalidDataException("This file was saved by a newer version of Modbus Slave.");
        return ws;
    }

    /// <summary>Captures the engine state.</summary>
    public static SlaveWorkspace From(SlaveEngine engine, ServerSettings server, IEnumerable<PollDocument> windows)
    {
        var ws = new SlaveWorkspace { Server = server.Clone() };
        ws.Server.Connection.ClientCertificatePassword = null;
        var list = windows.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            ws.Windows.Add(list[i].CloneDefinitionOnly());
            ws.Data[i] = engine.Read(list[i]);
        }
        foreach (var d in engine.Devices.Where(d => list.Any(w => w.Definition.SlaveId == d.Id)))
            ws.Devices.Add(new SlaveDeviceSettings { Id = d.Id, Errors = d.Errors.Clone(), Identification = d.Handler.Identification.Clone(), ServerId = d.Handler.ServerId });
        return ws;
    }

    /// <summary>Restores device settings and data into the engine. The caller adds the windows first.</summary>
    public void ApplyTo(SlaveEngine engine, IReadOnlyList<PollDocument> addedWindows)
    {
        engine.IgnoreUnitId = Server.IgnoreUnitId;
        engine.StrictAddressing = Server.StrictAddressing;
        foreach (var s in Devices)
        {
            var d = engine.Device(s.Id);
            d.Errors = s.Errors.Clone();
            d.Handler.Identification = s.Identification.Clone();
            if (s.ServerId.Length > 0) d.Handler.ServerId = s.ServerId;
        }
        for (var i = 0; i < addedWindows.Count; i++)
            if (Data.TryGetValue(i, out var values)) engine.Write(addedWindows[i], 0, values);
    }

    [JsonIgnore]
    public static string FileFilter => "Modbus Slave workspace (*.mms)|*.mms|All files (*.*)|*.*";
}
