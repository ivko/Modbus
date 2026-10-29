namespace Modbus.Core.Data;

/// <summary>Register byte order. Numbers match the automation property ByteOrder.</summary>
/// <remarks>
/// For the 32-bit value 0x075BCD15 the wire bytes are:
/// BigEndian 07 5B CD 15 (ABCD), LittleEndianByteSwap CD 15 07 5B (CDAB),
/// BigEndianByteSwap 5B 07 15 CD (BADC), LittleEndian 15 CD 5B 07 (DCBA).
/// </remarks>
public enum ByteOrder
{
    BigEndian = 0,
    LittleEndianByteSwap = 1,
    BigEndianByteSwap = 2,
    LittleEndian = 3,
}

public enum ValueKind { Int16, UInt16, Hex, Binary, Int32, UInt32, Int64, UInt64, Float, Double }

/// <summary>Cell display format. Numbers match the automation functions SetFormat/GetFormat/FormatAll.</summary>
public enum DisplayFormat
{
    Signed = 0,
    Unsigned = 1,
    Hex = 2,
    Binary = 3,
    FloatLittleEndianByteSwap = 4,
    FloatBigEndian = 5,
    DoubleLittleEndianByteSwap = 6,
    DoubleBigEndian = 7,
    Int32LittleEndianByteSwap = 8,
    Int32BigEndian = 9,
    FloatLittleEndian = 10,
    FloatBigEndianByteSwap = 11,
    DoubleLittleEndian = 12,
    DoubleBigEndianByteSwap = 13,
    Int32LittleEndian = 14,
    Int32BigEndianByteSwap = 15,
    UInt32BigEndian = 17,
    UInt32LittleEndianByteSwap = 18,
    UInt32BigEndianByteSwap = 19,
    UInt32LittleEndian = 20,
    Int64BigEndian = 21,
    Int64LittleEndianByteSwap = 22,
    Int64BigEndianByteSwap = 23,
    Int64LittleEndian = 24,
    UInt64BigEndian = 25,
    UInt64LittleEndianByteSwap = 26,
    UInt64BigEndianByteSwap = 27,
    UInt64LittleEndian = 28,
}

public static class DisplayFormats
{
    private static readonly Dictionary<(ValueKind, ByteOrder), DisplayFormat> Lookup = BuildLookup();

    public static IReadOnlyList<DisplayFormat> All { get; } = Enum.GetValues<DisplayFormat>();

    public static bool IsDefined(int id) => Enum.IsDefined(typeof(DisplayFormat), id);

    public static ValueKind Kind(this DisplayFormat f) => f switch
    {
        DisplayFormat.Signed => ValueKind.Int16,
        DisplayFormat.Unsigned => ValueKind.UInt16,
        DisplayFormat.Hex => ValueKind.Hex,
        DisplayFormat.Binary => ValueKind.Binary,
        _ when f.ToString().StartsWith("Float") => ValueKind.Float,
        _ when f.ToString().StartsWith("Double") => ValueKind.Double,
        _ when f.ToString().StartsWith("Int32") => ValueKind.Int32,
        _ when f.ToString().StartsWith("UInt32") => ValueKind.UInt32,
        _ when f.ToString().StartsWith("Int64") => ValueKind.Int64,
        _ => ValueKind.UInt64,
    };

    public static ByteOrder Order(this DisplayFormat f)
    {
        var name = f.ToString();
        if (name.EndsWith("LittleEndianByteSwap")) return ByteOrder.LittleEndianByteSwap;
        if (name.EndsWith("BigEndianByteSwap")) return ByteOrder.BigEndianByteSwap;
        if (name.EndsWith("LittleEndian")) return ByteOrder.LittleEndian;
        return ByteOrder.BigEndian;
    }

    /// <summary>Number of 16-bit registers one value occupies.</summary>
    public static int Registers(this DisplayFormat f) => f.Kind() switch
    {
        ValueKind.Int32 or ValueKind.UInt32 or ValueKind.Float => 2,
        ValueKind.Int64 or ValueKind.UInt64 or ValueKind.Double => 4,
        _ => 1,
    };

    public static DisplayFormat Of(ValueKind kind, ByteOrder order) => kind switch
    {
        ValueKind.Int16 => DisplayFormat.Signed,
        ValueKind.UInt16 => DisplayFormat.Unsigned,
        ValueKind.Hex => DisplayFormat.Hex,
        ValueKind.Binary => DisplayFormat.Binary,
        _ => Lookup[(kind, order)],
    };

    public static string Label(this DisplayFormat f)
    {
        var kind = f.Kind() switch
        {
            ValueKind.Int16 => "Signed",
            ValueKind.UInt16 => "Unsigned",
            ValueKind.Hex => "Hex",
            ValueKind.Binary => "Binary",
            ValueKind.Int32 => "32-bit Signed",
            ValueKind.UInt32 => "32-bit Unsigned",
            ValueKind.Int64 => "64-bit Signed",
            ValueKind.UInt64 => "64-bit Unsigned",
            ValueKind.Float => "32-bit Float",
            _ => "64-bit Double",
        };
        if (f.Registers() == 1) return kind;
        return kind + " " + OrderLabel(f.Order());
    }

    public static string OrderLabel(ByteOrder o) => o switch
    {
        ByteOrder.BigEndian => "Big-endian (ABCD)",
        ByteOrder.LittleEndian => "Little-endian (DCBA)",
        ByteOrder.BigEndianByteSwap => "Big-endian byte swap (BADC)",
        _ => "Little-endian byte swap (CDAB)",
    };

    /// <summary>Short CLI names: int16, uint16, hex, bin, int32, uint32, int64, uint64, float, double.</summary>
    public static ValueKind ParseKind(string name) => name.ToLowerInvariant() switch
    {
        "int16" or "signed" or "s16" => ValueKind.Int16,
        "uint16" or "unsigned" or "u16" => ValueKind.UInt16,
        "hex" => ValueKind.Hex,
        "bin" or "binary" => ValueKind.Binary,
        "int32" or "s32" => ValueKind.Int32,
        "uint32" or "u32" => ValueKind.UInt32,
        "int64" or "s64" => ValueKind.Int64,
        "uint64" or "u64" => ValueKind.UInt64,
        "float" or "float32" or "real" => ValueKind.Float,
        "double" or "float64" => ValueKind.Double,
        _ => throw new FormatException($"Unknown format '{name}'. Use int16, uint16, hex, bin, int32, uint32, int64, uint64, float or double."),
    };

    /// <summary>CLI byte order names: abcd, dcba, badc, cdab (or be, le, be-swap, le-swap).</summary>
    public static ByteOrder ParseOrder(string name) => name.ToLowerInvariant() switch
    {
        "abcd" or "be" or "big" => ByteOrder.BigEndian,
        "dcba" or "le" or "little" => ByteOrder.LittleEndian,
        "badc" or "be-swap" or "big-swap" => ByteOrder.BigEndianByteSwap,
        "cdab" or "le-swap" or "little-swap" or "word-swap" => ByteOrder.LittleEndianByteSwap,
        _ => throw new FormatException($"Unknown byte order '{name}'. Use abcd, dcba, badc or cdab."),
    };

    private static Dictionary<(ValueKind, ByteOrder), DisplayFormat> BuildLookup()
    {
        var map = new Dictionary<(ValueKind, ByteOrder), DisplayFormat>();
        foreach (var f in Enum.GetValues<DisplayFormat>())
            if (f.Registers() > 1) map[(f.Kind(), f.Order())] = f;
        return map;
    }
}
