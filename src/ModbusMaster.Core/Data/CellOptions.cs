using System.Globalization;

namespace ModbusMaster.Core.Data;

/// <summary>Linear scaling: the register range [RawMin..RawMax] maps to [ScaledMin..ScaledMax].</summary>
public sealed class Scaling
{
    public double RawMin { get; set; }
    public double RawMax { get; set; } = 65535;
    public double ScaledMin { get; set; }
    public double ScaledMax { get; set; } = 100;
    public int Decimals { get; set; } = 2;

    public double Apply(double raw) =>
        RawMax == RawMin ? ScaledMin : ScaledMin + (raw - RawMin) * (ScaledMax - ScaledMin) / (RawMax - RawMin);

    public double Reverse(double scaled) =>
        ScaledMax == ScaledMin ? RawMin : RawMin + (scaled - ScaledMin) * (RawMax - RawMin) / (ScaledMax - ScaledMin);

    public string Format(double raw) => Apply(raw).ToString("F" + Math.Clamp(Decimals, 0, 10), CultureInfo.InvariantCulture);

    public Scaling Clone() => (Scaling)MemberwiseClone();
}

/// <summary>Color rule operators. Numbers match the automation function ColorsSetRules.</summary>
public enum ColorRule
{
    NotUsed = 0,
    EqualTo = 1,
    GreaterThan = 2,
    LessThan = 3,
    GreaterThanOrEqualTo = 4,
    LessThanOrEqualTo = 5,
    And = 6,
}

/// <summary>Conditional colors for one value cell: normal colors plus two rules.</summary>
public sealed class ConditionalColors
{
    /// <summary>Colors as 0xRRGGBB, in the automation ID order 0..5: normal bg/fg, rule 1 bg/fg, rule 2 bg/fg.</summary>
    public int[] Colors { get; set; } = [0xFFFFFF, 0x000000, 0xFF0000, 0x000000, 0x00FF00, 0x000000];

    public ColorRule Rule1 { get; set; }
    public ColorRule Rule2 { get; set; }
    public double Compare1 { get; set; }
    public double Compare2 { get; set; }

    public bool IsActive => Rule1 != ColorRule.NotUsed || Rule2 != ColorRule.NotUsed || Colors[0] != 0xFFFFFF || Colors[1] != 0;

    /// <summary>Returns (background, foreground) for the value. Rule 1 wins over rule 2.</summary>
    public (int Background, int Foreground) Evaluate(double value)
    {
        if (Matches(Rule1, value, Compare1)) return (Colors[2], Colors[3]);
        if (Matches(Rule2, value, Compare2)) return (Colors[4], Colors[5]);
        return (Colors[0], Colors[1]);
    }

    private static bool Matches(ColorRule rule, double value, double compare) => rule switch
    {
        ColorRule.EqualTo => value == compare,
        ColorRule.GreaterThan => value > compare,
        ColorRule.LessThan => value < compare,
        ColorRule.GreaterThanOrEqualTo => value >= compare,
        ColorRule.LessThanOrEqualTo => value <= compare,
        ColorRule.And => ((long)value & (long)compare) != 0,
        _ => false,
    };

    public ConditionalColors Clone() => new()
    {
        Colors = (int[])Colors.Clone(),
        Rule1 = Rule1,
        Rule2 = Rule2,
        Compare1 = Compare1,
        Compare2 = Compare2,
    };
}

/// <summary>A text shown instead of a numeric value, e.g. 0 = "Stopped", 1 = "Running".</summary>
public sealed class ValueName
{
    public long Value { get; set; }
    public string Name { get; set; } = "";
}
