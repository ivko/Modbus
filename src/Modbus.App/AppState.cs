using System.IO;
using System.Text.Json;
using Modbus.Core;
using Modbus.Core.Polling;

namespace Modbus.App;

/// <summary>Application-wide state shared by the windows and the automation interface.</summary>
public sealed class AppState : IDisposable, Modbus.Ui.ITrafficSource
{
    public const int TrafficCapacity = 20000;

    private readonly object _trafficLock = new();
    private readonly LinkedList<TrafficEntry> _traffic = new();
    private ModbusClient? _client;

    public static AppState Current { get; } = new();

    private AppState()
    {
        Scheduler.ConnectionError += ex => ConnectionLost?.Invoke(ex);
        Settings = LoadLastConnection() ?? new ConnectionSettings();
    }

    public PollScheduler Scheduler { get; } = new();

    public ConnectionSettings Settings { get; set; }

    public ModbusClient? Client => _client;

    public bool IsConnected => _client is not null;

    public bool TrafficCaptureEnabled { get; set; } = true;

    public event Action? ConnectionChanged;
    public event Action<Exception>? ConnectionLost;
    public event Action<TrafficEntry>? TrafficAdded;

    /// <summary>Opens the connection with <see cref="Settings"/>. Throws <see cref="ModbusConnectionException"/>.</summary>
    public void Connect()
    {
        Disconnect();
        var client = ModbusConnection.Create(Settings);
        client.Traffic += OnTraffic;
        try
        {
            client.Open();
        }
        catch
        {
            client.Dispose();
            throw;
        }
        _client = client;
        Scheduler.Client = client;
        SaveLastConnection(Settings);
        ConnectionChanged?.Invoke();
    }

    public void Disconnect()
    {
        if (_client is null) return;
        Scheduler.Client = null;
        _client.Traffic -= OnTraffic;
        _client.Dispose();
        _client = null;
        ConnectionChanged?.Invoke();
    }

    public TrafficEntry[] TrafficSnapshot()
    {
        lock (_trafficLock) return _traffic.ToArray();
    }

    public void ClearTraffic()
    {
        lock (_trafficLock) _traffic.Clear();
    }

    private void OnTraffic(TrafficEntry entry)
    {
        if (!TrafficCaptureEnabled) return;
        lock (_trafficLock)
        {
            _traffic.AddLast(entry);
            while (_traffic.Count > TrafficCapacity) _traffic.RemoveFirst();
        }
        TrafficAdded?.Invoke(entry);
    }

    // The last used connection is remembered per user.
    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModbusMaster", "last-connection.json");

    private static ConnectionSettings? LoadLastConnection()
    {
        try
        {
            return File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize<ConnectionSettings>(File.ReadAllText(SettingsPath), Workspace.JsonOptions)
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void SaveLastConnection(ConnectionSettings s)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var copy = s.Clone();
            copy.ClientCertificatePassword = null; // never store secrets
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(copy, Workspace.JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not important enough to bother the user.
        }
    }

    public void Dispose()
    {
        Disconnect();
        Scheduler.Dispose();
    }
}
