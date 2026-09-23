using System;
using System.Collections.Generic;
using System.Text;

namespace Logzilla.App.Infrastructure;

/// <summary>
/// Turns a set of log entries into the text that goes on the clipboard.
///
/// Copying one entry and copying forty are not the same job. One entry is what
/// someone is about to paste into a chat message or a ticket, so it goes over
/// bare — no trailing newline, no decoration. Several are a transcript, so they
/// go one per line, and in a merged document each carries its source label,
/// because otherwise there is nothing to say which file a line came from.
/// </summary>
internal static class EntryClip
{
    /// <param name="texts">The entries, already rendered as they read on screen.</param>
    /// <param name="labels">Source label per entry, or null for a single-file
    /// document. Ignored when there is only one entry — it was selected on purpose
    /// and its source is not in doubt.</param>
    public static string Compose(IReadOnlyList<string> texts, IReadOnlyList<string> labels)
    {
        if (texts == null || texts.Count == 0)
        {
            return "";
        }
        if (texts.Count == 1)
        {
            return Normalize(texts[0]);
        }
        var sb = new StringBuilder();
        for (var i = 0; i < texts.Count; i++)
        {
            var label = labels != null && i < labels.Count ? labels[i] : null;
            if (!string.IsNullOrEmpty(label))
            {
                sb.Append('[').Append(label).Append("] ");
            }
            sb.Append(Normalize(texts[i])).Append(Environment.NewLine);
        }
        return sb.ToString();
    }

    /// <summary>A rendered entry carries its exception's own line breaks; the
    /// clipboard wants them in the platform's spelling.</summary>
    private static string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }
        return text.Contains('\n')
            ? text.Replace("\r\n", "\n").Replace("\n", Environment.NewLine)
            : text;
    }
}
