using System.Diagnostics;
using System.Net;
using ModbusMaster.Core;
using ModbusMaster.Core.Data;
using ModbusMaster.Core.Polling;
using ModbusMaster.Core.Protocol;
using ModbusMaster.Core.Server;
using ModbusMaster.Core.Slave;

namespace ModbusMaster.Tests;

public sealed class SlaveTests : IDisposable
{
    private readonly List<IDisposable> _cleanup = [];
    private readonly SlaveEngine _engine = new();

    public SlaveTests() => _cleanup.Add(_engine);

    private static PollDocument Window(byte id, FunctionCode table, ushort address, int quantity) =>
        new() { Definition = new PollDefinition { SlaveId = id, Function = table, Address = address, Quantity = quantity } };

    private ModbusClient Tcp(SimulatorFraming framing = SimulatorFraming.Mbap, int timeoutMs = 400)
    {
        var server = new ModbusTcpSimulator(IPAddress.Loopback, 0, framing, _engine);
        server.Start();
        _cleanup.Add(server);
        var kind = framing == SimulatorFraming.Mbap ? ConnectionKind.Tcp : ConnectionKind.RtuAsciiOverTcp;
        var client = ModbusConnection.Create(new ConnectionSettings
        {
            Kind = kind, Host = "127.0.0.1", Port = server.Port, Mode = framing == SimulatorFraming.Ascii ? SerialMode.Ascii : SerialMode.Rtu,
            ResponseTimeoutMs = timeoutMs, DelayBetweenPollsMs = 0,
        });
        _cleanup.Add(client);
        return client;
    }

    [Fact]
    public void Each_slave_id_has_its_own_data_and_unknown_ids_do_not_answer()
    {
        var w1 = Window(1, FunctionCode.ReadHoldingRegisters, 0, 10);
        var w2 = Window(2, FunctionCode.ReadHoldingRegisters, 0, 10);
        _engine.AddWindow(w1);
        _engine.AddWindow(w2);
        _engine.Write(w1, 0, new ushort[] { 111 });
        _engine.Write(w2, 0, new ushort[] { 222 });

        var c = Tcp();
        Assert.Equal(111, c.ReadHoldingRegisters(1, 0, 1)[0]);
        Assert.Equal(222, c.ReadHoldingRegisters(2, 0, 1)[0]);
        Assert.Throws<ModbusTimeoutException>(() => c.ReadHoldingRegisters(3, 0, 1));

        _engine.IgnoreUnitId = true;
        Assert.Equal(111, c.ReadHoldingRegisters(3, 0, 1)[0]);
    }

    [Fact]
    public void Strict_addressing_follows_the_windows()
    {
        _engine.AddWindow(Window(1, FunctionCode.ReadHoldingRegisters, 100, 10));
        _engine.AddWindow(Window(1, FunctionCode.ReadHoldingRegisters, 110, 5));
        var c = Tcp();
        Assert.Equal(15, c.ReadHoldingRegisters(1, 100, 15).Length); // two adjacent windows
        Assert.Equal(2, Assert.Throws<ModbusSlaveException>(() => c.ReadHoldingRegisters(1, 99, 2)).ExceptionCode);
        Assert.Equal(2, Assert.Throws<ModbusSlaveException>(() => c.ReadCoils(1, 100, 1)).ExceptionCode);

        _engine.StrictAddressing = false;
        Assert.Single(c.ReadCoils(1, 5000, 1));
    }

    [Fact]
    public void Master_writes_show_up_in_the_window()
    {
        var w = Window(1, FunctionCode.ReadHoldingRegisters, 0, 4);
        _engine.AddWindow(w);
        var c = Tcp();
        c.WriteMultipleRegisters(1, 0, new ushort[] { 5, 6, 7, 8 });
        Assert.Equal(new ushort[] { 5, 6, 7, 8 }, w.Values);
        Assert.True(w.TxCount >= 1);
    }

