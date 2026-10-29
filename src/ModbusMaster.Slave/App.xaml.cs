using System.IO;
using System.Windows;
using System.Windows.Threading;
using ModbusMaster.Slave.Automation;
using ModbusMaster.Ui;
using ModbusMaster.Ui.Automation;

namespace ModbusMaster.Slave;

/// <summary>
/// Command line:
///   ModbusSlave.exe [workspace.mms]
///   ModbusSlave.exe /automation          started by COM (hidden until a script calls ShowWindow)
///   ModbusSlave.exe /regserver [/compat] register automation for this user (/compat adds Mbslave.* names)
///   ModbusSlave.exe /unregserver
/// </summary>
public partial class App : Application
{
    public static readonly AutomationServer Automation = new(
        "Modbus Slave",
        new Guid("6B1C9E2A-4F37-4D59-9C1E-8A2D7F0B3E51"), "ModbusSlave.Application",
        new Guid("6B1C9E2A-4F37-4D59-9C1E-8A2D7F0B3E52"), "ModbusSlave.Document",
        "Mbslave.Application", "Mbslave.Document",
        () => new SlaveAutomationApplication(), () => new SlaveAutomationDocument());

    public static MainWindow Shell => (MainWindow)Current.MainWindow!;

    public static void ShowMainWindow()
    {
        var w = Shell;
        if (!w.IsVisible) w.Show();
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Help.ManualFile = "slave.html";
        var args = e.Args.Select(a => a.Trim()).ToArray();
        bool Has(string name) => args.Any(a => a.Equals("/" + name, StringComparison.OrdinalIgnoreCase) || a.Equals("-" + name, StringComparison.OrdinalIgnoreCase));

        if (Has("regserver") || Has("unregserver"))
        {
            try
            {
                if (Has("regserver")) Automation.Register(Has("compat"));
                else Automation.Unregister();
            }
            catch (Exception)
            {
                Shutdown(1);
                return;
            }
            Shutdown(0);
            return;
        }

        DispatcherUnhandledException += (_, ex) =>
        {
            MessageBox.Show(ex.Exception.Message, "Modbus Slave", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };
        var automationStart = Has("automation") || Has("embedding");
        var main = new MainWindow();
        MainWindow = main;
        main.Closed += (_, _) => Shutdown();

        try
        {
            Automation.RegisterClassObjects();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // No COM: the program still works without automation.
        }

        if (automationStart)
        {
            NativeCom.AllReleased += () => Dispatcher.BeginInvoke(() =>
            {
                if (!main.IsVisible && NativeCom.LiveObjects == 0) Shutdown();
            });
            main.Closing += (_, ce) =>
            {
                if (NativeCom.LiveObjects > 0) { ce.Cancel = true; main.Hide(); }
            };
            var idle = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            idle.Tick += (_, _) => { idle.Stop(); if (!main.IsVisible && NativeCom.LiveObjects == 0) Shutdown(); };
            idle.Start();
            return;
        }

        main.Show();
        var file = args.FirstOrDefault(a => !a.StartsWith('/') && !a.StartsWith('-') && File.Exists(a));
        if (file is not null) main.OpenPath(file);
        else main.NewWindow();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Automation.RevokeClassObjects();
        SlaveState.Current.Dispose();
        base.OnExit(e);
    }
}
