using System.Net;
using ModbusMaster.Core;
using ModbusMaster.Core.Data;
using ModbusMaster.Core.Polling;
using ModbusMaster.Core.Protocol;
using ModbusMaster.Core.Server;

namespace ModbusMaster.Tests;

public sealed class EndToEndTests : IDisposable
{
    private readonly List<IDisposable> _cleanup = [];

    private (ModbusClient Client, ModbusDataStore Store, ModbusTcpSimulator Sim) Start(SimulatorFraming framing, ConnectionKind kind, SerialMode mode = SerialMode.Rtu)
    {
        var store = new ModbusDataStore();
        var sim = new ModbusTcpSimulator(IPAddress.Loopback, 0, framing, new ModbusRequestHandler(store) { AddressLimit = 1000 })
        {
            UnitIds = [1, 2],
        };
        sim.Start();
        var client = ModbusConnection.Create(new ConnectionSettings
        {
            Kind = kind, Host = "127.0.0.1", Port = sim.Port, Mode = mode, ResponseTimeoutMs = 500, DelayBetweenPollsMs = 0,
        });
        _cleanup.Add(client);
        _cleanup.Add(sim);
        return (client, store, sim);
    }

    public static TheoryData<SimulatorFraming, ConnectionKind, SerialMode> Transports => new()
    {
        { SimulatorFraming.Mbap, ConnectionKind.Tcp, SerialMode.Rtu },
        { SimulatorFraming.Rtu, ConnectionKind.RtuAsciiOverTcp, SerialMode.Rtu },
        { SimulatorFraming.Ascii, ConnectionKind.RtuAsciiOverTcp, SerialMode.Ascii },
    };

