namespace Logzilla.Core.Formats;

/// <summary>Reads a timestamp from the head of a raw line.</summary>
public interface ITimestampParser
{
    TsKind Kind { get; set; }

    /// <summary>Anchor the stamp to the JSON time key rather than scanning loose text.</summary>
    bool IsJson { get; set; }

    /// <summary>Index just past the timestamp that was last matched.</summary>
    int LastEnd { get; set; }

    /// <summary>
    /// The UTC offset in minutes the last matched stamp carried (<c>Z</c> is 0), or
    /// null when it carried none. The milliseconds returned are always the wall clock
    /// as written; applying the offset is the caller's decision.
    /// </summary>
    int? LastOffset { get; }

    /// <summary>Syslog carries no year, so one is supplied from the file's own timestamp.</summary>
    int Year { get; set; }

    /// <summary>Midnight of the first day, for logs that carry only a time of day.</summary>
    long BaseDayMs { get; set; }

    bool Parse(byte[] buffer, int offset, int length, out double milliseconds);
}
