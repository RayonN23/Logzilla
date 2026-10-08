using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;
using System.Text;

namespace Logzilla.Setup;

/// <summary>
/// Creates .lnk shortcuts through the shell's own IShellLink, which is present on
/// every Windows install. The interfaces are declared with typed signatures rather
/// than late-bound through WScript.Shell so mistakes are caught at compile time.
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class ShortcutWriter
{
    private const int ErrorBadFormat = unchecked((int)0x8007000B);
    private const string PowerShellExecutableName = "powershell.exe";

    public static void Create(string shortcutPath, string targetPath, string workingDirectory, string description)
    {
        if (RequiresExternalShellHost(RuntimeInformation.ProcessArchitecture, RuntimeInformation.OSArchitecture))
        {
            CreateWithPowerShell(shortcutPath, targetPath, workingDirectory, description);
            return;
        }

        try
        {
            CreateWithShellLink(shortcutPath, targetPath, workingDirectory, description);
        }
        catch (BadImageFormatException)
        {
            CreateWithPowerShell(shortcutPath, targetPath, workingDirectory, description);
        }
        catch (COMException ex) when (ex.HResult == ErrorBadFormat)
        {
            CreateWithPowerShell(shortcutPath, targetPath, workingDirectory, description);
        }
    }

    internal static bool RequiresExternalShellHost(Architecture processArchitecture, Architecture osArchitecture) =>
        processArchitecture != osArchitecture;

    internal static string EscapePowerShellSingleQuotedString(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);

    internal static void CreateWithPowerShell(string shortcutPath, string targetPath, string workingDirectory, string description)
    {
        var script = string.Join(
            Environment.NewLine,
            "$ErrorActionPreference = 'Stop'",
            "$shell = New-Object -ComObject WScript.Shell",
            $"$shortcut = $shell.CreateShortcut('{EscapePowerShellSingleQuotedString(shortcutPath)}')",
            $"$shortcut.TargetPath = '{EscapePowerShellSingleQuotedString(targetPath)}'",
            $"$shortcut.WorkingDirectory = '{EscapePowerShellSingleQuotedString(workingDirectory)}'",
            $"$shortcut.Description = '{EscapePowerShellSingleQuotedString(description)}'",
            $"$shortcut.IconLocation = '{EscapePowerShellSingleQuotedString(targetPath)},0'",
            "$shortcut.Save()"
        );

        var startInfo = new ProcessStartInfo(
            FindPowerShellExecutable(),
            "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " +
            Convert.ToBase64String(Encoding.Unicode.GetBytes(script)))
        {
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            throw new SetupException("Windows could not start PowerShell to create a shortcut.");
        }

        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0 || !File.Exists(shortcutPath))
        {
            var detail = string.IsNullOrWhiteSpace(standardError)
                ? standardOutput.Trim()
                : standardError.Trim();

            throw new SetupException(
                string.IsNullOrWhiteSpace(detail)
                    ? $"Windows could not create shortcut '{Path.GetFileName(shortcutPath)}'."
                    : $"Windows could not create shortcut '{Path.GetFileName(shortcutPath)}': {detail}");
        }
    }

    private static void CreateWithShellLink(string shortcutPath, string targetPath, string workingDirectory, string description)
    {
        // Source-generated COM rather than [ComImport]: built-in COM interop cannot be
        // trimmed, and a trimmed installer is a third the size, which is what Windows
        // has to upload and check before it lets a new, unsigned download start.
        _ = CoInitializeEx(IntPtr.Zero, CoinitMultithreaded);
        var clsid = ShellLinkClassId;
        var iid = typeof(IShellLinkW).GUID;
        Marshal.ThrowExceptionForHR(CoCreateInstance(in clsid, IntPtr.Zero, ClsctxInprocServer, in iid, out var unknown));
        try
        {
            var shell = new StrategyBasedComWrappers().GetOrCreateObjectForComInstance(unknown, CreateObjectFlags.UniqueInstance);
            var link = (IShellLinkW)shell;
            link.SetPath(targetPath);
            link.SetWorkingDirectory(workingDirectory);
            link.SetDescription(description);
            link.SetIconLocation(targetPath, 0);
            ((IPersistFile)shell).Save(shortcutPath, true);
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    private static string FindPowerShellExecutable()
    {
        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var sysnative = Path.Combine(windowsDirectory, "Sysnative", "WindowsPowerShell", "v1.0", PowerShellExecutableName);
        var system32 = Path.Combine(windowsDirectory, "System32", "WindowsPowerShell", "v1.0", PowerShellExecutableName);

        if (File.Exists(sysnative))
        {
            return sysnative;
        }

        if (File.Exists(system32))
        {
            return system32;
        }

        return PowerShellExecutableName;
    }

    private const uint CoinitMultithreaded = 0;
    private const uint ClsctxInprocServer = 1;
    private static readonly Guid ShellLinkClassId = new("00021401-0000-0000-C000-000000000046");

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(IntPtr reserved, uint coInit);

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid classId, IntPtr outer, uint context, in Guid interfaceId, out IntPtr instance);

    // Every slot is declared, in the shell's order, so the ones used land at the right
    // vtable offsets; the getters are never called and take raw buffers.
    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    internal partial interface IShellLinkW
    {
        void GetPath(IntPtr file, int maxPath, IntPtr findData, int flags);

        void GetIdList(out IntPtr idList);

        void SetIdList(IntPtr idList);

        void GetDescription(IntPtr name, int maxName);

        void SetDescription(string name);

        void GetWorkingDirectory(IntPtr dir, int maxPath);

        void SetWorkingDirectory(string dir);

        void GetArguments(IntPtr args, int maxArgs);

        void SetArguments(string args);

        void GetHotkey(out short hotkey);

        void SetHotkey(short hotkey);

        void GetShowCmd(out int showCmd);

        void SetShowCmd(int showCmd);

        void GetIconLocation(IntPtr iconPath, int iconPathLength, out int iconIndex);

        void SetIconLocation(string iconPath, int iconIndex);

        void SetRelativePath(string relativePath, int reserved);

        void Resolve(IntPtr window, int flags);

        void SetPath(string file);
    }

    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
    [Guid("0000010B-0000-0000-C000-000000000046")]
    internal partial interface IPersistFile
    {
        void GetClassId(out Guid classId);

        [PreserveSig]
        int IsDirty();

        void Load(string fileName, uint mode);

        void Save(string fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);

        void SaveCompleted(string fileName);

        void GetCurFile(out IntPtr fileName);
    }
}
