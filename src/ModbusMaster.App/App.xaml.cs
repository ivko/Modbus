using System.IO;
using ModbusMaster.Ui.Automation;
using ModbusMaster.Ui.Views;
using ModbusMaster.Ui.Dialogs;
using ModbusMaster.Ui;
using System.Windows;
using System.Windows.Threading;
using ModbusMaster.App.Automation;

namespace ModbusMaster.App;

/// <summary>
/// Command line:
///   ModbusMaster.exe [workspace.mmw | window.mmp]
///   ModbusMaster.exe /automation      started by COM (hidden until a script calls ShowWindow)
///   ModbusMaster.exe /regserver [/compat]   register automation for this user (/compat adds Mbpoll.* names)
///   ModbusMaster.exe /unregserver
/// </summary>
public partial class App : Application
{
    private bool _automationStart;

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
        var args = e.Args.Select(a => a.Trim()).ToArray();
        bool Has(string name) => args.Any(a => a.Equals("/" + name, StringComparison.OrdinalIgnoreCase) || a.Equals("-" + name, StringComparison.OrdinalIgnoreCase));

        if (Has("regserver") || Has("unregserver"))
        {
            try
            {
                if (Has("regserver")) ComServer.Register(Has("compat"));
                else ComServer.Unregister();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                Shutdown(1);
                return;
            }
            Shutdown(0);
            return;
        }

        DispatcherUnhandledException += OnUnhandled;
        _automationStart = Has("automation") || Has("embedding");

        var main = new MainWindow();
        MainWindow = main;
        main.Closed += (_, _) => Shutdown();

        // Scripts attach to this running instance; when COM starts us, they get a hidden instance.
        try
        {
            ComServer.RegisterClassObjects();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // No COM on this system: the program still works without automation.
        }

        if (_automationStart)
        {
            NativeCom.AllReleased += () => Dispatcher.BeginInvoke(() =>
            {
                // Exit when scripts released everything and the user never took over the window.
                if (!main.IsVisible && NativeCom.LiveObjects == 0) Shutdown();
            });
            main.Closing += (_, ce) =>
            {
                if (NativeCom.LiveObjects > 0)
                {
                    // Scripts still hold objects: keep serving them in the background.
                    ce.Cancel = true;
                    main.Hide();
                }
            };
            // If no script connects at all (started by mistake), do not stay hidden forever.
            var idle = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            idle.Tick += (_, _) =>
            {
                idle.Stop();
                if (!main.IsVisible && NativeCom.LiveObjects == 0) Shutdown();
            };
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
        ComServer.RevokeClassObjects();
        AppState.Current.Dispose();
        base.OnExit(e);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "Modbus Master", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
