namespace Modbus.Core.Protocol;

public static class Checksums
{
    /// <summary>Modbus RTU CRC-16 (polynomial 0xA001, init 0xFFFF). Sent low byte first.</summary>
    public static ushort Crc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
            {
                var lsb = (crc & 1) != 0;
                crc >>= 1;
                if (lsb) crc ^= 0xA001;
            }
        }
        return crc;
    }

    /// <summary>Modbus ASCII LRC: two's complement of the 8-bit sum.</summary>
    public static byte Lrc(ReadOnlySpan<byte> data)
    {
        byte sum = 0;
        foreach (var b in data) sum = unchecked((byte)(sum + b));
        return unchecked((byte)(-sum));
    }
}
