using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;

namespace ModbusMaster.Ui.Automation;

/// <summary>
/// Out-of-process COM server with an Application and a Document class, registered per user (HKCU).
/// Optional compatible ProgIDs let scripts written for another program use this one.
/// </summary>
public sealed class AutomationServer(
    string productName,
    Guid applicationClsid, string applicationProgId,
    Guid documentClsid, string documentProgId,
    string compatApplicationProgId, string compatDocumentProgId,
    Func<DispatchTarget> createApplication, Func<DispatchTarget> createDocument)
{
    private const uint CLSCTX_LOCAL_SERVER = 4;
    private const uint REGCLS_MULTIPLEUSE = 1;

    private uint _appCookie, _docCookie;
    private IntPtr _appFactory, _docFactory;

    [DllImport("ole32.dll")]
    private static extern int CoRegisterClassObject(in Guid clsid, IntPtr unknown, uint context, uint flags, out uint cookie);

    [DllImport("ole32.dll")]
    private static extern int CoRevokeClassObject(uint cookie);

    public string ApplicationProgId => applicationProgId;
    public string DocumentProgId => documentProgId;
    public string CompatApplicationProgId => compatApplicationProgId;

    private static string ExePath => Environment.ProcessPath ?? AppContext.BaseDirectory;

    public void RegisterClassObjects()
    {
        if (_appFactory != IntPtr.Zero) return;
        _appFactory = NativeCom.CreateFactory(createApplication);
        _docFactory = NativeCom.CreateFactory(createDocument);
        if (CoRegisterClassObject(applicationClsid, _appFactory, CLSCTX_LOCAL_SERVER, REGCLS_MULTIPLEUSE, out _appCookie) >= 0)
            CoRegisterClassObject(documentClsid, _docFactory, CLSCTX_LOCAL_SERVER, REGCLS_MULTIPLEUSE, out _docCookie);
    }

    public void RevokeClassObjects()
    {
        if (_appCookie != 0) CoRevokeClassObject(_appCookie);
        if (_docCookie != 0) CoRevokeClassObject(_docCookie);
        _appCookie = _docCookie = 0;
    }

    public bool IsRegistered(string progId)
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{progId}\CLSID");
        return key?.GetValue(null) is string clsid &&
               (clsid.Equals(applicationClsid.ToString("B"), StringComparison.OrdinalIgnoreCase) ||
                clsid.Equals(documentClsid.ToString("B"), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True when another program owns the compatible ProgIDs.</summary>
    public bool CompatNamesOwnedByOtherProgram()
    {
        using var key = Registry.ClassesRoot.OpenSubKey($@"{compatApplicationProgId}\CLSID");
        return key?.GetValue(null) is string clsid && !clsid.Equals(applicationClsid.ToString("B"), StringComparison.OrdinalIgnoreCase);
    }

    public void Register(bool compatibilityNames)
    {
        RegisterClass(applicationClsid, applicationProgId, $"{productName} Application");
        RegisterClass(documentClsid, documentProgId, $"{productName} Document");
        if (compatibilityNames)
        {
            RegisterProgId(compatApplicationProgId, applicationClsid, $"{productName} Application (compatible name)");
            RegisterProgId(compatDocumentProgId, documentClsid, $"{productName} Document (compatible name)");
        }
        else
        {
            UnregisterCompatibilityNames();
        }
    }

    public void Unregister()
    {
        UnregisterCompatibilityNames();
        foreach (var progId in new[] { applicationProgId, documentProgId })
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{progId}", throwOnMissingSubKey: false);
        foreach (var clsid in new[] { applicationClsid, documentClsid })
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\CLSID\{clsid:B}", throwOnMissingSubKey: false);
    }

    private void UnregisterCompatibilityNames()
    {
        foreach (var progId in new[] { compatApplicationProgId, compatDocumentProgId })
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

    /// <summary>Tools > Register Automation dialog.</summary>
    public void ShowRegistrationDialog(string otherProductName)
    {
        var registered = IsRegistered(applicationProgId);
        var compat = IsRegistered(compatApplicationProgId);
        var other = CompatNamesOwnedByOtherProgram();

        var f = new FormDialog("Automation (COM)", "automation");
        f.AddInfo(registered
            ? $"Automation is registered for this Windows user. Scripts can use CreateObject(\"{applicationProgId}\") and CreateObject(\"{documentProgId}\")."
            : $"Automation is not registered. Register it so that Excel, VBScript, Python (pywin32) and other COM clients can control {productName}.");
        var register = f.AddCheck($"Register {applicationProgId} and {documentProgId}", true);
        var alias = f.AddCheck($"Also register the names {compatApplicationProgId} and {compatDocumentProgId}", compat);
        f.AddInfo(other
            ? $"{otherProductName} is installed on this computer. Registering these names makes existing scripts use {productName} instead of {otherProductName} for your Windows user. Leave it off to keep both programs separate."
            : $"With these names, scripts written for {otherProductName} work without changes.",
            other ? System.Windows.Media.Brushes.DarkOrange : null);
        f.AddInfo("Registration is per user (HKEY_CURRENT_USER) and needs no administrator rights. Programs running as administrator do not see per-user registrations.");
        f.AddInfo($"Server: {ExePath}");
        if (!f.ShowModal()) return;

        try
        {
            if (register.IsChecked == true) Register(alias.IsChecked == true);
            else Unregister();
            MessageBox.Show(register.IsChecked == true ? "Automation registered." : "Automation unregistered.", "Automation",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            MessageBox.Show($"Registration failed: {ex.Message}", "Automation", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
