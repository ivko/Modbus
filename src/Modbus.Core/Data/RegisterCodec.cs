using System.Buffers.Binary;
using System.Globalization;

namespace Modbus.Core.Data;

/// <summary>Converts between 16-bit registers and typed values for every byte order.</summary>
public static class RegisterCodec
{
    /// <summary>Wire bytes for <paramref name="registers"/> rearranged to big-endian value order.</summary>
    public static byte[] ToValueBytes(ReadOnlySpan<ushort> registers, ByteOrder order)
    {
        var bytes = new byte[registers.Length * 2];
        for (var i = 0; i < registers.Length; i++)
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(i * 2), registers[i]);
        Rearrange(bytes, order);
        return bytes;
    }

    public static ushort[] FromValueBytes(byte[] valueBytes, ByteOrder order)
    {
        var bytes = (byte[])valueBytes.Clone();
        Rearrange(bytes, order); // the transform is its own inverse
        var registers = new ushort[bytes.Length / 2];
        for (var i = 0; i < registers.Length; i++)
            registers[i] = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(i * 2));
        return registers;
    }

    private static void Rearrange(byte[] bytes, ByteOrder order)
    {
        var swapBytes = order is ByteOrder.BigEndianByteSwap or ByteOrder.LittleEndian;
        var reverseWords = order is ByteOrder.LittleEndian or ByteOrder.LittleEndianByteSwap;
        if (swapBytes)
            for (var i = 0; i + 1 < bytes.Length; i += 2)
                (bytes[i], bytes[i + 1]) = (bytes[i + 1], bytes[i]);
        if (reverseWords)
        {
            var words = bytes.Length / 2;
            for (var i = 0; i < words / 2; i++)
            {
                var a = i * 2;
                var b = (words - 1 - i) * 2;
                (bytes[a], bytes[b]) = (bytes[b], bytes[a]);
                (bytes[a + 1], bytes[b + 1]) = (bytes[b + 1], bytes[a + 1]);
            }
        }
    }

    /// <summary>Decodes a numeric value. Hex and binary formats return the unsigned register value.</summary>
    public static double Decode(ReadOnlySpan<ushort> registers, ValueKind kind, ByteOrder order)
    {
        switch (kind)
        {
            case ValueKind.Int16: return (short)registers[0];
            case ValueKind.UInt16 or ValueKind.Hex or ValueKind.Binary: return registers[0];
        }
        var b = ToValueBytes(registers[..RegisterCount(kind)], order);
        return kind switch
        {
            ValueKind.Int32 => BinaryPrimitives.ReadInt32BigEndian(b),
            ValueKind.UInt32 => BinaryPrimitives.ReadUInt32BigEndian(b),
            ValueKind.Int64 => BinaryPrimitives.ReadInt64BigEndian(b),
            ValueKind.UInt64 => BinaryPrimitives.ReadUInt64BigEndian(b),
            ValueKind.Float => BinaryPrimitives.ReadSingleBigEndian(b),
            _ => BinaryPrimitives.ReadDoubleBigEndian(b),
        };
    }

    public static ushort[] Encode(double value, ValueKind kind, ByteOrder order)
    {
        switch (kind)
        {
            case ValueKind.Int16:
                CheckRange(value, short.MinValue, ushort.MaxValue);
                return [unchecked((ushort)(long)Math.Round(value))];
            case ValueKind.UInt16 or ValueKind.Hex or ValueKind.Binary:
                CheckRange(value, short.MinValue, ushort.MaxValue);
                return [unchecked((ushort)(long)Math.Round(value))];
        }

        var b = new byte[RegisterCount(kind) * 2];
        switch (kind)
        {
            case ValueKind.Int32:
                CheckRange(value, int.MinValue, uint.MaxValue);
                BinaryPrimitives.WriteInt32BigEndian(b, unchecked((int)(long)Math.Round(value)));
                break;
            case ValueKind.UInt32:
                CheckRange(value, int.MinValue, uint.MaxValue);
                BinaryPrimitives.WriteUInt32BigEndian(b, unchecked((uint)(long)Math.Round(value)));
                break;
            case ValueKind.Int64:
                BinaryPrimitives.WriteInt64BigEndian(b, (long)Math.Round(value));
                break;
            case ValueKind.UInt64:
                BinaryPrimitives.WriteUInt64BigEndian(b, (ulong)Math.Round(value));
                break;
            case ValueKind.Float:
                BinaryPrimitives.WriteSingleBigEndian(b, (float)value);
                break;
            default:
                BinaryPrimitives.WriteDoubleBigEndian(b, value);
                break;
        }
        return FromValueBytes(b, order);
    }

    public static int RegisterCount(ValueKind kind) => kind switch
    {
        ValueKind.Int32 or ValueKind.UInt32 or ValueKind.Float => 2,
        ValueKind.Int64 or ValueKind.UInt64 or ValueKind.Double => 4,
        _ => 1,
    };

    /// <summary>Formats registers for display (no scaling).</summary>
    public static string Format(ReadOnlySpan<ushort> registers, DisplayFormat format)
    {
        var kind = format.Kind();
        if (registers.Length < RegisterCount(kind)) return "-";
        switch (kind)
        {
            case ValueKind.Hex: return "0x" + registers[0].ToString("X4", CultureInfo.InvariantCulture);
            case ValueKind.Binary:
                var bits = Convert.ToString(registers[0], 2).PadLeft(16, '0');
                return $"{bits[..4]} {bits[4..8]} {bits[8..12]} {bits[12..]}";
            case ValueKind.Int64:
                return BinaryPrimitives.ReadInt64BigEndian(ToValueBytes(registers[..4], format.Order())).ToString(CultureInfo.InvariantCulture);
            case ValueKind.UInt64:
                return BinaryPrimitives.ReadUInt64BigEndian(ToValueBytes(registers[..4], format.Order())).ToString(CultureInfo.InvariantCulture);
            case ValueKind.Float:
                return ((float)Decode(registers, kind, format.Order())).ToString("G7", CultureInfo.InvariantCulture);
            case ValueKind.Double:
                return Decode(registers, kind, format.Order()).ToString("G15", CultureInfo.InvariantCulture);
            default:
                return Decode(registers, kind, format.Order()).ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Parses user input for a write ("123", "-5", "0x1F", "0b1010", "1.25").</summary>
    public static ushort[] Parse(string text, DisplayFormat format)
    {
        var kind = format.Kind();
        var t = text.Trim().Replace(" ", "");
        double value;
        if (kind is ValueKind.Float or ValueKind.Double)
        {
            value = double.Parse(t.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture);
        }
        else if (kind == ValueKind.Hex && !t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            value = long.Parse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }
        else if (kind == ValueKind.Binary && !t.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
        {
            value = Convert.ToInt64(t, 2);
        }
        else if (kind == ValueKind.UInt64 && ulong.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var u64))
        {
            var bytes = new byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(bytes, u64);
            return FromValueBytes(bytes, format.Order());
        }
        else if (kind == ValueKind.Int64 && long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s64))
        {
            var bytes = new byte[8];
            BinaryPrimitives.WriteInt64BigEndian(bytes, s64);
            return FromValueBytes(bytes, format.Order());
        }
        else
        {
            value = Protocol.Hex.ParseNumber(t);
        }
        return Encode(value, kind, format.Order());
    }

    private static void CheckRange(double value, double min, double max)
    {
        if (double.IsNaN(value) || value < min || value > max)
            throw new OverflowException($"Value {value} is out of range ({min} to {max}).");
    }
}
