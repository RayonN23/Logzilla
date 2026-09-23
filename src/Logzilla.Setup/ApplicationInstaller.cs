using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.Versioning;

namespace Logzilla.Setup;

/// <summary>
/// Installs and removes the application.
///
/// The published app is carried inside this executable as an embedded zip, so a
/// single file is all the user needs. Everything is written under the user's own
/// profile, which is what keeps the whole thing free of an administrator prompt.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ApplicationInstaller
{
    private const string PayloadResourceName = "payload";
    private const int EmptyPayloadThreshold = 64;

    private readonly SetupConsole _console;

    public ApplicationInstaller(SetupConsole console) => _console = console;

    public void Install(string requestedDirectory)
    {
        var installDirectory = requestedDirectory ?? DefaultInstallDirectory();
        _console.Heading($"Installing {AppMetadata.ProductName} {AppMetadata.Version}");
        _console.Step("to " + installDirectory);

        StopRunningInstance(installDirectory);
        ExtractPayload(installDirectory);
        CopySetupForUninstall(installDirectory);

        var executablePath = Path.Combine(installDirectory, AppMetadata.ExecutableName);
        if (!File.Exists(executablePath))
        {
            throw new SetupException(
                $"{AppMetadata.ExecutableName} is missing from the payload. " +
                "Publish the application before building the installer.");
        }

        CreateShortcuts(executablePath, installDirectory);

        _console.Step("Registering log file types");
        new RegistryRegistrar(executablePath, installDirectory).Register();
        ShellNotifier.RefreshFileAssociations();

        _console.Heading("Installed.");
        _console.Step("Start menu:  " + AppMetadata.ProductName);
        _console.Step(
            "To open .log files by double-clicking, right-click one, choose Open with, " +
            "pick Logzilla and tick Always use this app.");
    }

    public void Uninstall()
    {
        var installDirectory = DefaultInstallDirectory();
        _console.Heading($"Removing {AppMetadata.ProductName}");

        StopRunningInstance(installDirectory);

        _console.Step("Removing registry entries");
        RegistryRegistrar.Unregister();

        _console.Step("Removing shortcuts");
        DeleteIfPresent(StartMenuShortcutPath());
        DeleteIfPresent(DesktopShortcutPath());
        // Installs made before the product was renamed carry the old shortcut name.
        DeleteIfPresent(LegacyStartMenuShortcutPath());
        DeleteIfPresent(LegacyDesktopShortcutPath());

        _console.Step("Removing program files");
        if (!DeleteDirectory(installDirectory, Environment.ProcessPath))
        {
            _console.Step("The uninstaller deletes itself moments after this window closes");
        }

        ShellNotifier.RefreshFileAssociations();
        _console.Heading("Removed.");
        _console.Step("Settings were left in " + SettingsDirectory());
    }

    private static string DefaultInstallDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            AppMetadata.FolderName);

    private static string SettingsDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppMetadata.FolderName);

    private static string StartMenuShortcutPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            AppMetadata.ProductName + ".lnk");

    private static string DesktopShortcutPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            AppMetadata.ProductName + ".lnk");

    private static string LegacyStartMenuShortcutPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            AppMetadata.LegacyShortcutName + ".lnk");

    private static string LegacyDesktopShortcutPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            AppMetadata.LegacyShortcutName + ".lnk");

    private void ExtractPayload(string installDirectory)
    {
        using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadResourceName);
        if (payload is null || payload.Length < EmptyPayloadThreshold)
        {
            throw new SetupException(
                "This installer was built without an application payload. " +
                "Run build.ps1, which publishes the app before packaging.");
        }

        Directory.CreateDirectory(installDirectory);
        _console.Step("Extracting files");

        using var archive = new ZipArchive(payload, ZipArchiveMode.Read);
        archive.ExtractToDirectory(installDirectory, overwriteFiles: true);
    }

    /// <summary>
    /// Keeps a copy of the installer beside the app so Add/Remove Programs has
    /// something to invoke for the uninstall.
    /// </summary>
    private static void CopySetupForUninstall(string installDirectory)
    {
        var current = Environment.ProcessPath;
        if (string.IsNullOrEmpty(current))
        {
            return;
        }

        var destination = Path.Combine(installDirectory, AppMetadata.SetupCopyName);
        if (string.Equals(current, destination, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        File.Copy(current, destination, overwrite: true);
    }

    private void CreateShortcuts(string executablePath, string installDirectory)
    {
        _console.Step("Creating shortcuts");
        TryCreateShortcut("Start menu", StartMenuShortcutPath(), executablePath, installDirectory);
        TryCreateShortcut("Desktop", DesktopShortcutPath(), executablePath, installDirectory);
    }

    /// <summary>
    /// Shortcuts are convenient but not essential: if shell interop is unavailable
    /// on this machine, keep the app installed and tell the user what was skipped.
    /// </summary>
    private void TryCreateShortcut(string label, string shortcutPath, string executablePath, string installDirectory)
    {
        try
        {
            ShortcutWriter.Create(shortcutPath, executablePath, installDirectory, AppMetadata.Description);
        }
        catch (Exception ex)
        {
            _console.Step($"{label} shortcut skipped: {ex.Message}");
        }
    }

    /// <summary>
    /// Files cannot be replaced while they are running, so close a previous copy
    /// first rather than failing halfway through the extraction.
    /// </summary>
    private void StopRunningInstance(string installDirectory)
    {
        var executablePath = Path.Combine(installDirectory, AppMetadata.ExecutableName);
        foreach (var process in Process.GetProcessesByName(
                     Path.GetFileNameWithoutExtension(AppMetadata.ExecutableName)))
        {
            using (process)
            {
                if (!MatchesInstallation(process, executablePath))
                {
                    continue;
                }

                _console.Step("Closing the running copy");
                try
                {
                    if (!process.CloseMainWindow() || !process.WaitForExit(5000))
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(5000);
                    }
                }
                catch (InvalidOperationException)
                {
                    // The process exited between listing it and asking it to close.
                }
            }
        }
    }

    private static bool MatchesInstallation(Process process, string executablePath)
    {
        try
        {
            return string.Equals(process.MainModule?.FileName, executablePath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // A process owned by another user cannot be inspected; it is not ours.
            return false;
        }
    }

    private static void DeleteIfPresent(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Removes the installed files. The running uninstaller lives inside this very
    /// folder, and Windows will not delete a running image, so that one file is left
    /// for a short-lived cmd process to remove once this one exits. Anything else
    /// still held open is handed to the same cleanup.
    ///
    /// Returns false when something had to be left for that cleanup. A leftover file
    /// is not a failed uninstall: the previous version let the recursive delete throw
    /// "Access to the path 'Logzilla-Setup.exe' is denied" — an UnauthorizedAccessException,
    /// which the IOException handler did not catch — so a removal that had actually
    /// finished ended with an error and a failure exit code.
    /// </summary>
    private static bool DeleteDirectory(string directory, string self)
    {
        if (!Directory.Exists(directory))
        {
            return true;
        }

        var leftBehind = false;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            if (IsSamePath(file, self))
            {
                leftBehind = true;
                continue;
            }

            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                leftBehind = true;
            }
        }

        if (leftBehind)
        {
            // A recursive delete would only throw on the file that is still locked.
            PendingDelete.ScheduleSelfRemoval(directory);
            return false;
        }

        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PendingDelete.ScheduleSelfRemoval(directory);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Whether two paths name the same file. The uninstaller is launched by whatever
    /// path Add/Remove Programs recorded, which need not match the spelling the
    /// directory walk produces, and a missed match here is what leaves the running
    /// image to be deleted — and to throw — like any other file.
    /// </summary>
    private static bool IsSamePath(string left, string right)
    {
        if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
        {
            return false;
        }

        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
