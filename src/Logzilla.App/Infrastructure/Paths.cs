using System;
using System.Diagnostics;
using System.IO;

namespace Logzilla.App.Infrastructure;


internal static class Paths
{
    public static string Dir
    {
        get
        {
            var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Logzilla");
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
