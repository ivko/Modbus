using System.Globalization;
using System.IO;
using System.Text;
using ModbusMaster.Core;
using ModbusMaster.Core.Polling;

namespace ModbusMaster.Ui.Views;

public sealed class LogOptions
{
    public string Path { get; set; } = "";
    /// <summary>0 = log every poll; otherwise at most once per this many seconds.</summary>
    public int IntervalSeconds { get; set; }
    public bool OnlyWhenChanged { get; set; }
    public bool LogErrors { get; set; } = true;
    public bool ErrorsOnly { get; set; }
    public string Delimiter { get; set; } = ";";
    public bool NewFileAtMidnight { get; set; }
}

/// <summary>Writes one CSV row per poll of a data window (Setup > Log).</summary>
public sealed class DataLogger : IDisposable
{
    private readonly PollDocument _doc;
    private readonly object _lock = new();
    private StreamWriter? _writer;
    private string _currentPath = "";
    private DateTime _lastWrite = DateTime.MinValue;
    private string? _lastValues;

    public DataLogger(PollDocument doc, LogOptions options)
    {
        _doc = doc;
        Options = options;
        Open(DateTime.Now);
        doc.Updated += OnUpdated;
    }

    public LogOptions Options { get; }
    public long Rows { get; private set; }

    private string PathFor(DateTime day)
    {
        if (!Options.NewFileAtMidnight) return Options.Path;
        var dir = System.IO.Path.GetDirectoryName(Options.Path) ?? "";
        var name = System.IO.Path.GetFileNameWithoutExtension(Options.Path);
        var ext = System.IO.Path.GetExtension(Options.Path);
        return System.IO.Path.Combine(dir, $"{name}_{day:yyyy-MM-dd}{ext}");
    }

    private void Open(DateTime now)
    {
        _currentPath = PathFor(now);
        var isNew = !File.Exists(_currentPath) || new FileInfo(_currentPath).Length == 0;
        _writer = new StreamWriter(new FileStream(_currentPath, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
        if (isNew)
        {
            var d = _doc.Definition;
            var header = new StringBuilder("Time");
            for (var i = 0; i < d.CellCount; i++)
            {
                if (_doc.IsContinuation(i)) continue;
                var name = _doc.PeekCell(i)?.Name;
                header.Append(Options.Delimiter).Append(string.IsNullOrEmpty(name) ? (d.Address + i).ToString(CultureInfo.InvariantCulture) : name);
            }
            header.Append(Options.Delimiter).Append("Status");
            _writer.WriteLine(header.ToString());
            _writer.Flush();
        }
    }

    private void OnUpdated(PollDocument doc)
    {
        var now = DateTime.Now;
        var ok = doc.LastResult == ModbusResult.Success;
        if (!ok && !Options.LogErrors) return;
        if (ok && Options.ErrorsOnly) return;
        if (Options.IntervalSeconds > 0 && (now - _lastWrite).TotalSeconds < Options.IntervalSeconds) return;

        var values = doc.Definition.IsWriteWindow ? doc.WriteValues : doc.Values;
        var sb = new StringBuilder();
        for (var i = 0; i < doc.Definition.CellCount; i++)
        {
            if (doc.IsContinuation(i)) continue;
            sb.Append(Options.Delimiter);
            if (ok && i < values.Length) sb.Append(doc.FormatCell(i, values));
        }
        var line = sb.ToString();
        if (ok && Options.OnlyWhenChanged && line == _lastValues) return;

        lock (_lock)
        {
            if (_writer is null) return;
            if (Options.NewFileAtMidnight && PathFor(now) != _currentPath)
            {
                _writer.Dispose();
                Open(now);
            }
            _writer.WriteLine(now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + line + Options.Delimiter +
                              (ok ? "OK" : ModbusResults.Describe(doc.LastResult)));
            _writer.Flush();
            Rows++;
        }
        _lastWrite = now;
        if (ok) _lastValues = line;
    }

    public void Dispose()
    {
        _doc.Updated -= OnUpdated;
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
