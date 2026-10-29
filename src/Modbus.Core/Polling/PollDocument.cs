using System.Text.Json.Serialization;
using Modbus.Core.Data;
using Modbus.Core.Protocol;

namespace Modbus.Core.Polling;

/// <summary>What one data window reads or writes (the Read/Write Definition, F8).</summary>
public sealed class PollDefinition
{
    public byte SlaveId { get; set; } = 1;
    public FunctionCode Function { get; set; } = FunctionCode.ReadHoldingRegisters;
    public ushort Address { get; set; }
    public int Quantity { get; set; } = 10;
    public int ScanRateMs { get; set; } = 1000;

    /// <summary>When true the window does not poll; use Read/Write Once.</summary>
    public bool Disabled { get; set; }

    /// <summary>Function 23 only: where the write part goes.</summary>
    public ushort WriteAddress { get; set; }

    /// <summary>Show PLC addresses (40001...) instead of protocol addresses (0...).</summary>
    public bool PlcAddresses { get; set; }

    public PollDefinition Clone() => (PollDefinition)MemberwiseClone();

    public bool IsBits => FunctionCodes.IsBitFunction(Function);

    public bool IsWriteWindow => Function is FunctionCode.WriteSingleCoil or FunctionCode.WriteSingleRegister
        or FunctionCode.WriteMultipleCoils or FunctionCode.WriteMultipleRegisters;

    /// <summary>Data cells shown (write single = 1).</summary>
    public int CellCount => Function is FunctionCode.WriteSingleCoil or FunctionCode.WriteSingleRegister ? 1 : Quantity;

    public string Validate()
    {
        if (Function is FunctionCode.Diagnostics or FunctionCode.ReportServerId or FunctionCode.MaskWriteRegister)
            return "This function is not available as a polling window. Use the Functions menu.";
        var max = Pdu.MaxQuantity(Function);
        if (Function == FunctionCode.ReadWriteMultipleRegisters) max = Pdu.MaxReadRegisters;
        if (CellCount < 1 || CellCount > max) return $"Quantity must be 1 to {max}.";
        if (Address + CellCount > 65536) return "Address + quantity exceeds 65535.";
        if (ScanRateMs < 0 || ScanRateMs > 3_600_000) return "Scan rate must be 0 to 3600000 ms.";
        if (SlaveId == 0 && !IsWriteWindow) return "Slave ID 0 is broadcast and can only be used for writes.";
        return "";
    }

    public string Describe() =>
        $"ID = {SlaveId}: F = {(byte)Function:D2}: SR = {ScanRateMs}ms";
}

/// <summary>Per-cell settings: name, format, scaling, colors, value names.</summary>
public sealed class CellSettings
{
    public string Name { get; set; } = "";
    public DisplayFormat Format { get; set; } = DisplayFormat.Signed;
    public Scaling? Scaling { get; set; }
    public ConditionalColors? Colors { get; set; }
    public List<ValueName> ValueNames { get; set; } = [];

    /// <summary>Slave windows only: the value changes by itself.</summary>
    public CellSimulation? Simulation { get; set; }

    [JsonIgnore]
    public bool IsDefault => Name.Length == 0 && Format == DisplayFormat.Signed && Scaling is null && Colors is null && ValueNames.Count == 0 && Simulation is null;

    public CellSettings Clone() => new()
    {
        Name = Name,
        Format = Format,
        Scaling = Scaling?.Clone(),
        Colors = Colors?.Clone(),
        ValueNames = ValueNames.Select(v => new ValueName { Value = v.Value, Name = v.Name }).ToList(),
        Simulation = Simulation?.Clone(),
    };
}

/// <summary>One data window: definition, cell settings and live values.</summary>
public sealed class PollDocument
{
    private readonly object _sync = new();
    private ushort[] _values = [];
    private ushort[] _writeValues = [];

    public string Title { get; set; } = "Mbpoll1";
    public PollDefinition Definition { get; set; } = new();

    /// <summary>Settings keyed by cell index (0 = first address).</summary>
    public Dictionary<int, CellSettings> Cells { get; set; } = [];

    /// <summary>0 = 10 rows, 1 = 20, 2 = 50, 3 = 100, 4 = fit to quantity.</summary>
    public int RowsMode { get; set; } = 4;

    // ---- runtime state (not saved) ----

    [JsonIgnore] public ModbusResult LastResult { get; private set; } = ModbusResult.DataUninitialized;
    [JsonIgnore] public string LastError { get; private set; } = "";
    [JsonIgnore] public ModbusResult LastWriteResult { get; set; } = ModbusResult.DataUninitialized;
    [JsonIgnore] public long TxCount { get; private set; }
    [JsonIgnore] public long RxCount { get; private set; }
    [JsonIgnore] public long ErrorCount { get; private set; }
    [JsonIgnore] public DateTime LastUpdate { get; private set; }
    [JsonIgnore] internal DateTime NextDueUtc { get; set; } = DateTime.MinValue;
    [JsonIgnore] internal bool OnceRequested { get; set; }

    /// <summary>Raised on the polling thread after each request of this window.</summary>
    public event Action<PollDocument>? Updated;

    public CellSettings Cell(int index)
    {
        lock (_sync)
        {
            if (!Cells.TryGetValue(index, out var c))
            {
                c = new CellSettings();
                Cells[index] = c;
            }
            return c;
        }
    }

