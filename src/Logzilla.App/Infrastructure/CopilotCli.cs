using System;
using System.IO;

namespace Logzilla.App.Infrastructure;

/// <summary>
/// Finds the Copilot runtime to run. A build made with /p:BundleCopilot=true carries
/// its own; otherwise Logzilla uses the Copilot CLI the user installed. Nothing is
/// ever downloaded at run time.
/// </summary>
internal static class CopilotCli
{
    /// <summary>
    /// The path to pass to the SDK, or null for the bundled runtime. <paramref name="found"/>
    /// is false when there is neither, and Copilot cannot be used.
    /// </summary>
    public static string Resolve(string configured, out bool found)
    {
        found = true;
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }
        var env = Environment.GetEnvironmentVariable("COPILOT_CLI_PATH");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
        {
            return env;
        }
        if (Bundled())
        {
            return null;
        }
        // The saved PATH as well as this process's own: a Logzilla opened before the CLI
        // was installed still has the old one.
        var path = string.Join(Path.PathSeparator,
            Environment.GetEnvironmentVariable("PATH") ?? "",
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "",
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? "");
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in new[] { "copilot.exe", "copilot.cmd" })
            {
                try
                {
                    // A relative entry (".", "bin") resolves against the working folder,
                    // which is wherever the opened log lives: a copilot.exe planted beside
                    // a downloaded log must not be the one that runs.
                    var folder = dir.Trim().Trim('"');
                    if (!Path.IsPathFullyQualified(folder))
                    {
                        continue;
                    }
                    var candidate = Path.Combine(folder, name);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (ArgumentException ex)
                {
                    System.Diagnostics.Debug.WriteLine("Skip PATH entry: " + ex.Message);
                }
            }
        }
        // PATH is fixed when a program starts, so a Logzilla opened before the CLI was
        // installed cannot see it there. Look where winget and npm put it as well.
        foreach (var candidate in KnownLocations())
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        found = false;
        return null;
    }

    private static System.Collections.Generic.IEnumerable<string> KnownLocations()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return Path.Combine(local, "Microsoft", "WinGet", "Links", "copilot.exe");
        yield return Path.Combine(roaming, "npm", "copilot.cmd");
        // winget installs portable packages into a folder named after the package and its source.
        var packages = Path.Combine(local, "Microsoft", "WinGet", "Packages");
        string[] folders = [];
        try
        {
            if (Directory.Exists(packages))
            {
                folders = Directory.GetDirectories(packages, "GitHub.Copilot_*");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine("Look for the Copilot CLI: " + ex.Message);
        }
        foreach (var folder in folders)
        {
            yield return Path.Combine(folder, "copilot.exe");
        }
    }

    private static bool Bundled() =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "runtime.node")) ||
        File.Exists(Path.Combine(AppContext.BaseDirectory, "runtimes", "win-arm64", "native", "runtime.node"));
}
