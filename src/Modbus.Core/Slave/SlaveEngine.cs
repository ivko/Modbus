using System.Text.Json.Serialization;
using Modbus.Core.Data;
using Modbus.Core.Polling;
using Modbus.Core.Protocol;
using Modbus.Core.Server;

namespace Modbus.Core.Slave;

/// <summary>Deliberate faults a simulated slave produces, to test how a master copes with a bad device.</summary>
public sealed class ErrorSimulation
{
    public bool Enabled { get; set; }

    /// <summary>Wait this long before answering (plus a random 0..DelayJitterMs).</summary>
    public int ResponseDelayMs { get; set; }
    public int DelayJitterMs { get; set; }

    /// <summary>Percentage of requests that get no answer at all (0-100).</summary>
    public double NoResponsePercent { get; set; }

    /// <summary>Do not answer every Nth request (0 = off).</summary>
    public int NoResponseEveryN { get; set; }

    /// <summary>Percentage of answers with a wrong CRC/LRC (or invalid MBAP header on TCP/UDP).</summary>
    public double ChecksumErrorPercent { get; set; }

    /// <summary>Percentage of answers cut short.</summary>
    public double TruncatePercent { get; set; }

    /// <summary>Percentage of answers that carry another slave ID.</summary>
    public double WrongIdPercent { get; set; }

    /// <summary>Exception code to return instead of the normal answer (0 = off), e.g. 4 = Server Device Failure, 6 = Busy.</summary>
    public byte ExceptionCode { get; set; }

    /// <summary>Only for this function code (0 = every function).</summary>
    public byte ExceptionFunction { get; set; }

    /// <summary>Percentage of matching requests that get the exception.</summary>
    public double ExceptionPercent { get; set; } = 100;

    [JsonIgnore]
    public bool IsActive => Enabled && (ResponseDelayMs > 0 || DelayJitterMs > 0 || NoResponsePercent > 0 || NoResponseEveryN > 0 ||
                                        ChecksumErrorPercent > 0 || TruncatePercent > 0 || WrongIdPercent > 0 || ExceptionCode != 0);

    public ErrorSimulation Clone() => (ErrorSimulation)MemberwiseClone();
}

/// <summary>One simulated slave: its data tables, function handler and error simulation.</summary>
public sealed class SlaveDevice
{
    private long _requests;
    private long _simulatedErrors;
    private long _sequence;

    internal SlaveDevice(byte id)
    {
        Id = id;
        Handler = new ModbusRequestHandler(Store) { ServerId = $"Modbus Slave ID {id}" };
    }

    public byte Id { get; }
    public ModbusDataStore Store { get; } = new();
    public ModbusRequestHandler Handler { get; }
    public ErrorSimulation Errors { get; set; } = new();

    public long Requests => Interlocked.Read(ref _requests);
    public long SimulatedErrors => Interlocked.Read(ref _simulatedErrors);

    internal long NextSequence() => Interlocked.Increment(ref _sequence);
    internal void CountRequest() => Interlocked.Increment(ref _requests);
    internal void CountSimulatedError() => Interlocked.Increment(ref _simulatedErrors);

    public void ResetCounters()
    {
        Interlocked.Exchange(ref _requests, 0);
        Interlocked.Exchange(ref _simulatedErrors, 0);
        Handler.Counters.Clear();
    }
}

