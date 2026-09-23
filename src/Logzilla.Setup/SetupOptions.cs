using System;
using System.Collections.Generic;

namespace Logzilla.Setup;

/// <summary>Command line switches accepted by the installer.</summary>
public sealed class SetupOptions
{
    private const string SilentSwitch = "/silent";
    private const string UninstallSwitch = "/uninstall";
    private const string DirectoryPrefix = "/dir=";

    public bool Silent { get; private set; }

    public bool Uninstall { get; private set; }

    /// <summary>Null means "use the default location under the user's profile".</summary>
    public string TargetDirectory { get; private set; }

    public static SetupOptions Parse(IEnumerable<string> args)
    {
        var options = new SetupOptions();
        if (args is null)
        {
            return options;
        }

        foreach (var arg in args)
        {
            if (arg.Equals(SilentSwitch, StringComparison.OrdinalIgnoreCase))
            {
                options.Silent = true;
            }
            else if (arg.Equals(UninstallSwitch, StringComparison.OrdinalIgnoreCase))
            {
                options.Uninstall = true;
            }
            else if (arg.StartsWith(DirectoryPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var path = arg[DirectoryPrefix.Length..].Trim('"');
                options.TargetDirectory = path.Length == 0 ? null : path;
            }
        }

        return options;
    }
}
