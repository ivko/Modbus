using System.Diagnostics;
using System.IO;
using System.Windows;

namespace Modbus.Ui;

/// <summary>Opens the user manual (manual\index.html next to the program) at a topic anchor.</summary>
public static class Help
{
    /// <summary>The manual page of this program inside the "manual" folder.</summary>
    public static string ManualFile { get; set; } = "index.html";

    public static string ManualPath => Path.Combine(AppContext.BaseDirectory, "manual", ManualFile);

    public static void Open(string topic = "")
    {
        if (!File.Exists(ManualPath))
        {
            MessageBox.Show($"The manual was not found:\n{ManualPath}", "Help", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var target = new Uri(ManualPath).AbsoluteUri + (topic.Length > 0 ? "#" + topic : "");
        try
        {
            // ShellExecute drops "#fragment" from file URLs, so open a tiny redirect page instead.
            var redirect = Path.Combine(Path.GetTempPath(), "Modbus-help.html");
            File.WriteAllText(redirect,
                $"<!doctype html><meta charset=\"utf-8\"><meta http-equiv=\"refresh\" content=\"0;url={target}\"><title>Modbus Master help</title>");
            Process.Start(new ProcessStartInfo(redirect) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            MessageBox.Show($"Could not open the manual: {ex.Message}", "Help", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
