using System.IO;
using System.Text.Json;
using Modbus.Core;
using Modbus.Core.Polling;
using Modbus.Core.Server;
using Modbus.Core.Slave;
using Modbus.Ui;

namespace Modbus.Slave;

/// <summary>Application-wide state: the slave engine, the server and the traffic log.</summary>
public sealed class SlaveState : IDisposable
{
    private IModbusServer? _server;

    public static SlaveState Current { get; } = new();

    private SlaveState()
    {
        Settings = LoadLast() ?? new ServerSettings();
        ApplyEngineOptions();
    }

    public SlaveEngine Engine { get; } = new();
    public TrafficBuffer Traffic { get; } = new();
    public ServerSettings Settings { get; set; }

    public bool IsRunning => _server is not null;
    public string? Description => _server?.Description;

    public event Action? StateChanged;

    public void ApplyEngineOptions()
    {
        Engine.IgnoreUnitId = Settings.IgnoreUnitId;
        Engine.StrictAddressing = Settings.StrictAddressing;
    }

    /// <summary>Starts listening with <see cref="Settings"/>. Throws <see cref="ModbusConnectionException"/>.</summary>
    public void Start()
    {
        Stop();
        ApplyEngineOptions();
        IModbusServer server;
        try
        {
            server = Settings.CreateServer(Engine);
        }
        catch (Exception ex) when (ex is IOException or System.Security.Cryptography.CryptographicException)
        {
            throw new ModbusConnectionException($"Cannot load the server certificate: {ex.Message}", 13, ex);
        }
        server.Traffic += Traffic.Add;
        try
        {
            server.Start();
        }
        catch
        {
            server.Dispose();
            throw;
        }
        _server = server;
        SaveLast(Settings);
        StateChanged?.Invoke();
    }

    public void Stop()
    {
        if (_server is null) return;
        _server.Traffic -= Traffic.Add;
        _server.Dispose();
        _server = null;
        StateChanged?.Invoke();
    }

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModbusMaster", "last-slave-connection.json");

    private static ServerSettings? LoadLast()
    {
        try
        {
            return File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize<ServerSettings>(File.ReadAllText(SettingsPath), Workspace.JsonOptions)
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void SaveLast(ServerSettings s)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var copy = s.Clone();
            copy.Connection.ClientCertificatePassword = null;
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(copy, Workspace.JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not important enough to bother the user.
        }
    }

    public void Dispose()
    {
        Stop();
        Engine.Dispose();
    }
}
