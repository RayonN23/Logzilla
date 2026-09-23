using System.Collections.Generic;

namespace Logzilla.Core.Formats;

/// <summary>Turns a JSON log record into something a person can read.</summary>
public interface IJsonLogReader
{
    IReadOnlyList<string> MessageKeys { get; }

    IReadOnlyList<KeyValuePair<string, string>> Parse(string record);

    string FirstValue(IReadOnlyList<KeyValuePair<string, string>> properties, IReadOnlyList<string> keys);

    /// <summary>One-line summary for the grid, with the message template filled in.</summary>
    string RenderLine(string record);

    /// <summary>
    /// The same entry with nothing clipped and the exception's line breaks kept —
    /// what the expanded row and the clipboard need. <see cref="RenderLine"/> exists
    /// to fit one grid row, which is not a reason to hand out a cut-off stack trace.
    /// </summary>
    string RenderFull(string record);

    /// <summary>Message, properties and exception laid out for the detail panel.</summary>
    string Describe(string record);

    /// <summary>True for keys that are plumbing rather than content.</summary>
    bool IsMetadataKey(string key);
}
