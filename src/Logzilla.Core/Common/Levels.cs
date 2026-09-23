namespace Logzilla.Core.Common;

public static class Levels
{
    public const int Count = 7;

    public static readonly string[] Name = ["", "TRACE", "DEBUG", "INFO", "WARN", "ERROR", "FATAL"];

    /// <summary>Names for the severity buttons, where level 0 needs a visible label.</summary>
    public static readonly string[] Abbreviation = ["none", "TRACE", "DEBUG", "INFO", "WARN", "ERROR", "FATAL"];
}
