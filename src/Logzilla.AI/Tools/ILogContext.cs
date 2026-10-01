using System.Collections.Generic;
using Logzilla.Core.Logs;
using Logzilla.Core.Query;

namespace Logzilla.AI.Tools;

/// <summary>
/// What the host lets the assistant see. The tools read the log through this and
/// nothing else: no file paths, no disk, no network. It is read from a background
/// thread while a model is answering, so members must not touch UI controls.
/// </summary>
public interface ILogContext
{
    /// <summary>The document in the active tab, or null when nothing is open.</summary>
    LogDocument Document { get; }

    /// <summary>What the user's own filters currently show.</summary>
    FilterResult View { get; }

    /// <summary>A plain description of the user's current filters, or empty for none.</summary>
    string FilterSummary { get; }

    /// <summary>True limits every tool to the entries the user's filters show.</summary>
    bool ScopeToView { get; }

    IFilterEngine Engine { get; }

    /// <summary>A fresh reader of the document's lines. The caller disposes it.</summary>
    ILineReader OpenReader();

    /// <summary>A stored time as the user reads it, in the clock the app is showing.</summary>
    string FormatTime(LogSource source, double storedMs);

    /// <summary>A time the model typed, read in the user's clock, as stored. False if unreadable.</summary>
    bool TryParseTime(string text, out double storedMs);

    /// <summary>The clock times are given in, e.g. "local time (UTC-04:00)".</summary>
    string ClockName { get; }

    /// <summary>The fields the log carries, most common first, sampled.</summary>
    IReadOnlyList<(string Key, int Entries)> Fields();

    /// <summary>The values one field takes, most common first, sampled.</summary>
    IReadOnlyList<(string Value, int Count)> FieldValues(string key);
}
