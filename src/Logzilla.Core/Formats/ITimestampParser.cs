namespace Logzilla.Core.Formats;

/// <summary>Reads a timestamp from the head of a raw line.</summary>
public interface ITimestampParser
{
    TsKind Kind { get; set; }

    /// <summary>Anchor the stamp to the JSON time key rather than scanning loose text.</summary>
    bool IsJson { get; set; }

    /// <summary>Index just past the timestamp that was last matched.</summary>
    int LastEnd { get; set; }

    /// <summary>Syslog carries no year, so one is supplied from the file's own timestamp.</summary>
    int Year { get; set; }

    /// <summary>Midnight of the first day, for logs that carry only a time of day.</summary>
    long BaseDayMs { get; set; }

    bool Parse(byte[] buffer, int offset, int length, out double milliseconds);
}
