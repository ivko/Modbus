using System.Windows.Media;

namespace ModbusMaster.Ui;

public static class ColorUtil
{
    public static Color FromRgb(int rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    public static int ToRgb(Color c) => (c.R << 16) | (c.G << 8) | c.B;

    public static SolidColorBrush Brush(int rgb)
    {
        var b = new SolidColorBrush(FromRgb(rgb));
        b.Freeze();
        return b;
    }

    public static readonly (string Name, int Rgb)[] Palette =
    [
        ("White", 0xFFFFFF), ("Black", 0x000000), ("Red", 0xFF0000), ("Lime", 0x00FF00), ("Green", 0x008000),
        ("Blue", 0x0000FF), ("Yellow", 0xFFFF00), ("Orange", 0xFFA500), ("Cyan", 0x00FFFF), ("Magenta", 0xFF00FF),
        ("Silver", 0xC0C0C0), ("Gray", 0x808080), ("Maroon", 0x800000), ("Olive", 0x808000), ("Purple", 0x800080),
        ("Teal", 0x008080), ("Navy", 0x000080), ("Light green", 0xC8F0C8), ("Light red", 0xF8C8C8), ("Light yellow", 0xFFF8C0),
    ];
}
