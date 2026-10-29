using System.Globalization;
using ModbusMaster.Core.Protocol;

namespace ModbusMaster.Cli;

/// <summary>Parses "--name value" pairs and "--flag" switches.</summary>
public sealed class Options
{
    private static readonly HashSet<string> Flags =
    [
        "json", "watch", "log", "coils", "insecure", "remove-echo", "ipv6", "simulate", "plain", "quiet", "all",
    ];

    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);

    public static Options Parse(IEnumerable<string> args)
    {
        var o = new Options();
        var list = args.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var a = list[i];
            if (!a.StartsWith("--")) throw new FormatException($"Unexpected argument '{a}'. Options start with --.");
            var name = a[2..];
            var eq = name.IndexOf('=');
            if (eq > 0)
            {
                o._values[name[..eq]] = name[(eq + 1)..];
                continue;
            }
            if (Flags.Contains(name))
            {
                o._flags.Add(name);
                continue;
            }
            if (i + 1 >= list.Count) throw new FormatException($"--{name} needs a value.");
            o._values[name] = list[++i];
        }
        return o;
    }

    public bool Flag(string name) => _flags.Contains(name);

    public string? Value(string name) => _values.GetValueOrDefault(name);

    public int Int(string name, int fallback) =>
        Value(name) is { } v ? checked((int)Hex.ParseNumber(v)) : fallback;

    public byte Byte(string name, byte fallback)
    {
        if (Value(name) is not { } v) return fallback;
        var n = Hex.ParseNumber(v);
        if (n is < 0 or > 255) throw new FormatException($"--{name} must be 0 to 255.");
        return (byte)n;
    }

    public ushort UShort(string name, ushort fallback)
    {
        if (Value(name) is not { } v) return fallback;
        var n = Hex.ParseNumber(v);
        if (n is < 0 or > 65535) throw new FormatException($"--{name} must be 0 to 65535.");
        return (ushort)n;
    }

    /// <summary>"5" or "1-247".</summary>
    public (int From, int To) Range(string name, int defaultFrom, int defaultTo)
    {
        if (Value(name) is not { } v) return (defaultFrom, defaultTo);
        var parts = v.Split('-', 2);
        var from = int.Parse(parts[0], CultureInfo.InvariantCulture);
        var to = parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : from;
        if (to < from) throw new FormatException($"--{name}: the end of the range is before the start.");
        return (from, to);
    }
}
