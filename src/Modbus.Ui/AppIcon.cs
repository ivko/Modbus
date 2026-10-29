using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Modbus.Ui;

/// <summary>
/// The icon of the running program (blue "M" for the master, green "S" for the slave), loaded from the
/// app.ico resource of the program. Every top-level window of the program uses it.
/// </summary>
public static class AppIcon
{
    private static ImageSource? _current;

    public static ImageSource? Current
    {
        get
        {
            if (_current is not null) return _current;
            try
            {
                var frame = BitmapFrame.Create(new Uri("pack://application:,,,/app.ico", UriKind.Absolute));
                frame.Freeze();
                _current = frame;
            }
            catch (Exception ex) when (ex is IOException or UriFormatException or NotSupportedException or InvalidOperationException)
            {
                // No app.ico resource: keep the default window icon.
            }
            return _current;
        }
    }

    /// <summary>Sets the program icon on a window (no-op when there is none).</summary>
    public static void Apply(Window window)
    {
        if (Current is { } icon) window.Icon = icon;
    }
}
