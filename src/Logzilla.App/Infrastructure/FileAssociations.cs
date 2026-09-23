using System;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Logzilla.Core.Common;

namespace Logzilla.App.Infrastructure;


// Registers Logzilla so Windows offers it for log files. Everything goes under
// HKEY_CURRENT_USER, so no administrator rights are needed and nothing is
// taken away from whatever currently opens these files.
internal static class FileAssociations
{
    public const string ProgId = "Logzilla.LogDocument";
    public static readonly string[] Exts = { ".log", ".log1", ".log2", ".trace", ".out", ".err", ".syslog" };

    private static void IgnoreNonCritical(Exception ex, string context)
    {
        Debug.WriteLine(context + ": " + ex.Message);
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    private static void Notify()
    {
        try { SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero); }
        catch (Exception ex) { IgnoreNonCritical(ex, "Notify shell about file associations"); }
    }

    private static Microsoft.Win32.RegistryKey RequireKey(Microsoft.Win32.RegistryKey key, string context)
    {
        if (key == null)
        {
            throw new InvalidOperationException("Could not open registry key: " + context);
        }
        return key;
    }

    public static bool IsRegistered()
    {
        try
        {
            using (var k =
                Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + ProgId))

            {
                return k != null;
            }
        }
        catch { return false; }
    }

    [SuppressMessage("Reliability", "S2259", Justification = "Registry keys are guarded by RequireKey checks.")]
    [SuppressMessage("Major Code Smell", "S3776", Justification = "Registry setup is intentionally explicit.")]
    public static void Register(string exe)
    {
        var exeName = Path.GetFileName(exe);
        using (var classes =
            RequireKey(Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes"), @"Software\Classes"))
        {
            using (var p = RequireKey(classes.CreateSubKey(ProgId), ProgId))
            {
                p.SetValue("", "Log file");
                p.SetValue("FriendlyTypeName", "Log file");
                using (var di = RequireKey(p.CreateSubKey("DefaultIcon"), "DefaultIcon"))

                {
                    di.SetValue("", "\"" + exe + "\",0");
                }
                using (var sh = RequireKey(p.CreateSubKey(@"shell\open"), @"shell\open"))
                {
                    sh.SetValue("", "Open with " + AppInfo.Name);
                    sh.SetValue("FriendlyAppName", AppInfo.Name);
                    using (var c = RequireKey(sh.CreateSubKey("command"), "command"))

                    {
                        c.SetValue("", "\"" + exe + "\" \"%1\"");
                    }
                }
            }

            foreach (var ext in Exts)

            {
                using (var e = RequireKey(classes.CreateSubKey(ext + @"\OpenWithProgids"), ext + @"\OpenWithProgids"))

                {
                    e.SetValue(ProgId, Array.Empty<byte>(), Microsoft.Win32.RegistryValueKind.Binary);
                }
            }

            // Makes Logzilla appear in the "Open with" list
            using (var app = RequireKey(classes.CreateSubKey(@"Applications\" + exeName), @"Applications\" + exeName))
            {
                app.SetValue("FriendlyAppName", AppInfo.Name);
                using (var c = RequireKey(app.CreateSubKey(@"shell\open\command"), @"shell\open\command"))

                {
                    c.SetValue("", "\"" + exe + "\" \"%1\"");
                }
                using (var st = RequireKey(app.CreateSubKey("SupportedTypes"), "SupportedTypes"))

                {
                    foreach (var ext in Exts)
                    {
                        st.SetValue(ext, "");
                    }
                }
            }
        }

        // Makes Logzilla appear in Settings > Apps > Default apps
        using (var cap =
            RequireKey(Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Logzilla\Capabilities"), @"Software\Logzilla\Capabilities"))
        {
            cap.SetValue("ApplicationName", AppInfo.Name);
            cap.SetValue("ApplicationDescription", "Log analyzer for very large files");
            cap.SetValue("ApplicationIcon", "\"" + exe + "\",0");
            using (var fa = RequireKey(cap.CreateSubKey("FileAssociations"), "FileAssociations"))

            {
                foreach (var ext in Exts)
                {
                    fa.SetValue(ext, ProgId);
                }
            }
        }
        using (var ra =
            RequireKey(Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"), @"Software\RegisteredApplications"))

        {
            ra.SetValue("Logzilla", @"Software\Logzilla\Capabilities");
        }

        Notify();
    }

    [SuppressMessage("Major Code Smell", "S3776", Justification = "Unregister handles many best-effort cleanup paths.")]
    public static void Unregister(string exe)
    {
        var exeName = Path.GetFileName(exe);
        try
        {
            using (var classes =
                Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes", true))
            {
                if (classes != null)
                {
                    try { classes.DeleteSubKeyTree(ProgId, false); }
                    catch (Exception ex) { IgnoreNonCritical(ex, "Delete ProgId key"); }
                    try { classes.DeleteSubKeyTree(@"Applications\" + exeName, false); }
                    catch (Exception ex) { IgnoreNonCritical(ex, "Delete application key"); }
                    foreach (var ext in Exts)
                    {
                        try
                        {
                            using (var e = classes.OpenSubKey(ext + @"\OpenWithProgids", true))

                            {
                                if (e != null)
                                {
                                    e.DeleteValue(ProgId, false);
                                }
                            }
                        }
                        catch (Exception ex) { IgnoreNonCritical(ex, "Remove OpenWithProgids for " + ext); }
                    }
                }
            }
            using (var ra =
                Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", true))

            {
                if (ra != null)
                {
                    ra.DeleteValue("Logzilla", false);
                }
            }
            try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\Logzilla", false); }
            catch (Exception ex) { IgnoreNonCritical(ex, "Delete Logzilla software key"); }
        }
        catch (Exception ex) { IgnoreNonCritical(ex, "Unregister file associations"); }
        Notify();
    }

    public static void OpenDefaultAppsSettings()
    {
        try
        {
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            Process.Start(explorer, "ms-settings:defaultapps");
        }
        catch (Exception ex)
        {
            IgnoreNonCritical(ex, "Open modern Default Apps settings");
            try
            {
                var control = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "control.exe");
                Process.Start(control, "/name Microsoft.DefaultPrograms");
            }
            catch (Exception inner) { IgnoreNonCritical(inner, "Open legacy Default Programs"); }
        }
    }
}
