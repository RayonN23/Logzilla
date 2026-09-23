using System;

namespace Logzilla.Setup;

/// <summary>A failure the user can act on, reported without a stack trace.</summary>
public sealed class SetupException : Exception
{
    public SetupException(string message) : base(message) { }

    public SetupException(string message, Exception inner) : base(message, inner) { }

    public SetupException() { }
}
