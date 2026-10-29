using System.Windows.Input;
using ModbusMaster.Core;

namespace ModbusMaster.Ui;

/// <summary>Where the Communication Traffic window gets its frames from.</summary>
public interface ITrafficSource
{
    TrafficEntry[] TrafficSnapshot();
    void ClearTraffic();
    event Action<TrafficEntry>? TrafficAdded;
}

/// <summary>Keeps the last frames of a connection for the traffic window.</summary>
public sealed class TrafficBuffer : ITrafficSource
{
    private readonly object _lock = new();
    private readonly LinkedList<TrafficEntry> _entries = new();

    public int Capacity { get; init; } = 20000;
    public bool Enabled { get; set; } = true;

    public event Action<TrafficEntry>? TrafficAdded;

    public void Add(TrafficEntry entry)
    {
        if (!Enabled) return;
        lock (_lock)
        {
            _entries.AddLast(entry);
            while (_entries.Count > Capacity) _entries.RemoveFirst();
        }
        TrafficAdded?.Invoke(entry);
    }

    public TrafficEntry[] TrafficSnapshot()
    {
        lock (_lock) return _entries.ToArray();
    }

    public void ClearTraffic()
    {
        lock (_lock) _entries.Clear();
    }
}

public sealed class RelayCommand(Action action) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => action();
}
