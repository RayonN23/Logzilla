using System;

namespace Logzilla.Setup;

/// <summary>Progress reporting, muted entirely when running silently.</summary>
public sealed class SetupConsole
{
    private readonly bool _silent;

    public SetupConsole(bool silent) => _silent = silent;

    public void Step(string message)
    {
        if (_silent)
        {
            return;
        }

        Console.WriteLine("  " + message);
    }

    public void Heading(string message)
    {
        if (_silent)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine(message);
    }

    public void Error(string message)
    {
        if (_silent)
        {
            return;
        }

        Console.Error.WriteLine();
        Console.Error.WriteLine("ERROR: " + message);
    }

    /// <summary>
    /// Keeps the window open when the installer was double-clicked, and returns
    /// immediately when it was launched from a script or a console.
    /// </summary>
    public void WaitForAcknowledgement()
    {
        if (_silent || Console.IsOutputRedirected || !Environment.UserInteractive)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine("Press any key to close.");
        try
        {
            Console.ReadKey(intercept: true);
        }
        catch (InvalidOperationException)
        {
            // No console input available; nothing to wait for.
        }
    }
}
