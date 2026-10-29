using System.Text.Json;
using System.Text.Json.Serialization;

namespace Modbus.Core.Polling;

/// <summary>A saved session: the connection plus every data window (*.mmw), or a single window (*.mmp).</summary>
public sealed class Workspace
{
    public const int CurrentVersion = 1;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public int Version { get; set; } = CurrentVersion;
    public ConnectionSettings? Connection { get; set; }
    public List<PollDocument> Windows { get; set; } = [];

    public void Save(string path)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }

    public static Workspace Load(string path)
    {
        var ws = JsonSerializer.Deserialize<Workspace>(File.ReadAllText(path), JsonOptions)
                 ?? throw new InvalidDataException("Empty workspace file.");
        if (ws.Version > CurrentVersion)
            throw new InvalidDataException("This file was saved by a newer version of Modbus Master.");
        foreach (var w in ws.Windows)
        {
            // Drop default entries left from editing.
            foreach (var key in w.Cells.Where(kv => kv.Value.IsDefault).Select(kv => kv.Key).ToList()) w.Cells.Remove(key);
        }
        return ws;
    }

    public static ConnectionSettings LoadConnection(string path)
    {
        var text = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(text);
        if (doc.RootElement.TryGetProperty("Connection", out var conn))
            return conn.Deserialize<ConnectionSettings>(JsonOptions) ?? new ConnectionSettings();
        return JsonSerializer.Deserialize<ConnectionSettings>(text, JsonOptions) ?? new ConnectionSettings();
    }
}