    [Theory]
    [MemberData(nameof(Transports))]
    public void Every_function_round_trips(SimulatorFraming framing, ConnectionKind kind, SerialMode mode)
    {
        var (c, store, _) = Start(framing, kind, mode);

        c.WriteMultipleRegisters(1, 10, new ushort[] { 1, 2, 3, 65535 });
        Assert.Equal(new ushort[] { 1, 2, 3, 65535 }, c.ReadHoldingRegisters(1, 10, 4));

        c.WriteSingleRegister(1, 20, 0x1234);
        Assert.Equal(0x1234, store.HoldingRegisters[20]);

        c.WriteSingleCoil(1, 5, true);
        c.WriteMultipleCoils(1, 6, new[] { true, false, true });
        Assert.Equal(new[] { true, true, false, true }, c.ReadCoils(1, 5, 4));

        store.DiscreteInputs[3] = true;
        store.InputRegisters[7] = 777;
        Assert.Equal(new[] { false, true }, c.ReadDiscreteInputs(1, 2, 2));
        Assert.Equal(new ushort[] { 777 }, c.ReadInputRegisters(1, 7, 1));

        store.HoldingRegisters[30] = 0x0012;
        c.MaskWriteRegister(1, 30, 0x00F2, 0x0025);
        Assert.Equal(0x0017, store.HoldingRegisters[30]); // example from the Modbus specification

        var rw = c.ReadWriteMultipleRegisters(1, 40, 2, 40, new ushort[] { 9, 8 });
        Assert.Equal(new ushort[] { 9, 8 }, rw);

        Assert.Contains("simulator", System.Text.Encoding.ASCII.GetString(c.ReportServerId(1)));
        Assert.Equal(0xA537, c.Diagnostics(1, 0, 0xA537));
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public void Exception_and_timeout(SimulatorFraming framing, ConnectionKind kind, SerialMode mode)
    {
        var (c, _, _) = Start(framing, kind, mode);

        var ex = Assert.Throws<ModbusSlaveException>(() => c.ReadHoldingRegisters(1, 999, 2));
        Assert.Equal(2, ex.ExceptionCode);
        Assert.Equal(ModbusResult.IllegalDataAddress, ex.Result);

        var timeout = Assert.Throws<ModbusTimeoutException>(() => c.ReadHoldingRegisters(9, 0, 1));
        Assert.Equal(ModbusResult.Timeout, timeout.Result);

        // The connection is still usable after a timeout.
        Assert.Single(c.ReadHoldingRegisters(2, 0, 1));
    }

    [Fact]
    public void Traffic_is_reported()
    {
        var (c, _, _) = Start(SimulatorFraming.Rtu, ConnectionKind.RtuAsciiOverTcp);
        var entries = new List<TrafficEntry>();
        c.Traffic += entries.Add;
        c.ReadHoldingRegisters(1, 0, 10);
        Assert.Equal("01 03 00 00 00 0A C5 CD", entries[0].DataText);
        Assert.Equal(TrafficDirection.Rx, entries[1].Direction);
        Assert.Equal(25, entries[1].Data.Length);
    }

    [Fact]
    public void Connect_failure_reports_open_error()
    {
        var c = ModbusConnection.Create(new ConnectionSettings { Kind = ConnectionKind.Tcp, Host = "127.0.0.1", Port = 1, ConnectTimeoutMs = 500 });
        var ex = Assert.Throws<ModbusConnectionException>(c.Open);
        Assert.True(ex.OpenError is 13 or 14);
    }

    [Fact]
    public void Scheduler_polls_windows_and_writes_write_windows()
    {
        var (c, store, _) = Start(SimulatorFraming.Mbap, ConnectionKind.Tcp);
        store.HoldingRegisters[0] = 0x3F80; // 1.0f big-endian
        using var scheduler = new PollScheduler { Client = c };
        var read = new PollDocument { Definition = new PollDefinition { Quantity = 4, ScanRateMs = 50 } };
        read.Cell(0).Format = DisplayFormat.FloatBigEndian;
        var write = new PollDocument
        {
            Definition = new PollDefinition { Function = FunctionCode.WriteMultipleRegisters, Address = 100, Quantity = 2, ScanRateMs = 50 },
        };
        write.SetWriteValues(0, new ushort[] { 11, 22 });

        var done = new ManualResetEventSlim();
        read.Updated += d => { if (d.LastResult == ModbusResult.Success) done.Set(); };
        scheduler.Add(read);
        scheduler.Add(write);
        Assert.True(done.Wait(3000));
        Assert.Equal("1", read.FormatCell(0));
        Assert.True(read.IsContinuation(1));

        SpinWait.SpinUntil(() => store.HoldingRegisters[101] == 22, 3000);
        Assert.Equal(22, store.HoldingRegisters[101]);
        Assert.Equal(ModbusResult.Success, write.LastWriteResult);
    }

    [Fact]
    public void Slave_and_address_scan()
    {
        var (c, _, _) = Start(SimulatorFraming.Mbap, ConnectionKind.Tcp);
        c.ResponseTimeout = TimeSpan.FromMilliseconds(100);
        var found = Scanners.ScanSlaves(c, 1, 4, FunctionCode.ReadHoldingRegisters, 0, 1).Where(r => r.Found).Select(r => r.SlaveId);
        Assert.Equal(new byte[] { 1, 2 }, found);

        var addresses = Scanners.ScanAddresses(c, 1, FunctionCode.ReadHoldingRegisters, 990, 1005, 10).ToList();
        Assert.Equal(16, addresses.Count);
        Assert.Equal(10, addresses.Count(a => a.Result == ModbusResult.Success));
        Assert.All(addresses.Where(a => a.Address >= 1000), a => Assert.Equal(ModbusResult.IllegalDataAddress, a.Result));
    }

    [Fact]
    public void Workspace_round_trip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mm-{Guid.NewGuid():N}.mmw");
        try
        {
            var doc = new PollDocument { Title = "Pumps", Definition = new PollDefinition { SlaveId = 3, Quantity = 4 } };
            doc.Cell(2).Name = "Speed";
            doc.Cell(2).Format = DisplayFormat.FloatLittleEndianByteSwap;
            doc.Cell(2).Scaling = new Scaling { ScaledMax = 50 };
            new Workspace { Connection = new ConnectionSettings { Host = "10.0.0.5" }, Windows = [doc] }.Save(path);

            var ws = Workspace.Load(path);
            Assert.Equal("10.0.0.5", ws.Connection!.Host);
            var w = Assert.Single(ws.Windows);
            Assert.Equal(3, w.Definition.SlaveId);
            Assert.Equal("Speed", w.Cells[2].Name);
            Assert.Equal(DisplayFormat.FloatLittleEndianByteSwap, w.Cells[2].Format);
            Assert.Equal(50, w.Cells[2].Scaling!.ScaledMax);
        }
        finally
        {
            File.Delete(path);
        }
    }

    public void Dispose()
    {
        foreach (var d in _cleanup) d.Dispose();
    }
}
