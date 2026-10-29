using System.Globalization;

namespace ModbusMaster.Core.Protocol;

public static class Hex
{
    public static string Format(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return string.Empty;
        var chars = new char[data.Length * 3 - 1];
        for (var i = 0; i < data.Length; i++)
        {
            var s = data[i].ToString("X2", CultureInfo.InvariantCulture);
            chars[i * 3] = s[0];
            chars[i * 3 + 1] = s[1];
            if (i < data.Length - 1) chars[i * 3 + 2] = ' ';
        }
        return new string(chars);
    }

    /// <summary>Parses "01 03 00 0A", "01-03-00-0A" or "0103000A".</summary>
    public static byte[] Parse(string text)
    {
        var clean = new string(text.Where(Uri.IsHexDigit).ToArray());
        if (clean.Length % 2 != 0) throw new FormatException("Odd number of hex digits.");
        return Convert.FromHexString(clean);
    }

    /// <summary>Parses 123, -5, 0x1F, 1Fh or 0b1010.</summary>
    public static long ParseNumber(string text)
    {
        var s = text.Trim().Replace("_", "");
        var negative = s.StartsWith('-');
        if (negative) s = s[1..];
        long value;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            value = long.Parse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        else if (s.EndsWith('h') || s.EndsWith('H'))
            value = long.Parse(s[..^1], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        else if (s.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
            value = Convert.ToInt64(s[2..], 2);
        else
            value = long.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture);
        return negative ? -value : value;
    }
}
