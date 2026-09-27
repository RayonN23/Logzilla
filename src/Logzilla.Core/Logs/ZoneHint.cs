namespace Logzilla.Core.Logs;

/// <summary>What a file's last-modified time suggests about stamps that carry no zone.</summary>
public enum ZoneHint
{
    /// <summary>Nothing to go on: the last entry is nowhere near the save time.</summary>
    None,

    /// <summary>The last entry matches the save time in UTC.</summary>
    Utc,

    /// <summary>The last entry matches the save time on this machine's clock.</summary>
    Local,
}
