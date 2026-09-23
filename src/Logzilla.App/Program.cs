using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Logzilla.App.Infrastructure;
using Logzilla.App.Views;
using Logzilla.Core.Implementation;

namespace Logzilla.App;

internal static class Program
{
    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    private static void IgnoreNonCritical(Exception ex, string context)
    {
        Debug.WriteLine(context + ": " + ex.Message);
    }

    [STAThread]
    [SuppressMessage("Major Code Smell", "S3776", Justification = "Startup flow is intentionally linear and easy to follow.")]
    private static void Main(string[] args)
    {
        args = args ?? Array.Empty<string>();
        if (HandleAssociationCommand(args))
        {
            return;
        }

        TryEnableDpiAwareness();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try
        {
            Application.SetColorMode(SystemColorMode.Dark);
        }
        catch (Exception ex) { IgnoreNonCritical(ex, "SetColorMode"); }

        Theme.ApplySystemPreferences();
        var clock = CoreServices.CreateClock();
        var f = new MainForm(
            CoreServices.CreateIndexer(clock),
            CoreServices.CreateFilterEngine(),
            CoreServices.CreateJsonLogReader());
        var open = ExistingPaths(args);
        f.Shown += delegate
        {
            if (open.Count > 0)
            {
                f.OpenMany(open.ToArray());
            }
        };
        Application.Run(f);
    }

    private static bool HandleAssociationCommand(string[] args)
    {
        // Command line hooks used by install.cmd / uninstall.cmd.
        // A real file always wins over switch parsing, so a path is never eaten.
        foreach (var a in args)
        {
            if (System.IO.File.Exists(a))
            {
                continue;
            }
            var s = a.TrimStart('-', '/').ToLowerInvariant();
            if (s == "register")
            {
                try
                {
                    FileAssociations.Register(Application.ExecutablePath);
                }
                catch (Exception ex)
                {
                    IgnoreNonCritical(ex, "CLI register");
                }
                return true;
            }
            if (s == "unregister")
            {
                try
                {
                    FileAssociations.Unregister(Application.ExecutablePath);
                }
                catch (Exception ex)
                {
                    IgnoreNonCritical(ex, "CLI unregister");
                }
                return true;
            }
        }
        return false;
    }

    private static void TryEnableDpiAwareness()
    {
        try
        {
            SetProcessDPIAware();
        }
        catch (Exception ex)
        {
            IgnoreNonCritical(ex, "SetProcessDPIAware");
        }
    }

    private static List<string> ExistingPaths(string[] args)
    {
        return args.Where(System.IO.File.Exists).ToList();
    }
}
