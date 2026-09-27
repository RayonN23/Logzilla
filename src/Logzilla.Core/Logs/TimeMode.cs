namespace Logzilla.Core.Logs;

/// <summary>The clock the times on screen are read in.</summary>
public enum TimeMode
{
    /// <summary>Exactly as the file wrote them.</summary>
    AsWritten,

    /// <summary>This machine's time zone.</summary>
    Local,

    /// <summary>Coordinated Universal Time.</summary>
    Utc,
}
