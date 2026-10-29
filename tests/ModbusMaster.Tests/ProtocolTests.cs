using ModbusMaster.Core.Data;
using ModbusMaster.Core.Framing;
using ModbusMaster.Core.Protocol;

namespace ModbusMaster.Tests;

public class ProtocolTests
{
    [Fact]
    public void Crc16_matches_specification_example()
    {
        // Read 10 holding registers from slave 1, address 0: CRC C5 CD (low byte first).
        var frame = RtuFramer.Encode(1, Pdu.Read(FunctionCode.ReadHoldingRegisters, 0, 10));
        Assert.Equal("01 03 00 00 00 0A C5 CD", Hex.Format(frame));
        Assert.True(RtuFramer.CheckCrc(frame));
    }

    [Fact]
    public void Lrc_and_ascii_frame()
    {
        var frame = AsciiFramer.Encode(1, Pdu.Read(FunctionCode.ReadHoldingRegisters, 0, 1));
        Assert.Equal(":010300000001FB\r\n", System.Text.Encoding.ASCII.GetString(frame));
        Assert.Equal(new byte[] { 1, 3, 0, 0, 0, 1 }, AsciiFramer.Decode(frame));
    }

    [Fact]
    public void Mbap_header()
    {
        var frame = MbapFramer.Encode(0x0102, 7, Pdu.WriteSingleRegister(1, 0xABCD));
        Assert.Equal("01 02 00 00 00 06 07 06 00 01 AB CD", Hex.Format(frame));
    }

    [Theory]
    [InlineData(ByteOrder.BigEndian, 0x075B, 0xCD15)]
    [InlineData(ByteOrder.LittleEndianByteSwap, 0xCD15, 0x075B)]
    [InlineData(ByteOrder.BigEndianByteSwap, 0x5B07, 0x15CD)]
    [InlineData(ByteOrder.LittleEndian, 0x15CD, 0x5B07)]
    public void Int32_byte_orders_follow_manual_example(ByteOrder order, int r0, int r1)
    {
        var regs = RegisterCodec.Encode(123456789, ValueKind.Int32, order);
        Assert.Equal(new[] { (ushort)r0, (ushort)r1 }, regs);
        Assert.Equal(123456789, RegisterCodec.Decode(regs, ValueKind.Int32, order));
    }

    [Fact]
    public void Float_and_double_round_trip()
    {
        Assert.Equal(new ushort[] { 0x3F80, 0x0000 }, RegisterCodec.Encode(1.0, ValueKind.Float, ByteOrder.BigEndian));
        foreach (var order in Enum.GetValues<ByteOrder>())
        {
            var f = RegisterCodec.Encode(-12.5, ValueKind.Float, order);
            Assert.Equal(-12.5, RegisterCodec.Decode(f, ValueKind.Float, order));
            var d = RegisterCodec.Encode(Math.PI, ValueKind.Double, order);
            Assert.Equal(Math.PI, RegisterCodec.Decode(d, ValueKind.Double, order));
        }
    }

    [Fact]
    public void Display_format_ids_match_automation_table()
    {
        Assert.Equal(5, (int)DisplayFormats.Of(ValueKind.Float, ByteOrder.BigEndian));
        Assert.Equal(4, (int)DisplayFormats.Of(ValueKind.Float, ByteOrder.LittleEndianByteSwap));
        Assert.Equal(9, (int)DisplayFormats.Of(ValueKind.Int32, ByteOrder.BigEndian));
        Assert.Equal(28, (int)DisplayFormats.Of(ValueKind.UInt64, ByteOrder.LittleEndian));
        Assert.Equal(4, DisplayFormat.DoubleBigEndian.Registers());
        Assert.Equal("32-bit Float Big-endian (ABCD)", DisplayFormat.FloatBigEndian.Label());
    }

    [Fact]
    public void Parse_user_values()
    {
        Assert.Equal(new ushort[] { 0xFFFB }, RegisterCodec.Parse("-5", DisplayFormat.Signed));
        Assert.Equal(new ushort[] { 0x001F }, RegisterCodec.Parse("1F", DisplayFormat.Hex));
        Assert.Equal(new ushort[] { 0x000A }, RegisterCodec.Parse("1010", DisplayFormat.Binary));
        Assert.Equal(new ushort[] { 0x3FA0, 0x0000 }, RegisterCodec.Parse("1,25", DisplayFormat.FloatBigEndian));
        Assert.Equal(new ushort[] { 0xFFFF, 0xFFFF, 0xFFFF, 0xFFFF }, RegisterCodec.Parse("18446744073709551615", DisplayFormat.UInt64BigEndian));
        Assert.Throws<OverflowException>(() => RegisterCodec.Parse("70000", DisplayFormat.Unsigned));
    }

    [Fact]
    public void Scaling_maps_linearly()
    {
        var s = new Scaling { RawMin = 0, RawMax = 4000, ScaledMin = 4, ScaledMax = 20, Decimals = 1 };
        Assert.Equal("12.0", s.Format(2000));
        Assert.Equal(2000, s.Reverse(12));
    }

    [Fact]
    public void Conditional_colors_rule1_wins()
    {
        var c = new ConditionalColors { Rule1 = ColorRule.GreaterThan, Compare1 = 100, Rule2 = ColorRule.EqualTo, Compare2 = 200 };
        Assert.Equal((0xFF0000, 0), c.Evaluate(200));
        Assert.Equal((0xFFFFFF, 0), c.Evaluate(50));
    }
}
