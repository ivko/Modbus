using Modbus.Ui.Automation;
using Modbus.Ui.Views;
using Modbus.Ui.Dialogs;
using Modbus.Ui;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;

namespace Modbus.App.Automation;

/// <summary>Out-of-process COM server: class objects and per-user registration.</summary>
public static class ComServer
{
    public const string ApplicationProgId = "ModbusMaster.Application";
    public const string DocumentProgId = "ModbusMaster.Document";
    public const string CompatApplicationProgId = "Mbpoll.Application";
    public const string CompatDocumentProgId = "Mbpoll.Document";

    public static readonly Guid ApplicationClsid = new("6B1C9E2A-4F37-4D59-9C1E-8A2D7F0B3E41");
    public static readonly Guid DocumentClsid = new("6B1C9E2A-4F37-4D59-9C1E-8A2D7F0B3E42");

    private const uint CLSCTX_LOCAL_SERVER = 4;
    private const uint REGCLS_MULTIPLEUSE = 1;

    private static uint _appCookie;
    private static uint _docCookie;
    private static IntPtr _appFactory;
    private static IntPtr _docFactory;

    [DllImport("ole32.dll")]
    private static extern int CoRegisterClassObject(in Guid clsid, IntPtr unknown, uint context, uint flags, out uint cookie);

    [DllImport("ole32.dll")]
    private static extern int CoRevokeClassObject(uint cookie);

    /// <summary>Makes this process the server for new automation objects. Call on the UI (STA) thread.</summary>
    public static void RegisterClassObjects()
    {
        if (_appFactory != IntPtr.Zero) return;
        _appFactory = NativeCom.CreateFactory(() => new AutomationApplication());
        _docFactory = NativeCom.CreateFactory(() => new AutomationDocument());
        var hr = CoRegisterClassObject(ApplicationClsid, _appFactory, CLSCTX_LOCAL_SERVER, REGCLS_MULTIPLEUSE, out _appCookie);
        if (hr >= 0) CoRegisterClassObject(DocumentClsid, _docFactory, CLSCTX_LOCAL_SERVER, REGCLS_MULTIPLEUSE, out _docCookie);
    }

    public static void RevokeClassObjects()
    {
        if (_appCookie != 0) CoRevokeClassObject(_appCookie);
        if (_docCookie != 0) CoRevokeClassObject(_docCookie);
        _appCookie = _docCookie = 0;
    }

    // ---- registration (HKCU, no administrator rights) ----

    private static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "ModbusMaster.exe");

    public static bool IsRegistered(string progId)
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{progId}\CLSID");
        return key?.GetValue(null) is string clsid &&
               (clsid.Equals(ApplicationClsid.ToString("B"), StringComparison.OrdinalIgnoreCase) ||
                clsid.Equals(DocumentClsid.ToString("B"), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True when another program (Modbus Poll) owns the Mbpoll.* ProgIDs.</summary>
    public static bool ModbusPollInstalled()
    {
        using var key = Registry.ClassesRoot.OpenSubKey($@"{CompatApplicationProgId}\CLSID");
        return key?.GetValue(null) is string clsid && !clsid.Equals(ApplicationClsid.ToString("B"), StringComparison.OrdinalIgnoreCase);
    }

    public static void Register(bool compatibilityNames)
    {
        RegisterClass(ApplicationClsid, ApplicationProgId, "Modbus Master Application");
        RegisterClass(DocumentClsid, DocumentProgId, "Modbus Master Document");
        if (compatibilityNames)
        {
            RegisterProgId(CompatApplicationProgId, ApplicationClsid, "Modbus Master Application (Modbus Poll compatible name)");
            RegisterProgId(CompatDocumentProgId, DocumentClsid, "Modbus Master Document (Modbus Poll compatible name)");
        }
        else
        {
            UnregisterCompatibilityNames();
        }
    }

    public static void Unregister()
    {
        UnregisterCompatibilityNames();
        foreach (var progId in new[] { ApplicationProgId, DocumentProgId })
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{progId}", throwOnMissingSubKey: false);
        foreach (var clsid in new[] { ApplicationClsid, DocumentClsid })
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\CLSID\{clsid:B}", throwOnMissingSubKey: false);
    }

    private static void UnregisterCompatibilityNames()
    {
        foreach (var progId in new[] { CompatApplicationProgId, CompatDocumentProgId })
            if (IsRegistered(progId))
                Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{progId}", throwOnMissingSubKey: false);
    }

    private static void RegisterClass(Guid clsid, string progId, string description)
    {
        using (var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\CLSID\{clsid:B}"))
        {
            key.SetValue(null, description);
            using (var server = key.CreateSubKey("LocalServer32")) server.SetValue(null, $"\"{ExePath}\" /automation");
            using (var pid = key.CreateSubKey("ProgID")) pid.SetValue(null, progId);
        }
        RegisterProgId(progId, clsid, description);
    }

    private static void RegisterProgId(string progId, Guid clsid, string description)
    {
        using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{progId}");
        key.SetValue(null, description);
        using var c = key.CreateSubKey("CLSID");
        c.SetValue(null, clsid.ToString("B"));
    }
}

/// <summary>Tools > Register Automation.</summary>
public static class AutomationRegistration
{
    public static void ShowDialog()
    {
        var registered = ComServer.IsRegistered(ComServer.ApplicationProgId);
        var compat = ComServer.IsRegistered(ComServer.CompatApplicationProgId);
        var modbusPoll = ComServer.ModbusPollInstalled();

        var f = new FormDialog("Automation (COM)", "automation");
        f.AddInfo(registered
            ? "Automation is registered for this Windows user. Scripts can use CreateObject(\"ModbusMaster.Application\") and CreateObject(\"ModbusMaster.Document\")."
            : "Automation is not registered. Register it so that Excel, VBScript, Python (pywin32) and other COM clients can control Modbus Master.");
        var register = f.AddCheck("Register ModbusMaster.Application and ModbusMaster.Document", true);
        var alias = f.AddCheck("Also register the names Mbpoll.Application and Mbpoll.Document", compat);
        f.AddInfo(modbusPoll
            ? "Modbus Poll is installed on this computer. Registering the Mbpoll names here makes existing scripts use Modbus Master instead of Modbus Poll for your Windows user. Leave it off to keep both programs separate; then change the names in your scripts."
            : "With the Mbpoll names, scripts written for Modbus Poll work without changes.",
            modbusPoll ? System.Windows.Media.Brushes.DarkOrange : null);
        f.AddInfo("Registration is per user (HKEY_CURRENT_USER) and needs no administrator rights. Programs running as administrator do not see per-user registrations.");
        f.AddInfo($"Server: {Environment.ProcessPath}");
        if (!f.ShowModal()) return;

        try
        {
            if (register.IsChecked == true) ComServer.Register(alias.IsChecked == true);
            else ComServer.Unregister();
            MessageBox.Show(register.IsChecked == true ? "Automation registered." : "Automation unregistered.", "Automation",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            MessageBox.Show($"Registration failed: {ex.Message}", "Automation", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