    public CellSettings? PeekCell(int index)
    {
        lock (_sync) return Cells.GetValueOrDefault(index);
    }

    /// <summary>Snapshot of the last values read (registers, or 0/1 for bits).</summary>
    [JsonIgnore]
    public ushort[] Values
    {
        get { lock (_sync) return (ushort[])_values.Clone(); }
    }

    /// <summary>Data written by write windows (and edited by the user in them).</summary>
    [JsonIgnore]
    public ushort[] WriteValues
    {
        get { lock (_sync) return (ushort[])EnsureWriteArray().Clone(); }
    }

    public void SetWriteValue(int index, ushort value)
    {
        lock (_sync)
        {
            var arr = EnsureWriteArray();
            if (index >= 0 && index < arr.Length) arr[index] = value;
        }
    }

    public void SetWriteValues(int index, ReadOnlySpan<ushort> values)
    {
        lock (_sync)
        {
            var arr = EnsureWriteArray();
            for (var i = 0; i < values.Length && index + i < arr.Length; i++) arr[index + i] = values[i];
        }
    }

    private ushort[] EnsureWriteArray()
    {
        var n = Definition.CellCount;
        if (_writeValues.Length != n) Array.Resize(ref _writeValues, n);
        return _writeValues;
    }

    /// <summary>Requests one read/write even when the window is disabled (F6).</summary>
    public void RequestOnce()
    {
        OnceRequested = true;
        NextDueUtc = DateTime.MinValue;
    }

    public void ResetCounters()
    {
        lock (_sync)
        {
            TxCount = RxCount = ErrorCount = 0;
        }
    }

    public void ApplyDefinition(PollDefinition definition)
    {
        lock (_sync)
        {
            Definition = definition.Clone();
            _values = [];
            LastResult = ModbusResult.DataUninitialized;
            LastError = "";
            NextDueUtc = DateTime.MinValue;
        }
    }

    internal void Complete(ushort[]? values, ModbusResult result, string error, bool responded)
    {
        lock (_sync)
        {
            TxCount++;
            if (responded) RxCount++;
            if (result != ModbusResult.Success) ErrorCount++;
            if (values is not null) _values = values;
            LastResult = result;
            LastError = error;
            LastUpdate = DateTime.Now;
        }
        Updated?.Invoke(this);
    }

    internal void NotifyChanged() => Updated?.Invoke(this);

    /// <summary>Slave windows: shows <paramref name="values"/> without touching the request counters.</summary>
    internal void SetValues(ushort[] values)
    {
        lock (_sync)
        {
            _values = values;
            LastResult = ModbusResult.Success;
            LastError = "";
            LastUpdate = DateTime.Now;
        }
        Updated?.Invoke(this);
    }

    /// <summary>Slave windows: counts requests that reached this window.</summary>
    internal void CountRequest(bool error)
    {
        lock (_sync)
        {
            TxCount++;
            if (!error) RxCount++;
            else ErrorCount++;
        }
    }

    /// <summary>Formats cell <paramref name="index"/> with its name, format, scaling and value names.</summary>
    public string FormatCell(int index, ushort[]? values = null)
    {
        values ??= Definition.IsWriteWindow ? WriteValues : Values;
        if (index < 0 || index >= values.Length) return "";
        var cell = PeekCell(index);
        if (Definition.IsBits) return values[index] != 0 ? "1" : "0";
        var format = cell?.Format ?? DisplayFormat.Signed;
        var span = values.AsSpan(index);
        if (span.Length < format.Registers()) return "-";
        var raw = RegisterCodec.Decode(span, format.Kind(), format.Order());
        if (cell is not null && cell.ValueNames.Count > 0)
        {
            var match = cell.ValueNames.FirstOrDefault(v => v.Value == (long)raw);
            if (match is not null) return match.Name;
        }
        if (cell?.Scaling is { } scaling) return scaling.Format(raw);
        return RegisterCodec.Format(span, format);
    }

    /// <summary>True when cell <paramref name="index"/> is covered by a multi-register value that starts earlier.</summary>
    public bool IsContinuation(int index)
    {
        if (Definition.IsBits) return false;
        for (var back = 1; back <= 3 && index - back >= 0; back++)
        {
            var c = PeekCell(index - back);
            if (c is not null && c.Format.Registers() > back) return true;
        }
        return false;
    }

    public double NumericValue(int index, ushort[]? values = null)
    {
        values ??= Definition.IsWriteWindow ? WriteValues : Values;
        if (index < 0 || index >= values.Length) return double.NaN;
        if (Definition.IsBits) return values[index];
        var cell = PeekCell(index);
        var format = cell?.Format ?? DisplayFormat.Signed;
        if (values.Length - index < format.Registers()) return double.NaN;
        var raw = RegisterCodec.Decode(values.AsSpan(index), format.Kind(), format.Order());
        return cell?.Scaling?.Apply(raw) ?? raw;
    }

    public PollDocument CloneDefinitionOnly()
    {
        var copy = new PollDocument { Title = Title, RowsMode = RowsMode, Definition = Definition.Clone() };
        lock (_sync)
            foreach (var (k, v) in Cells) copy.Cells[k] = v.Clone();
        return copy;
    }
}
