using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Logzilla.Core.Formats;
using Logzilla.Core.Logs;

namespace Logzilla.App.Infrastructure;

/// <summary>
/// The fields a log carries and the values seen in each, drawn from a sample of its
/// entries — what the field filter editor offers as you type, so nobody has to ///
/// JSON records contribute their keys, nested ones included. Plain-text lines
/// contribute their key=value pairs, the same ones the Fields panel lists.
/// </summary>
internal sealed partial class FieldCatalog
{
    /// <summary>Entries read at most. Enough to see every common field and its usual
    /// values; the editor still accepts anything typed.</summary>
    public const int SampleEntries = 20000;

    private const int MaxValuesPerField = 3000;
    private const int MaxValueLength = 200;
    private const int MaxDepth = 3;

    private sealed class Field
    {
        public string Key;
        public int Entries;
        public readonly Dictionary<string, int> Values = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly Dictionary<string, Field> _fields = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many entries were read.</summary>
    public int Scanned { get; private set; }

    /// <summary>Every field seen, most common first; among equals, named fields before
    /// CLEF's @-keys, then alphabetically.</summary>
    public List<(string Key, int Entries)> Keys() =>
        _fields.Values.OrderByDescending(f => f.Entries).ThenBy(f => f.Key.StartsWith('@') ? 1 : 0)
            .ThenBy(f => f.Key, StringComparer.OrdinalIgnoreCase)
            .Select(f => (f.Key, f.Entries)).ToList();

    /// <summary>The values seen for a field, most common first; empty when the field
    /// was never seen.</summary>
    public List<(string Value, int Count)> ValuesOf(string key)
    {
        if (string.IsNullOrEmpty(key) || !_fields.TryGetValue(key.Trim(), out var f))
        {
            return [];
        }
        return f.Values.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => (kv.Key, kv.Value)).ToList();
    }

    /// <summary>The field's name as the log spells it, for a key typed in another case.</summary>
    public string Spelling(string key) =>
        !string.IsNullOrEmpty(key) && _fields.TryGetValue(key.Trim(), out var f) ? f.Key : key?.Trim();

    /// <summary>Records one entry's fields. Each field counts once per entry however
    /// often it repeats.</summary>
    public void AddEntry(IEnumerable<KeyValuePair<string, string>> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        Scanned++;
        HashSet<Field> seen = null;
        foreach (var (key, value) in pairs)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrEmpty(value) || value.Length > MaxValueLength)
            {
                continue;
            }
            if (!_fields.TryGetValue(key, out var f))
            {
                f = new Field { Key = key };
                _fields[key] = f;
            }
            seen ??= [];
            if (seen.Add(f))
            {
                f.Entries++;
            }
            if (f.Values.TryGetValue(value, out var n))
            {
                f.Values[value] = n + 1;
            }
            else if (f.Values.Count < MaxValuesPerField)
            {
                f.Values[value] = 1;
            }
        }
    }

    // ── reading a document ─────────────────────────────────────────────

    /// <summary>JSON keys that are the record's own plumbing — time, level, message,
    /// template, exception, renderings — rather than something to filter on. Trace,
    /// span and event ids (@tr, @sp, @i) are exactly what people filter on, so they stay.</summary>
    private static readonly HashSet<string> JsonPlumbing = new(StringComparer.Ordinal)
    {
        "@t", "@m", "@mt", "@l", "@x", "@r",
    };

    private static readonly HashSet<string> KeptMetadata = new(StringComparer.Ordinal) { "@tr", "@sp", "@i" };

    [GeneratedRegex("([A-Za-z_][\\w.\\-]{1,32})=(?:\"([^\"]{0,120})\"|'([^']{0,120})'|([^\\s,;)\\]}\"']{1,120}))")]
    private static partial Regex KeyValue { get; }

    /// <summary>Samples up to <see cref="SampleEntries"/> entry headers spread evenly
    /// through the document.</summary>
    public static FieldCatalog Build(LogDocument document, ILineReader reader, IJsonLogReader json)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(json);
        var catalog = new FieldCatalog();
        var n = document.N;
        var step = Math.Max(1, n / SampleEntries);
        var pairs = new List<KeyValuePair<string, string>>();
        for (var i = 0; i < n; i += step)
        {
            // A header line and the continuations up to the next sample point, so a
            // key=value on a stack trace line is seen too.
            pairs.Clear();
            var end = Math.Min(n, i + step);
            for (var j = i; j < end; j++)
            {
                if (j > i && document.Flag[j] == 0)
                {
                    break;
                }
                string text;
                try { text = reader.Text(j); }
                catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { break; }
                if (document.SrcOf(j).IsJson)
                {
                    AddJson(pairs, json, json.Parse(text), 0);
                }
                else
                {
                    AddText(pairs, text);
                }
            }
            catalog.AddEntry(pairs);
        }
        return catalog;
    }

    private static void AddJson(List<KeyValuePair<string, string>> pairs, IJsonLogReader json,
                                IReadOnlyList<KeyValuePair<string, string>> kv, int depth)
    {
        foreach (var (key, value) in kv)
        {
            if (string.IsNullOrWhiteSpace(key) || value == null || JsonPlumbing.Contains(key) || (json.IsMetadataKey(key) && !KeptMetadata.Contains(key) && depth == 0))
            {
                continue;
            }
            if (value.Length > 0 && value[0] == '{')
            {
                if (depth < MaxDepth)
                {
                    AddJson(pairs, json, json.Parse(value), depth + 1);
                }
                continue;
            }
            if (value.Length > 0 && value[0] == '[')
            {
                continue;
            }
            pairs.Add(new KeyValuePair<string, string>(key, value));
        }
    }

    private static void AddText(List<KeyValuePair<string, string>> pairs, string text)
    {
        if (text.Length > 4000)
        {
            text = text[..4000];
        }
        var taken = 0;
        foreach (Match m in KeyValue.Matches(text))
        {
            if (taken++ > 30)
            {
                break;
            }
            var v = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Success ? m.Groups[3].Value : m.Groups[4].Value;
            pairs.Add(new KeyValuePair<string, string>(m.Groups[1].Value, v));
        }
    }
}
