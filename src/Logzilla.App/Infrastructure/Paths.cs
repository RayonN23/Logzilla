using System;
using System.Diagnostics;
using System.IO;

namespace Logzilla.App.Infrastructure;


internal static class Paths
{
    /// <summary>Points settings, recent files and the AI record somewhere other than
    /// %AppData%\Logzilla. The end-to-end tests set it so every run starts clean and
    /// never touches the real user's settings; a portable copy can use it too.</summary>
    public const string DataDirVariable = "LOGZILLA_DATA_DIR";

    public static string Dir
    {
        get
        {
            var over = Environment.GetEnvironmentVariable(DataDirVariable);
            var d = !string.IsNullOrWhiteSpace(over)
                ? over
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Logzilla");
            try { if (!Directory.Exists(d))
{
    Directory.CreateDirectory(d);
} }
            catch (Exception ex) { Debug.WriteLine("Create settings directory: " + ex.Message); }
            return d;
        }
    }
    public static string Settings { get { return Path.Combine(Dir, "settings.ini"); } }
    public static string Recent { get { return Path.Combine(Dir, "recent.txt"); } }
}
