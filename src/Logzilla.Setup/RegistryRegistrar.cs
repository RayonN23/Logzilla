using System;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Logzilla.Setup;

/// <summary>
/// Writes the per-user registry entries: the log file type, the Open with entry,
/// the Default Apps capability and the Add/Remove Programs record.
///
/// Everything lands under HKEY_CURRENT_USER, which is why the installer needs no
/// administrator rights. Windows deliberately refuses to let an installer seize
/// the default handler for an extension, so this registers the type and leaves
/// the final choice to the user.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RegistryRegistrar
{
    private const string ProgId = "Logzilla.LogFile";
    private const string ClassesKey = @"Software\Classes";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Logzilla";
    private const string CapabilitiesKey = @"Software\Logzilla\Capabilities";
    private const string RegisteredApplicationsKey = @"Software\RegisteredApplications";

    private static readonly string[] LogExtensions = [".log", ".log1", ".log2", ".trace", ".syslog", ".out", ".err"];

    private readonly string _executablePath;
    private readonly string _installDirectory;

    public RegistryRegistrar(string executablePath, string installDirectory)
    {
        _executablePath = executablePath;
        _installDirectory = installDirectory;
    }

    public void Register()
    {
        var openCommand = $"\"{_executablePath}\" \"%1\"";

        using (var progId = Registry.CurrentUser.CreateSubKey($@"{ClassesKey}\{ProgId}"))
        {
            progId.SetValue(string.Empty, "Log file");
            using (var icon = progId.CreateSubKey("DefaultIcon"))
            {
                icon.SetValue(string.Empty, _executablePath + ",0");
            }

            using var command = progId.CreateSubKey(@"shell\open\command");
            command.SetValue(string.Empty, openCommand);
        }

        using (var application = Registry.CurrentUser.CreateSubKey(
                   $@"{ClassesKey}\Applications\{AppMetadata.ExecutableName}"))
        {
            application.SetValue("FriendlyAppName", AppMetadata.ProductName);
            using var command = application.CreateSubKey(@"shell\open\command");
            command.SetValue(string.Empty, openCommand);
        }

        foreach (var extension in LogExtensions)
        {
            using var openWith = Registry.CurrentUser.CreateSubKey(
                $@"{ClassesKey}\{extension}\OpenWithProgids");
            openWith.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.Binary);
        }

        using (var capabilities = Registry.CurrentUser.CreateSubKey(CapabilitiesKey))
        {
            capabilities.SetValue("ApplicationName", AppMetadata.ProductName);
            capabilities.SetValue("ApplicationDescription", AppMetadata.Description);
            using var associations = capabilities.CreateSubKey("FileAssociations");
            foreach (var extension in LogExtensions)
            {
                associations.SetValue(extension, ProgId);
            }
        }

        using (var registered = Registry.CurrentUser.CreateSubKey(RegisteredApplicationsKey))
        {
            registered.SetValue(AppMetadata.ProductName, CapabilitiesKey);
        }

        WriteUninstallEntry();
    }

    public static void Unregister()
    {
        Registry.CurrentUser.DeleteSubKeyTree($@"{ClassesKey}\{ProgId}", throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(
            $@"{ClassesKey}\Applications\{AppMetadata.ExecutableName}", throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Logzilla", throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);

        foreach (var extension in LogExtensions)
        {
            using var openWith = Registry.CurrentUser.OpenSubKey(
                $@"{ClassesKey}\{extension}\OpenWithProgids", writable: true);
            openWith?.DeleteValue(ProgId, throwOnMissingValue: false);
        }

        using var registered = Registry.CurrentUser.OpenSubKey(RegisteredApplicationsKey, writable: true);
        registered?.DeleteValue(AppMetadata.ProductName, throwOnMissingValue: false);
    }

    /// <summary>Puts the app in Settings > Apps so it uninstalls like anything else.</summary>
    private void WriteUninstallEntry()
    {
        using var uninstall = Registry.CurrentUser.CreateSubKey(UninstallKey);
        uninstall.SetValue("DisplayName", AppMetadata.ProductName);
        uninstall.SetValue("DisplayVersion", AppMetadata.Version);
        uninstall.SetValue("Publisher", AppMetadata.Publisher);
        uninstall.SetValue("Comments", AppMetadata.Description);
        uninstall.SetValue("Contact", AppMetadata.Contact);
        uninstall.SetValue("DisplayIcon", _executablePath);
        uninstall.SetValue("InstallLocation", _installDirectory);
        uninstall.SetValue("UninstallString", $"\"{SetupCopyPath()}\" /uninstall");
        uninstall.SetValue("QuietUninstallString", $"\"{SetupCopyPath()}\" /uninstall /silent");
        uninstall.SetValue("NoModify", 1, RegistryValueKind.DWord);
        uninstall.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        uninstall.SetValue("EstimatedSize", DirectorySize.InKilobytes(_installDirectory), RegistryValueKind.DWord);
    }

    private string SetupCopyPath() =>
        System.IO.Path.Combine(_installDirectory, AppMetadata.SetupCopyName);
}
