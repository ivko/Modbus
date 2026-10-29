using ModbusMaster.Core.Protocol;

namespace ModbusMaster.Core.Polling;

/// <summary>
/// Runs every data window on one background thread. Windows share the connection, so requests are
/// serialized; each window is due again <c>ScanRate</c> ms after its previous request started.
/// </summary>
public sealed class PollScheduler : IDisposable
{
    private readonly object _sync = new();
    private readonly List<PollDocument> _documents = [];
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private ModbusClient? _client;
    private volatile bool _stopped;

    public PollScheduler()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Modbus poll scheduler" };
        _thread.Start();
    }

    /// <summary>Raised when a request fails because the connection is not open or was lost.</summary>
    public event Action<Exception>? ConnectionError;

    public ModbusClient? Client
    {
        get { lock (_sync) return _client; }
        set
        {
            lock (_sync)
            {
                _client = value;
                foreach (var d in _documents) d.NextDueUtc = DateTime.MinValue;
            }
            _wake.Set();
        }
    }

    public IReadOnlyList<PollDocument> Documents
    {
        get { lock (_sync) return _documents.ToArray(); }
    }

    public void Add(PollDocument document)
    {
        lock (_sync)
        {
            if (!_documents.Contains(document)) _documents.Add(document);
            document.NextDueUtc = DateTime.MinValue;
        }
        _wake.Set();
    }

    public void Remove(PollDocument document)
    {
        lock (_sync) _documents.Remove(document);
    }

    /// <summary>Call after a definition change so the window polls immediately.</summary>
    public void Kick(PollDocument? document = null)
    {
        if (document is not null) document.NextDueUtc = DateTime.MinValue;
        _wake.Set();
    }

    private void Run()
    {
        while (!_stopped)
        {
            PollDocument? next = null;
            ModbusClient? client;
            var wait = TimeSpan.FromMilliseconds(500);
            lock (_sync)
            {
                client = _client;
                var now = DateTime.UtcNow;
                if (client is not null)
                {
                    foreach (var d in _documents)
                    {
                        var active = !d.Definition.Disabled || d.OnceRequested;
                        if (!active) continue;
                        if (d.Definition.Validate().Length > 0) continue;
                        if (next is null || d.NextDueUtc < next.NextDueUtc) next = d;
                    }
                    if (next is not null && next.NextDueUtc > now)
                    {
                        wait = next.NextDueUtc - now;
                        next = null;
                    }
                }
            }

            if (next is null || client is null)
            {
                _wake.WaitOne(wait > TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : wait);
                continue;
            }

            var started = DateTime.UtcNow;
            next.OnceRequested = false;
            Execute(client, next);
            var scan = Math.Max(0, next.Definition.ScanRateMs);
            next.NextDueUtc = started.AddMilliseconds(scan);
        }
    }

    /// <summary>Performs the window's request once on the calling thread.</summary>
    public static void Execute(ModbusClient client, PollDocument doc, Action<Exception>? connectionError = null)
    {
        var def = doc.Definition.Clone();
        try
        {
            ushort[]? values = null;
            switch (def.Function)
            {
                case FunctionCode.ReadCoils:
                case FunctionCode.ReadDiscreteInputs:
                case FunctionCode.ReadHoldingRegisters:
                case FunctionCode.ReadInputRegisters:
                    values = client.Read(def.SlaveId, def.Function, def.Address, def.Quantity);
                    break;
                case FunctionCode.WriteSingleCoil:
                    client.WriteSingleCoil(def.SlaveId, def.Address, doc.WriteValues[0] != 0);
                    break;
                case FunctionCode.WriteSingleRegister:
                    client.WriteSingleRegister(def.SlaveId, def.Address, doc.WriteValues[0]);
                    break;
                case FunctionCode.WriteMultipleCoils:
                    client.WriteMultipleCoils(def.SlaveId, def.Address, doc.WriteValues.Select(v => v != 0).ToArray());
                    break;
                case FunctionCode.WriteMultipleRegisters:
                    client.WriteMultipleRegisters(def.SlaveId, def.Address, doc.WriteValues);
                    break;
                case FunctionCode.ReadWriteMultipleRegisters:
                    values = client.ReadWriteMultipleRegisters(def.SlaveId, def.Address, def.Quantity, def.WriteAddress, doc.WriteValues);
                    break;
                default:
                    throw new InvalidOperationException(def.Validate());
            }
            if (def.IsWriteWindow) doc.LastWriteResult = ModbusResult.Success;
            doc.Complete(values, ModbusResult.Success, "", responded: true);
        }
        catch (ModbusSlaveException ex)
        {
            if (def.IsWriteWindow) doc.LastWriteResult = ex.Result;
            doc.Complete(null, ex.Result, ex.Message, responded: true);
        }
        catch (ModbusConnectionException ex)
        {
            doc.Complete(null, ModbusResult.PortNotOpen, ex.Message, responded: false);
            connectionError?.Invoke(ex);
        }
        catch (ModbusCommunicationException ex)
        {
            if (def.IsWriteWindow) doc.LastWriteResult = ex.Result;
            var responded = ex is not ModbusTimeoutException;
            doc.Complete(null, ex.Result, ex.Message, responded);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            doc.Complete(null, ModbusResult.ReadError, ex.Message, responded: false);
        }
    }

    private void Execute(ModbusClient client, PollDocument doc) => Execute(client, doc, e => ConnectionError?.Invoke(e));

    public void Dispose()
    {
        _stopped = true;
        _wake.Set();
        _thread.Join(TimeSpan.FromSeconds(2));
        _wake.Dispose();
    }
}