/// <summary>
/// Slave side of the program. Each data window defines a slave ID, a table (coils, discrete inputs, holding or
/// input registers), an address and a quantity. Requests are answered from the device memory of that slave ID.
/// With strict addressing, addresses that are not shown in any window return Illegal Data Address.
/// </summary>
public sealed class SlaveEngine : IServerHandler, IDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<byte, SlaveDevice> _devices = [];
    private readonly List<PollDocument> _windows = [];
    private readonly Dictionary<(PollDocument, int), DateTime> _nextSimulation = [];
    private readonly Thread _thread;
    private readonly DateTime _start = DateTime.UtcNow;
    private volatile bool _stopped;

    public SlaveEngine()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Slave simulation" };
        _thread.Start();
    }

    /// <summary>Answer requests for slave IDs that have no window with the first defined slave (TCP devices often send unit 0 or 255).</summary>
    public bool IgnoreUnitId { get; set; }

    /// <summary>Only addresses shown in a window exist; others return exception 02.</summary>
    public bool StrictAddressing { get; set; } = true;

    public IReadOnlyList<PollDocument> Windows
    {
        get { lock (_sync) return _windows.ToArray(); }
    }

    public IReadOnlyList<SlaveDevice> Devices
    {
        get { lock (_sync) return _devices.Values.OrderBy(d => d.Id).ToArray(); }
    }

    /// <summary>Slave IDs that currently have at least one window.</summary>
    public IReadOnlyList<byte> ActiveIds
    {
        get { lock (_sync) return _windows.Select(w => w.Definition.SlaveId).Distinct().Order().ToArray(); }
    }

    public SlaveDevice Device(byte id)
    {
        lock (_sync)
        {
            if (!_devices.TryGetValue(id, out var device))
            {
                device = new SlaveDevice(id);
                device.Handler.IsValidRange = (table, address, count) => IsValidRange(id, table, address, count);
                _devices[id] = device;
            }
            return device;
        }
    }

    public void AddWindow(PollDocument window)
    {
        if (!IsTable(window.Definition.Function))
            throw new ArgumentException("A slave window must use function 01, 02, 03 or 04 to choose its table.");
        lock (_sync)
        {
            if (!_windows.Contains(window)) _windows.Add(window);
        }
        Device(window.Definition.SlaveId);
        Sync(window);
    }

    public void RemoveWindow(PollDocument window)
    {
        lock (_sync)
        {
            _windows.Remove(window);
            foreach (var key in _nextSimulation.Keys.Where(k => k.Item1 == window).ToList()) _nextSimulation.Remove(key);
        }
    }

    /// <summary>Call after the window definition changed.</summary>
    public void WindowChanged(PollDocument window)
    {
        Device(window.Definition.SlaveId);
        lock (_sync)
            foreach (var key in _nextSimulation.Keys.Where(k => k.Item1 == window).ToList()) _nextSimulation.Remove(key);
        Sync(window);
    }

    public static bool IsTable(FunctionCode f) => f is FunctionCode.ReadCoils or FunctionCode.ReadDiscreteInputs
        or FunctionCode.ReadHoldingRegisters or FunctionCode.ReadInputRegisters;

    // ---- data access for the UI and automation ----

    /// <summary>Writes registers (or 0/1 for bit tables) starting at window cell <paramref name="index"/>.</summary>
    public void Write(PollDocument window, int index, IReadOnlyList<ushort> values)
    {
        var def = window.Definition;
        var device = Device(def.SlaveId);
        var start = def.Address + index;
        lock (device.Store.SyncRoot)
        {
            for (var i = 0; i < values.Count && start + i < 65536; i++)
            {
                switch (def.Function)
                {
                    case FunctionCode.ReadCoils: device.Store.Coils[start + i] = values[i] != 0; break;
                    case FunctionCode.ReadDiscreteInputs: device.Store.DiscreteInputs[start + i] = values[i] != 0; break;
                    case FunctionCode.ReadHoldingRegisters: device.Store.HoldingRegisters[start + i] = values[i]; break;
                    default: device.Store.InputRegisters[start + i] = values[i]; break;
                }
            }
        }
        SyncAll();
    }

    /// <summary>Current values of the window's range (0/1 for bit tables).</summary>
    public ushort[] Read(PollDocument window)
    {
        var def = window.Definition;
        var device = Device(def.SlaveId);
        var count = Math.Min(def.Quantity, 65536 - def.Address);
        var result = new ushort[count];
        lock (device.Store.SyncRoot)
        {
            for (var i = 0; i < count; i++)
            {
                var a = def.Address + i;
                result[i] = def.Function switch
                {
                    FunctionCode.ReadCoils => (ushort)(device.Store.Coils[a] ? 1 : 0),
                    FunctionCode.ReadDiscreteInputs => (ushort)(device.Store.DiscreteInputs[a] ? 1 : 0),
                    FunctionCode.ReadHoldingRegisters => device.Store.HoldingRegisters[a],
                    _ => device.Store.InputRegisters[a],
                };
            }
        }
        return result;
    }

    // ---- request handling ----

    private bool IsValidRange(byte id, FunctionCode table, int address, int count)
    {
        if (!StrictAddressing) return true;
        lock (_sync)
        {
            // Every address of the request must be covered by an enabled window of that slave and table.
            var ranges = _windows.Where(w => w.Definition.SlaveId == id && w.Definition.Function == table && !w.Definition.Disabled)
                .Select(w => (Start: (int)w.Definition.Address, End: w.Definition.Address + w.Definition.Quantity))
                .OrderBy(r => r.Start).ToList();
            var next = address;
            var end = address + count;
            foreach (var (s, e) in ranges)
            {
                if (s > next) break;
                if (e > next) next = e;
                if (next >= end) return true;
            }
            return next >= end;
        }
    }

    private SlaveDevice? Target(byte unit)
    {
        lock (_sync)
        {
            var ids = _windows.Select(w => w.Definition.SlaveId).Distinct().ToList();
            if (ids.Contains(unit)) return _devices.GetValueOrDefault(unit);
            if (IgnoreUnitId && ids.Count > 0) return _devices.GetValueOrDefault(ids.Min());
            return null;
        }
    }

    public ServerReply Handle(byte unit, byte[] pdu)
    {
        // Serial broadcast (unit 0) executes writes on every slave and is never answered.
        if (unit == 0 && Target(0) is null && !IgnoreUnitId)
        {
            foreach (var id in ActiveIds) Device(id).Handler.Handle(pdu);
            SyncAll();
            return ServerReply.NoResponse;
        }

        var device = Target(unit);
        if (device is null) return ServerReply.NoResponse;
        device.CountRequest();
        var windows = WindowsFor(device.Id);
        var function = pdu.Length > 0 ? pdu[0] : (byte)0;

        // A slave whose windows are all disabled does not answer.
        if (windows.Count > 0 && windows.All(w => w.Definition.Disabled))
        {
            device.Handler.Counters.NoResponse();
            return ServerReply.NoResponse;
        }

        var e = device.Errors;
        var sequence = device.NextSequence();
        if (e.Enabled)
        {
            if ((e.NoResponseEveryN > 0 && sequence % e.NoResponseEveryN == 0) || Chance(e.NoResponsePercent))
            {
                device.CountSimulatedError();
                device.Handler.Counters.NoResponse();
                Count(windows, function, error: true);
                return ServerReply.NoResponse;
            }
        }

        byte[]? response;
        if (e.Enabled && e.ExceptionCode != 0 && (e.ExceptionFunction == 0 || e.ExceptionFunction == function) && Chance(e.ExceptionPercent))
        {
            device.CountSimulatedError();
            response = ModbusRequestHandler.Exception(function, e.ExceptionCode);
        }
        else
        {
            response = device.Handler.Handle(pdu);
        }
        if (response is null) return ServerReply.NoResponse;

        var fault = FrameFault.None;
        var delay = TimeSpan.Zero;
        if (e.Enabled)
        {
            if (Chance(e.ChecksumErrorPercent)) fault = FrameFault.BadChecksum;
            else if (Chance(e.TruncatePercent)) fault = FrameFault.Truncate;
            else if (Chance(e.WrongIdPercent)) fault = FrameFault.WrongId;
            if (fault != FrameFault.None) device.CountSimulatedError();
            var ms = e.ResponseDelayMs + (e.DelayJitterMs > 0 ? Random.Shared.Next(0, e.DelayJitterMs + 1) : 0);
            if (ms > 0) delay = TimeSpan.FromMilliseconds(ms);
        }

        Count(windows, function, error: (response[0] & 0x80) != 0 || fault != FrameFault.None);
        if (function is 5 or 6 or 15 or 16 or 22 or 23 || unit == 0) SyncAll();
        return new ServerReply(response, delay, fault);
    }

    void IServerHandler.OnFrame(bool valid)
    {
        foreach (var d in Devices)
        {
            ((IServerHandler)d.Handler).OnFrame(valid);
        }
    }

    private List<PollDocument> WindowsFor(byte id)
    {
        lock (_sync) return _windows.Where(w => w.Definition.SlaveId == id).ToList();
    }

    private static void Count(List<PollDocument> windows, byte function, bool error)
    {
        var table = function switch
        {
            1 or 5 or 15 => FunctionCode.ReadCoils,
            2 => FunctionCode.ReadDiscreteInputs,
            3 or 6 or 16 or 22 or 23 => FunctionCode.ReadHoldingRegisters,
            4 => FunctionCode.ReadInputRegisters,
            _ => (FunctionCode)0,
        };
        foreach (var w in windows.Where(w => w.Definition.Function == table)) w.CountRequest(error);
    }

    private static bool Chance(double percent) => percent > 0 && (percent >= 100 || Random.Shared.NextDouble() * 100 < percent);

    // ---- value simulation and window refresh ----

    private void Run()
    {
        while (!_stopped)
        {
            try
            {
                Simulate();
                SyncAll();
            }
            catch (Exception ex) when (ex is ArgumentException or OverflowException or InvalidOperationException)
            {
                // A bad simulation setting must not stop the engine.
            }
            Thread.Sleep(50);
        }
    }

    private void Simulate()
    {
        var now = DateTime.UtcNow;
        foreach (var window in Windows)
        {
            var def = window.Definition;
            foreach (var (index, cell) in window.CellSnapshot())
            {
                if (cell.Simulation is not { Mode: not SimulationMode.None } sim || index >= def.Quantity) continue;
                var key = (window, index);
                bool due;
                lock (_sync)
                {
                    due = !_nextSimulation.TryGetValue(key, out var next) || now >= next;
                    if (due) _nextSimulation[key] = now.AddMilliseconds(Math.Max(50, sim.IntervalMs));
                }
                if (!due) continue;
                var values = Read(window);
                Write(window, index, NextValue(window, index, values, sim, now));
            }
        }
    }

    private ushort[] NextValue(PollDocument window, int index, ushort[] values, CellSimulation sim, DateTime now)
    {
        var t = (now - _start).TotalSeconds;
        var period = Math.Max(0.1, sim.PeriodSeconds);
        var min = Math.Min(sim.Min, sim.Max);
        var max = Math.Max(sim.Min, sim.Max);

        if (window.Definition.IsBits)
        {
            var bit = values[index] != 0;
            bit = sim.Mode switch
            {
                SimulationMode.Random => Random.Shared.Next(2) == 1,
                SimulationMode.Sine => Math.Sin(2 * Math.PI * t / period) >= 0,
                SimulationMode.Ramp => t % period >= period / 2,
                _ => !bit,
            };
            return [(ushort)(bit ? 1 : 0)];
        }

        var format = window.PeekCell(index)?.Format ?? DisplayFormat.Signed;
        var kind = format.Kind();
        var span = values.AsSpan(index);
        if (span.Length < format.Registers()) return [];
        var current = RegisterCodec.Decode(span, kind, format.Order());
        var isInteger = kind is not (ValueKind.Float or ValueKind.Double);

        var next = sim.Mode switch
        {
            SimulationMode.Increment => current + sim.Step > max ? min : current + sim.Step,
            SimulationMode.Decrement => current - sim.Step < min ? max : current - sim.Step,
            SimulationMode.Random => min + Random.Shared.NextDouble() * (max - min),
            SimulationMode.Sine => (min + max) / 2 + (max - min) / 2 * Math.Sin(2 * Math.PI * t / period),
            SimulationMode.Ramp => min + (max - min) * (t % period) / period,
            SimulationMode.Toggle => current == min ? max : min,
            _ => current,
        };
        if (isInteger) next = Math.Round(next);
        return RegisterCodec.Encode(Clamp(next, kind), kind, format.Order());
    }

    private static double Clamp(double v, ValueKind kind) => kind switch
    {
        ValueKind.Int16 => Math.Clamp(v, short.MinValue, short.MaxValue),
        ValueKind.UInt16 or ValueKind.Hex or ValueKind.Binary => Math.Clamp(v, 0, ushort.MaxValue),
        ValueKind.Int32 => Math.Clamp(v, int.MinValue, int.MaxValue),
        ValueKind.UInt32 => Math.Clamp(v, 0, uint.MaxValue),
        ValueKind.UInt64 => Math.Max(0, v),
        _ => v,
    };

    private void SyncAll()
    {
        foreach (var w in Windows) Sync(w);
    }

    private void Sync(PollDocument window)
    {
        var values = Read(window);
        var old = window.Values;
        var changed = old.Length != values.Length || !old.AsSpan().SequenceEqual(values);
        // Refresh at least once a second so charts and logs keep running while values stay the same.
        if (changed || (DateTime.Now - window.LastUpdate).TotalMilliseconds >= 1000) window.SetValues(values);
    }

    public void Dispose()
    {
        _stopped = true;
        _thread.Join(TimeSpan.FromSeconds(1));
    }
}
