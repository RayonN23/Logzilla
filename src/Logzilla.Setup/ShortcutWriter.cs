using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Logzilla.Setup;

/// <summary>
/// Creates .lnk shortcuts through the shell's own IShellLink, which is present on
/// every Windows install. The interfaces are declared with typed signatures rather
/// than late-bound through WScript.Shell so mistakes are caught at compile time.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ShortcutWriter
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
        var link = (IShellLinkW)new ShellLink();
        link.SetPath(targetPath);
        link.SetWorkingDirectory(workingDirectory);
        link.SetDescription(description);
        link.SetIconLocation(targetPath, 0);

        ((IPersistFile)link).Save(shortcutPath, fRemember: true);
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

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class ShellLink : IShellLinkW, IPersistFile
    {
        // The shell provides every member; this type exists only to be activated.
        public extern void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] char[] file, int maxPath, IntPtr findData, int flags);
        public extern void GetIDList(out IntPtr idList);
        public extern void SetIDList(IntPtr idList);
        public extern void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] char[] name, int maxName);
        public extern void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        public extern void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] char[] dir, int maxPath);
        public extern void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        public extern void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] char[] args, int maxArgs);
        public extern void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        public extern void GetHotkey(out short hotkey);
        public extern void SetHotkey(short hotkey);
        public extern void GetShowCmd(out int showCmd);
        public extern void SetShowCmd(int showCmd);
        public extern void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] char[] iconPath, int iconPathLength, out int iconIndex);
        public extern void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        public extern void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relativePath, int reserved);
        public extern void Resolve(IntPtr window, int flags);
        public extern void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);

        public extern void GetClassID(out Guid classId);
        [PreserveSig] public extern int IsDirty();
        public extern void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);
        public extern void Save([MarshalAs(UnmanagedType.LPWStr)] string fileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        public extern void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);
        public extern void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
    }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] char[] file, int maxPath, IntPtr findData, int flags);

        void GetIDList(out IntPtr idList);

        void SetIDList(IntPtr idList);

        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] char[] name, int maxName);

        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);

        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] char[] dir, int maxPath);

        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);

        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] char[] args, int maxArgs);

        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);

        void GetHotkey(out short hotkey);

        void SetHotkey(short hotkey);

        void GetShowCmd(out int showCmd);

        void SetShowCmd(int showCmd);

        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] char[] iconPath, int iconPathLength, out int iconIndex);

        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);

        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relativePath, int reserved);

        void Resolve(IntPtr window, int flags);

        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport]
    [Guid("0000010B-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid classId);

        [PreserveSig]
        int IsDirty();

        void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);

        void Save([MarshalAs(UnmanagedType.LPWStr)] string fileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);

        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);

        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
    }
}