    [Fact]
    public void Disabled_windows_do_not_answer()
    {
        var w = Window(1, FunctionCode.ReadHoldingRegisters, 0, 4);
        _engine.AddWindow(w);
        w.Definition.Disabled = true;
        var c = Tcp();
        Assert.Throws<ModbusTimeoutException>(() => c.ReadHoldingRegisters(1, 0, 1));
    }

    [Theory]
    [InlineData(SimulatorFraming.Mbap)]
    [InlineData(SimulatorFraming.Rtu)]
    [InlineData(SimulatorFraming.Ascii)]
    public void Simulated_faults_reach_the_master(SimulatorFraming framing)
    {
        _engine.AddWindow(Window(1, FunctionCode.ReadHoldingRegisters, 0, 10));
        var device = _engine.Device(1);
        var c = Tcp(framing);

        device.Errors = new ErrorSimulation { Enabled = true, ChecksumErrorPercent = 100 };
        var bad = Assert.Throws<ModbusFrameException>(() => c.ReadHoldingRegisters(1, 0, 1));
        Assert.Equal(framing == SimulatorFraming.Mbap ? ModbusResult.ResponseError : ModbusResult.CrcError, bad.Result);

        device.Errors = new ErrorSimulation { Enabled = true, WrongIdPercent = 100 };
        Assert.Contains("expected 1", Assert.Throws<ModbusFrameException>(() => c.ReadHoldingRegisters(1, 0, 1)).Message);

        device.Errors = new ErrorSimulation { Enabled = true, TruncatePercent = 100 };
        Assert.ThrowsAny<ModbusCommunicationException>(() => c.ReadHoldingRegisters(1, 0, 4));

        device.Errors = new ErrorSimulation { Enabled = true, ExceptionCode = 6, ExceptionFunction = 3 };
        Assert.Equal(ModbusResult.ServerDeviceBusy, Assert.Throws<ModbusSlaveException>(() => c.ReadHoldingRegisters(1, 0, 1)).Result);

        device.Errors = new ErrorSimulation { Enabled = true, NoResponseEveryN = 2 };
        var results = Enumerable.Range(0, 4).Select(_ =>
        {
            try { c.ReadHoldingRegisters(1, 0, 1); return "ok"; }
            catch (ModbusTimeoutException) { return "timeout"; }
        }).ToList();
        Assert.Equal(2, results.Count(r => r == "timeout"));

        device.Errors = new ErrorSimulation();
        Assert.Single(c.ReadHoldingRegisters(1, 0, 1));
        Assert.True(device.SimulatedErrors >= 6);
    }

    [Fact]
    public void Response_delay_is_applied()
    {
        _engine.AddWindow(Window(1, FunctionCode.ReadHoldingRegisters, 0, 1));
        _engine.Device(1).Errors = new ErrorSimulation { Enabled = true, ResponseDelayMs = 250 };
        var c = Tcp(timeoutMs: 1000);
        var sw = Stopwatch.StartNew();
        c.ReadHoldingRegisters(1, 0, 1);
        Assert.True(sw.ElapsedMilliseconds >= 230, $"took {sw.ElapsedMilliseconds} ms");

        c.ResponseTimeout = TimeSpan.FromMilliseconds(100);
        Assert.Throws<ModbusTimeoutException>(() => c.ReadHoldingRegisters(1, 0, 1));
    }

    [Fact]
    public void Udp_server_answers()
    {
        var w = Window(4, FunctionCode.ReadInputRegisters, 0, 2);
        _engine.AddWindow(w);
        _engine.Write(w, 0, new ushort[] { 42, 43 });
        var server = new ModbusUdpServer(IPAddress.Loopback, 0, SimulatorFraming.Mbap, _engine);
        server.Start();
        _cleanup.Add(server);
        using var c = ModbusConnection.Create(new ConnectionSettings { Kind = ConnectionKind.Udp, Host = "127.0.0.1", Port = server.Port, ResponseTimeoutMs = 500, DelayBetweenPollsMs = 0 });
        Assert.Equal(new ushort[] { 42, 43 }, c.ReadInputRegisters(4, 0, 2));
    }

