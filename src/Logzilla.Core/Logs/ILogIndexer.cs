using System;
using System.Collections.Generic;

namespace Logzilla.Core.Logs;

/// <summary>Builds and extends the per-line index of a log document.</summary>
public interface ILogIndexer
{
    /// <summary>Inspects a file and returns a source with its encoding and formats detected.</summary>
    LogSource Prepare(string path);

    /// <summary>Indexes from where the source left off to the end of the file.</summary>
    void Run(LogDocument document, int sourceIndex, Action<long, long> progress, Func<bool> cancelled);

    /// <summary>Interleaves several documents by time into one.</summary>
    LogDocument Merge(IReadOnlyList<LogDocument> documents);

    /// <summary>Re-sorts a merged document after one of its files' times have moved.</summary>
    void Reorder(LogDocument document);

    ILineReader OpenReader(LogDocument document, int bufferSize);
}
