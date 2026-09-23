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

    /// <summary>Assigned by the indexer once the format has been detected.</summary>
    public ITimestampParser Parser { get; set; }
    public long IndexedTo { get; set; }
    public bool LastUnterminated { get; set; }
    public int TintIndex { get; set; }
    public int Lines { get; set; }
}
