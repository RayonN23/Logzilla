using System;
using System.Collections.Generic;
using System.Text;
using Logzilla.Core.Formats;

namespace Logzilla.Core.Logs;

/// <summary>One physical log file inside a document. A merged document holds several.</summary>
public sealed class LogSource
{
    public string Path { get; set; }
    public string Name { get; set; }

    /// <summary>Short display label, shortened further when merged sources share a prefix.</summary>
    public string Label { get; set; }

    public long Size { get; set; }
    public Encoding Encoding { get; set; } = Encoding.UTF8;

    /// <summary>Bytes per character: 1 for UTF-8 and ANSI, 2 for UTF-16.</summary>
    public int Stride { get; set; } = 1;

    public long DataStart { get; set; }
    public TsKind Kind { get; set; } = TsKind.None;

    /// <summary>True when the file holds one JSON object per line.</summary>
    public bool IsJson { get; set; }

    /// <summary>
    /// True when the stamps carry their own UTC offset (<c>Z</c>, <c>+02:00</c>, CLF's
    /// <c>+0000</c>). The document's times for this file are then real instants, and
    /// <see cref="Offsets"/> says what each was written as.
    /// </summary>
    public bool Zoned { get; set; }

    /// <summary>The offsets the stamps were written with, in order. Filled while indexing.</summary>
    public List<ZoneOffset> Offsets { get; } = new List<ZoneOffset>();

    /// <summary>
    /// For stamps with no zone of their own: the zone the user says they were written
    /// in. Null means nobody knows, and the document holds the times as written.
    /// </summary>
    public TimeZoneInfo Declared { get; set; }

    /// <summary>Assigned by the indexer once the format has been detected.</summary>
    public ITimestampParser Parser { get; set; }
    public long IndexedTo { get; set; }
    public bool LastUnterminated { get; set; }
    public int TintIndex { get; set; }
    public int Lines { get; set; }
}
