using System.Globalization;
using System.IO.Ports;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModbusMaster.Core;
using ModbusMaster.Core.Channels;
using ModbusMaster.Core.Data;
using ModbusMaster.Core.Polling;
using ModbusMaster.Core.Protocol;
using ModbusMaster.Core.Server;

namespace ModbusMaster.Cli;

/// <summary>
/// mbm - Modbus Master command line for scripts.
/// Exit codes: 0 success, 1 Modbus exception from the slave, 2 communication error, 3 usage error.
/// </summary>
public static class Program
{
    private const int ExitOk = 0;
    private const int ExitSlaveException = 1;
    private const int ExitCommunication = 2;
    private const int ExitUsage = 3;

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length == 0 || args[0] is "help" or "-h" or "--help" or "/?")
        {
            Console.WriteLine(Help.Text);
            return args.Length == 0 ? ExitUsage : ExitOk;
        }

        Options o;
        try
        {
            o = Options.Parse(args.Skip(1));
        }
        catch (FormatException ex)
        {
            return Usage(ex.Message);
        }

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "read" => Read(o),
                "write" => Write(o),
                "mask" => Mask(o),
                "rw" => ReadWrite(o),
                "id" => ReportId(o),
                "diag" => Diagnostics(o),
                "raw" => Raw(o),
                "scan" => Scan(o),
                "scan-addresses" => ScanAddresses(o),
                "ports" => Ports(o),
                "serve" => Serve(o),
                "version" => Version(),
                _ => Usage($"Unknown command '{args[0]}'. Run 'mbm help'."),
            };
        }
        catch (ModbusSlaveException ex)
        {
            return Fail(o, ExitSlaveException, ex.Message, (int)ex.Result);
        }
        catch (ModbusCommunicationException ex)
        {
            return Fail(o, ExitCommunication, ex.Message, (int)ex.Result);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException or KeyNotFoundException)
        {
            return Usage(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(o, ExitCommunication, ex.Message, (int)ModbusResult.PortNotOpen);
        }
    }

    // ---- commands -----------------------------------------------------------------------

    private static int Read(Options o)
    {
        var function = ReadFunction(o);
        var unit = o.Byte("unit", 1);
        var address = o.UShort("address", 0);
        var isBits = FunctionCodes.IsBitFunction(function);
        var format = isBits ? DisplayFormat.Unsigned : FormatOption(o);
        var perValue = format.Registers();
        var count = o.Int("count", isBits ? 1 : perValue);
        using var client = CreateClient(o);

        var interval = o.Int("interval", 1000);
        var watch = o.Flag("watch");
        var csvPath = o.Value("csv");
        var first = true;
        do
        {
            var started = DateTime.UtcNow;
            var raw = client.Read(unit, function, address, count);
            var now = DateTime.Now;
            if (csvPath is not null) AppendCsv(csvPath, now, address, raw, format, isBits, ref first);
            Print(o, unit, function, address, raw, format, isBits, now);
            if (!watch) break;
            var wait = TimeSpan.FromMilliseconds(interval) - (DateTime.UtcNow - started);
            if (wait > TimeSpan.Zero) Thread.Sleep(wait);
        } while (true);
        return ExitOk;
    }

    private static int Write(Options o)
    {
        var unit = o.Byte("unit", 1);
        var address = o.UShort("address", 0);
        var valuesText = o.Value("values") ?? o.Value("value") ?? throw new FormatException("--values is required, e.g. --values 1,2,3");
        var items = valuesText.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var fc = o.Value("fc");
        var coils = o.Flag("coils") || fc is "5" or "05" or "15";
        using var client = CreateClient(o);

        if (coils)
        {
            var bits = items.Select(ParseBool).ToArray();
            var single = fc is "5" or "05" || (fc is null && bits.Length == 1);
            if (single)
            {
                if (bits.Length != 1) throw new FormatException("Function 05 writes exactly one coil.");
                client.WriteSingleCoil(unit, address, bits[0]);
            }
            else
            {
                client.WriteMultipleCoils(unit, address, bits);
            }
            Done(o, $"Wrote {bits.Length} coil(s) at {address}.");
            return ExitOk;
        }

        var format = FormatOption(o);
        var registers = items.SelectMany(v => RegisterCodec.Parse(v, format)).ToArray();
        var useSingle = fc is "6" or "06" || (fc is null && registers.Length == 1);
        if (useSingle)
        {
            if (registers.Length != 1) throw new FormatException("Function 06 writes exactly one register. Use --fc 16 for 32/64-bit values.");
            client.WriteSingleRegister(unit, address, registers[0]);
        }
        else
        {
            client.WriteMultipleRegisters(unit, address, registers);
        }
        Done(o, $"Wrote {registers.Length} register(s) at {address}.");
        return ExitOk;
    }

    private static int Mask(Options o)
    {
        using var client = CreateClient(o);
        var address = o.UShort("address", 0);
        client.MaskWriteRegister(o.Byte("unit", 1), address, o.UShort("and", 0xFFFF), o.UShort("or", 0));
        Done(o, $"Mask write done at {address}.");
        return ExitOk;
    }

    private static int ReadWrite(Options o)
    {
        var format = FormatOption(o);
        var values = (o.Value("values") ?? throw new FormatException("--values is required."))
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SelectMany(v => RegisterCodec.Parse(v, format)).ToArray();
        using var client = CreateClient(o);
        var unit = o.Byte("unit", 1);
        var readAddress = o.UShort("read-address", 0);
        var result = client.ReadWriteMultipleRegisters(unit, readAddress, o.Int("read-count", 1), o.UShort("write-address", 0), values);
        Print(o, unit, FunctionCode.ReadWriteMultipleRegisters, readAddress, result, format, false, DateTime.Now);
        return ExitOk;
    }

    private static int ReportId(Options o)
    {
        using var client = CreateClient(o);
        var unit = o.Byte("unit", 1);
        var data = client.ReportServerId(unit);
        var text = new string(data.Select(b => b is >= 0x20 and <= 0x7E ? (char)b : (char)0x2E).ToArray());
        if (o.Flag("json"))
            WriteJson(new JsonObject { ["unit"] = unit, ["hex"] = Hex.Format(data), ["text"] = text });
        else
            Console.WriteLine($"{Hex.Format(data)}\n{text}");
        return ExitOk;
    }

    private static int Diagnostics(Options o)
    {
        using var client = CreateClient(o);
        var unit = o.Byte("unit", 1);
        var sub = o.UShort("sub", 0);
        var result = client.Diagnostics(unit, sub, o.UShort("data", 0));
        if (o.Flag("json"))
            WriteJson(new JsonObject { ["unit"] = unit, ["sub"] = sub, ["data"] = result });
        else
            Console.WriteLine($"Sub-function {sub}: {result} (0x{result:X4})");
        return ExitOk;
    }

    private static int Raw(Options o)
    {
        using var client = CreateClient(o);
        var unit = o.Byte("unit", 1);
        var pdu = Hex.Parse(o.Value("pdu") ?? throw new FormatException("--pdu is required, e.g. --pdu \"03 00 00 00 0A\""));
        var response = client.Execute(unit, pdu);
        if (o.Flag("json"))
            WriteJson(new JsonObject { ["unit"] = unit, ["request"] = Hex.Format(pdu), ["response"] = Hex.Format(response) });
        else
            Console.WriteLine(Hex.Format(response));
        return ExitOk;
    }

    private static int Scan(Options o)
    {
        var (from, to) = o.Range("units", 1, 247);
        var function = ReadFunction(o);
        using var client = CreateClient(o, defaultTimeoutMs: 200);
        var found = new JsonArray();
        foreach (var r in Scanners.ScanSlaves(client, from, to, function, o.UShort("address", 0), o.Int("count", 1)))
        {
            if (!o.Flag("json")) Console.Error.Write($"\rID {r.SlaveId,3}  ");
            if (!r.Found) continue;
            if (o.Flag("json"))
                found.Add(new JsonObject { ["unit"] = r.SlaveId, ["result"] = ModbusResults.Describe(r.Result) });
            else
                Console.WriteLine($"\rID {r.SlaveId,3}: {ModbusResults.Describe(r.Result)}");
        }
        if (o.Flag("json")) WriteJson(new JsonObject { ["found"] = found });
        else Console.Error.WriteLine("\rScan done.   ");
        return ExitOk;
    }

    private static int ScanAddresses(Options o)
    {
        var (from, to) = o.Range("addresses", 0, 9999);
        var function = ReadFunction(o);
        var unit = o.Byte("unit", 1);
        using var client = CreateClient(o);
        var rows = new JsonArray();
        foreach (var r in Scanners.ScanAddresses(client, unit, function, from, to, o.Int("block", 100)))
        {
            if (r.Result != ModbusResult.Success && !o.Flag("all")) continue;
            if (o.Flag("json"))
                rows.Add(new JsonObject { ["address"] = r.Address, ["value"] = r.Value, ["result"] = ModbusResults.Describe(r.Result) });
            else
                Console.WriteLine(r.Value is { } v ? $"{r.Address}: {v}" : $"{r.Address}: {ModbusResults.Describe(r.Result)}");
        }
        if (o.Flag("json")) WriteJson(new JsonObject { ["unit"] = unit, ["function"] = (int)function, ["addresses"] = rows });
        return ExitOk;
    }

    private static int Ports(Options o)
    {
        var ports = SerialChannel.AvailablePorts();
        if (o.Flag("json")) WriteJson(new JsonArray(ports.Select(p => (JsonNode)p!).ToArray()));
        else foreach (var p in ports) Console.WriteLine(p);
        return ExitOk;
    }

    private static int Serve(Options o)
    {
        var framing = (o.Value("framing") ?? "tcp").ToLowerInvariant() switch
        {
            "tcp" or "mbap" => SimulatorFraming.Mbap,
            "rtu" => SimulatorFraming.Rtu,
            "ascii" => SimulatorFraming.Ascii,
            var other => throw new FormatException($"Unknown framing '{other}'. Use tcp, rtu or ascii."),
        };
        var store = new ModbusDataStore();
        var handler = new ModbusRequestHandler(store);
        var bind = IPAddress.Parse(o.Value("bind") ?? "0.0.0.0");
        using var sim = new ModbusTcpSimulator(bind, o.Int("port", 502), framing, handler);
        if (o.Value("units") is not null)
        {
            var (a, b) = o.Range("units", 1, 247);
            sim.UnitIds = Enumerable.Range(a, b - a + 1).Select(i => (byte)i).ToHashSet();
        }
        if (o.Flag("log")) sim.Traffic += e => Console.WriteLine(e);
        sim.Start();
        Console.WriteLine($"Simulator listening on {bind}:{sim.Port} ({framing}). Press Ctrl+C to stop.");

        using var stop = new ManualResetEventSlim();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };
        var simulate = o.Flag("simulate");
        var tick = 0;
        while (!stop.Wait(1000))
        {
            if (!simulate) continue;
            tick++;
            lock (store.SyncRoot)
            {
                store.InputRegisters[0] = (ushort)tick;
                store.InputRegisters[1] = (ushort)(500 + 400 * Math.Sin(tick / 10.0));
                store.InputRegisters[2] = (ushort)Random.Shared.Next(0, 1000);
                store.DiscreteInputs[0] = tick % 2 == 0;
                store.HoldingRegisters[100] = (ushort)tick;
            }
        }
        return ExitOk;
    }

    private static int Version()
    {
        Console.WriteLine($"mbm {typeof(Program).Assembly.GetName().Version} (Modbus Master)");
        return ExitOk;
    }

    // ---- helpers ------------------------------------------------------------------------

    private static ModbusClient CreateClient(Options o, int defaultTimeoutMs = 1000)
    {
        var s = o.Value("connection") is { } file ? Workspace.LoadConnection(file) : new ConnectionSettings();
        string? hostPort = null;
        if (o.Value("tcp") is { } tcp) { s.Kind = ConnectionKind.Tcp; hostPort = tcp; }
        else if (o.Value("udp") is { } udp) { s.Kind = ConnectionKind.Udp; hostPort = udp; }
        else if (o.Value("rtu-tcp") is { } rtuTcp) { s.Kind = ConnectionKind.RtuAsciiOverTcp; s.Mode = SerialMode.Rtu; hostPort = rtuTcp; }
        else if (o.Value("rtu-udp") is { } rtuUdp) { s.Kind = ConnectionKind.RtuAsciiOverUdp; s.Mode = SerialMode.Rtu; hostPort = rtuUdp; }
        else if (o.Value("ascii-tcp") is { } asciiTcp) { s.Kind = ConnectionKind.RtuAsciiOverTcp; s.Mode = SerialMode.Ascii; hostPort = asciiTcp; }
        else if (o.Value("ascii-udp") is { } asciiUdp) { s.Kind = ConnectionKind.RtuAsciiOverUdp; s.Mode = SerialMode.Ascii; hostPort = asciiUdp; }
        else if (o.Value("tls") is { } tls) { s.Kind = ConnectionKind.TcpSecurity; s.Port = 802; hostPort = tls; }
        else if (o.Value("serial") is { } port)
        {
            s.Kind = ConnectionKind.Serial;
            s.SerialPort = port.ToUpperInvariant();
        }
        else if (o.Value("connection") is null)
        {
            throw new FormatException("Choose a connection: --tcp HOST[:PORT], --udp, --rtu-tcp, --rtu-udp, --ascii-tcp, --ascii-udp, --tls, --serial COMn or --connection FILE.");
        }

        if (hostPort is not null)
        {
            var (host, port) = SplitHostPort(hostPort, s.Kind == ConnectionKind.TcpSecurity ? 802 : 502);
            s.Host = host;
            s.Port = port;
        }

        s.BaudRate = o.Int("baud", s.BaudRate);
        s.DataBits = o.Int("data-bits", s.DataBits);
        if (o.Value("parity") is { } parity)
            s.Parity = parity.ToUpperInvariant() switch
            {
                "N" or "NONE" => Parity.None,
                "E" or "EVEN" => Parity.Even,
                "O" or "ODD" => Parity.Odd,
                _ => throw new FormatException("--parity must be N, E or O."),
            };
        if (o.Value("stop-bits") is { } stop)
            s.StopBits = stop == "2" ? StopBits.Two : stop == "1" ? StopBits.One : throw new FormatException("--stop-bits must be 1 or 2.");
        if (o.Value("mode") is { } mode)
            s.Mode = mode.ToLowerInvariant() == "ascii" ? SerialMode.Ascii : mode.ToLowerInvariant() == "rtu" ? SerialMode.Rtu
                : throw new FormatException("--mode must be rtu or ascii.");
        if (o.Flag("remove-echo")) s.RemoveEcho = true;
        if (o.Flag("ipv6")) s.IpVersion = 6;

        s.ResponseTimeoutMs = o.Int("timeout", o.Value("connection") is null ? defaultTimeoutMs : s.ResponseTimeoutMs);
        s.ConnectTimeoutMs = o.Int("connect-timeout", s.ConnectTimeoutMs);
        s.DelayBetweenPollsMs = o.Int("delay", s.DelayBetweenPollsMs);
        s.ClientCertificatePath = o.Value("cert") ?? s.ClientCertificatePath;
        s.ClientCertificatePassword = o.Value("cert-password") ?? s.ClientCertificatePassword;
        s.TrustedCaPath = o.Value("ca") ?? s.TrustedCaPath;
        s.TlsServerName = o.Value("server-name") ?? s.TlsServerName;
        if (o.Flag("insecure")) s.AllowUntrustedServer = true;

        var client = ModbusConnection.Create(s);
        if (o.Flag("log")) client.Traffic += e => Console.Error.WriteLine(e);
        client.Open();
        return client;
    }

    private static (string Host, int Port) SplitHostPort(string text, int defaultPort)
    {
        if (text.StartsWith('['))
        {
            var end = text.IndexOf(']');
            var host = text[1..end];
            return (host, end + 2 <= text.Length - 1 ? int.Parse(text[(end + 2)..], CultureInfo.InvariantCulture) : defaultPort);
        }
        var colon = text.LastIndexOf(':');
        if (colon > 0 && text.IndexOf(':') == colon)
            return (text[..colon], int.Parse(text[(colon + 1)..], CultureInfo.InvariantCulture));
        return (text, defaultPort);
    }

    private static FunctionCode ReadFunction(Options o)
    {
        if (o.Value("table") is { } table)
            return table.ToLowerInvariant() switch
            {
                "coils" or "0x" => FunctionCode.ReadCoils,
                "inputs" or "discrete-inputs" or "1x" => FunctionCode.ReadDiscreteInputs,
                "holding" or "holding-registers" or "4x" => FunctionCode.ReadHoldingRegisters,
                "input-registers" or "3x" => FunctionCode.ReadInputRegisters,
                _ => throw new FormatException("--table must be coils, inputs, holding or input-registers."),
            };
        var fc = o.Int("fc", 3);
        if (fc is < 1 or > 4) throw new FormatException("--fc must be 1, 2, 3 or 4 for reads.");
        return (FunctionCode)fc;
    }

    private static DisplayFormat FormatOption(Options o)
    {
        var kind = DisplayFormats.ParseKind(o.Value("format") ?? "int16");
        var order = DisplayFormats.ParseOrder(o.Value("byte-order") ?? "abcd");
        return DisplayFormats.Of(kind, order);
    }

    private static bool ParseBool(string s) => s.ToLowerInvariant() switch
    {
        "1" or "on" or "true" or "0xff00" => true,
        "0" or "off" or "false" => false,
        _ => throw new FormatException($"'{s}' is not a coil value. Use 1/0, on/off or true/false."),
    };

    private static List<(int Address, string Text, JsonNode? Number)> Decode(ushort address, ushort[] raw, DisplayFormat format, bool isBits)
    {
        var list = new List<(int, string, JsonNode?)>();
        var step = isBits ? 1 : format.Registers();
        for (var i = 0; i + step <= raw.Length; i += step)
        {
            if (isBits)
            {
                list.Add((address + i, raw[i].ToString(CultureInfo.InvariantCulture), JsonValue.Create(raw[i] != 0)));
                continue;
            }
            var text = RegisterCodec.Format(raw.AsSpan(i), format);
            JsonNode? number = format.Kind() switch
            {
                ValueKind.Hex or ValueKind.Binary => JsonValue.Create(text),
                ValueKind.Int64 => JsonValue.Create(long.Parse(text, CultureInfo.InvariantCulture)),
                ValueKind.UInt64 => JsonValue.Create(ulong.Parse(text, CultureInfo.InvariantCulture)),
                _ => JsonValue.Create(RegisterCodec.Decode(raw.AsSpan(i), format.Kind(), format.Order())),
            };
            list.Add((address + i, text, number));
        }
        return list;
    }

    private static void Print(Options o, byte unit, FunctionCode function, ushort address, ushort[] raw, DisplayFormat format, bool isBits, DateTime time)
    {
        var values = Decode(address, raw, format, isBits);
        if (o.Flag("json"))
        {
            var arr = new JsonArray();
            foreach (var (a, _, n) in values) arr.Add(new JsonObject { ["address"] = a, ["value"] = n });
            WriteJson(new JsonObject
            {
                ["time"] = time.ToString("O", CultureInfo.InvariantCulture),
                ["unit"] = unit,
                ["function"] = (int)function,
                ["address"] = address,
                ["format"] = isBits ? "bit" : format.Label(),
                ["values"] = arr,
                ["raw"] = new JsonArray(raw.Select(r => (JsonNode)r).ToArray()),
            });
            return;
        }
        if (o.Flag("plain"))
        {
            Console.WriteLine(string.Join(" ", values.Select(v => v.Text)));
            return;
        }
        foreach (var (a, t, _) in values) Console.WriteLine($"{a}: {t}");
    }

    private static void AppendCsv(string path, DateTime time, ushort address, ushort[] raw, DisplayFormat format, bool isBits, ref bool first)
    {
        var values = Decode(address, raw, format, isBits);
        var sb = new StringBuilder();
        if (first && !File.Exists(path))
            sb.AppendLine("Time;" + string.Join(";", values.Select(v => v.Address)));
        first = false;
        sb.AppendLine(time.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + ";" + string.Join(";", values.Select(v => v.Text)));
        File.AppendAllText(path, sb.ToString());
    }

    private static void Done(Options o, string message)
    {
        if (o.Flag("json")) WriteJson(new JsonObject { ["ok"] = true, ["message"] = message });
        else if (!o.Flag("quiet")) Console.WriteLine(message);
    }

    private static void WriteJson(JsonNode node) =>
        Console.WriteLine(node.ToJsonString(JsonOutput));

    private static readonly JsonSerializerOptions JsonOutput = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static int Fail(Options? o, int code, string message, int result)
    {
        if (o?.Flag("json") == true)
            WriteJson(new JsonObject { ["ok"] = false, ["result"] = result, ["error"] = message });
        else
            Console.Error.WriteLine($"Error: {message}");
        return code;
    }

    private static int Usage(string message)
    {
        Console.Error.WriteLine($"Error: {message}");
        Console.Error.WriteLine("Run 'mbm help' for usage.");
        return ExitUsage;
    }
}
