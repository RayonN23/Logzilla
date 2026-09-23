using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace Logzilla.App.Infrastructure;


internal sealed class Settings
{
    private static void IgnoreNonCritical(Exception ex, string context)
    {
        Debug.WriteLine(context + ": " + ex.Message);
    }

    private readonly Dictionary<string, string> _v = new();

    public string Get(string k, string d)
    {
        string v;
        return _v.TryGetValue(k, out v) ? v : d;
    }
    public int GetInt(string k, int d)
    {
        int n;
        return int.TryParse(Get(k, ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : d;
    }
    public bool GetBool(string k, bool d) { return Get(k, d ? "1" : "0") == "1"; }
    public void Set(string k, string v) { _v[k] = v; }
    public void Set(string k, int v) { _v[k] = v.ToString(CultureInfo.InvariantCulture); }
    public void Set(string k, bool v) { _v[k] = v ? "1" : "0"; }

    public void Load()
    {
        try
        {
            if (!File.Exists(Paths.Settings))
            {
                return;
            }
            foreach (var line in File.ReadAllLines(Paths.Settings))
            {
                var eq = line.IndexOf('=');
                if (eq > 0)
                {
                    _v[line.Substring(0, eq)] = line.Substring(eq + 1);
                }
            }
        }
        catch (Exception ex) { IgnoreNonCritical(ex, "Load settings"); }
    }

    public void Save()
    {
        try
        {
            var sb = new StringBuilder();
            foreach (var kv in _v)
            {
                sb.AppendLine(kv.Key + "=" + kv.Value);
            }
            File.WriteAllText(Paths.Settings, sb.ToString());
        }
        catch (Exception ex) { IgnoreNonCritical(ex, "Save settings"); }
    }

    public static List<string> LoadRecent()
    {
        var l = new List<string>();
        try
        {
            if (File.Exists(Paths.Recent))

            {
                foreach (var p in File.ReadAllLines(Paths.Recent))

                {
                    if (p.Trim().Length > 0 && !l.Contains(p))
                    {
                        l.Add(p);
                    }
                }
            }
        }
        catch (Exception ex) { IgnoreNonCritical(ex, "Load recent files"); }
        return l;
    }

    public static void PushRecent(string path)
    {
        var l = LoadRecent();
        l.Remove(path);
        l.Insert(0, path);
        while (l.Count > 12)
        {
            l.RemoveAt(l.Count - 1);
        }
        try { File.WriteAllLines(Paths.Recent, l.ToArray()); }
        catch (Exception ex) { IgnoreNonCritical(ex, "Write recent files"); }
    }
}

// Registers Logzilla so Windows offers it for log files. Everything goes under
// HKEY_CURRENT_USER, so no administrator rights are needed and nothing is
// taken away from whatever currently opens these files.
