namespace Logzilla.AI.Tools;

/// <summary>
/// A search the model ran, as the user's own filters would express it, so the Ask tab
/// can offer to apply it to the log. Empty strings mean "not set".
/// </summary>
public sealed record SearchRequest(string Query, bool Regex, bool MatchCase, string MinLevel,
    string From, string To, string Field, string FieldValue);

/// <summary>
/// One tool call, described for the transcript: "Searched "timeout" — 124 entries".
/// <see cref="Search"/> is set when the call can be applied as a filter.
/// </summary>
public sealed record ToolActivity(string Tool, string Summary, SearchRequest Search = null);
