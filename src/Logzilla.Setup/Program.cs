using System;
using System.Runtime.Versioning;

namespace Logzilla.Setup;

/// <summary>
/// Entry point for the installer.
///
///   Logzilla-Setup.exe              install, reporting progress
///   Logzilla-Setup.exe /silent      install without any output
///   Logzilla-Setup.exe /uninstall   remove the installation
///   Logzilla-Setup.exe /dir=PATH    install somewhere other than the default
/// </summary>
[SupportedOSPlatform("windows")]
internal static class Program
{
    private const int Success = 0;
    private const int Failure = 1;

    private static int Main(string[] args)
    {
        var options = SetupOptions.Parse(args);
        var console = new SetupConsole(options.Silent);

        if (!OperatingSystem.IsWindows())
        {
            console.Error("Logzilla only installs on Windows.");
            return Failure;
        }

        try
        {
            var installer = new ApplicationInstaller(console);
            if (options.Uninstall)
            {
                installer.Uninstall();
            }
            else
            {
                installer.Install(options.TargetDirectory);
            }

            return Success;
        }
        catch (SetupException ex)
        {
            console.Error(ex.Message);
            return Failure;
        }
        catch (Exception ex)
        {
            console.Error("Unexpected failure: " + ex.Message);
            return Failure;
        }
        finally
        {
            console.WaitForAcknowledgement();
        }
    }
}