    [Fact]
    public void Device_identification_event_counter_and_diagnostics()
    {
        _engine.AddWindow(Window(1, FunctionCode.ReadHoldingRegisters, 0, 1));
        var device = _engine.Device(1);
        device.Handler.Identification = new DeviceIdentification { VendorName = "ACME", ProductCode = "P-1", Revision = "2.3", ProductName = "Pump" };
        var c = Tcp(SimulatorFraming.Rtu);

        var basic = c.ReadDeviceIdentification(1, code: 1);
        Assert.Equal(new[] { "ACME", "P-1", "2.3" }, basic.Select(o => o.Value));
        var regular = c.ReadDeviceIdentification(1, code: 2);
        Assert.Equal("Pump", regular.Single(o => o.Id == 4).Value);
        Assert.Equal("2.3", c.ReadDeviceIdentification(1, code: 4, objectId: 2).Single().Value);

        var before = c.GetCommEventCounter(1).EventCount; // function 11 itself is not counted
        c.ReadHoldingRegisters(1, 0, 1);
        c.ReadHoldingRegisters(1, 0, 1);
        Assert.Equal(before + 2, c.GetCommEventCounter(1).EventCount);
        Assert.True(c.Diagnostics(1, 14, 0) >= 3); // server message count

        // Force listen only: the slave stops answering until "restart communications".
        Assert.Throws<ModbusTimeoutException>(() => c.Diagnostics(1, 4, 0));
        Assert.Throws<ModbusTimeoutException>(() => c.ReadHoldingRegisters(1, 0, 1));
        Assert.Throws<ModbusTimeoutException>(() => c.Diagnostics(1, 1, 0));
        Assert.Single(c.ReadHoldingRegisters(1, 0, 1));
    }

    [Fact]
    public void Value_simulation_changes_values()
    {
        var w = Window(1, FunctionCode.ReadHoldingRegisters, 0, 4);
        w.Cell(0).Simulation = new CellSimulation { Mode = SimulationMode.Increment, Step = 1, Min = 0, Max = 1000, IntervalMs = 50 };
        w.Cell(2).Format = DisplayFormat.FloatBigEndian;
        w.Cell(2).Simulation = new CellSimulation { Mode = SimulationMode.Random, Min = 10, Max = 20, IntervalMs = 50 };
        _engine.AddWindow(w);
        Thread.Sleep(400);
        var values = w.Values;
        Assert.True(values[0] >= 3, $"increment reached {values[0]}");
        var f = RegisterCodec.Decode(values.AsSpan(2), ValueKind.Float, ByteOrder.BigEndian);
        Assert.InRange(f, 10, 20);
    }

    [Fact]
    public void Slave_workspace_round_trip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ms-{Guid.NewGuid():N}.mms");
        try
        {
            var w = Window(7, FunctionCode.ReadCoils, 0, 8);
            _engine.AddWindow(w);
            _engine.Write(w, 0, new ushort[] { 1, 0, 1 });
            _engine.Device(7).Errors = new ErrorSimulation { Enabled = true, ResponseDelayMs = 30 };
            SlaveWorkspace.From(_engine, new ServerSettings(), [w]).Save(path);

            using var other = new SlaveEngine();
            var ws = SlaveWorkspace.Load(path);
            var restored = ws.Windows.Single();
            other.AddWindow(restored);
            ws.ApplyTo(other, [restored]);
            Assert.Equal(new ushort[] { 1, 0, 1, 0, 0, 0, 0, 0 }, other.Read(restored));
            Assert.Equal(30, other.Device(7).Errors.ResponseDelayMs);
        }
        finally
        {
            File.Delete(path);
        }
    }

    public void Dispose()
    {
        foreach (var d in Enumerable.Reverse(_cleanup)) d.Dispose();
    }
}
