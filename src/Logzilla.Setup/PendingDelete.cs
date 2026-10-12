using System;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace Logzilla.Setup;

/// <summary>
/// An uninstaller cannot delete itself while it is running. This hands the last
/// step to a short-lived cmd process that waits for this one to exit first.
/// </summary>
[SupportedOSPlatform("windows")]
public static class PendingDelete
{
    private static bool Scheduled;

    public static void ScheduleSelfRemoval(string directory)
    {
        if (Scheduled)
        {
            return;
        }

        Scheduled = true;

        // ping supplies the delay: it is present on every Windows install, whereas
        // timeout.exe fails when there is no console to read from.
        var command = $"/c ping 127.0.0.1 -n 4 > nul & rmdir /s /q \"{directory}\"";
        // By full path: a bare "cmd.exe" is looked for in the application's own folder
        // first, which is the very folder being removed.
        var cmd = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        var startInfo = new ProcessStartInfo(cmd, command)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        try
        {
            Process.Start(startInfo)?.Dispose();
        }
        catch (Exception)
        {
            // Best effort: leaving the folder behind is better than failing the uninstall.
        }
    }
}
