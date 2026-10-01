using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Logzilla.AI.Privacy;
using Logzilla.Core.Common;
using Logzilla.Core.Formats;
using Logzilla.Core.Logs;
using Logzilla.Core.Query;

namespace Logzilla.AI.Tools;

/// <summary>
/// The only things a model can do: read the log that is open, through the same
/// engine the grid uses. Every tool is read-only — none can write, run anything,
/// open another file or reach the network — so a log line crafted to steer the
/// model has nothing dangerous to steer it towards.
///
/// A multi-gigabyte log does not fit in a prompt, so the tools answer questions
/// about it instead: counts come from the engine, never from the model, and every
/// entry comes back with its line number so the answer can cite it. Output is capped
/// and passed through the <see cref="Redactor"/> before it leaves the process.
/// </summary>
public sealed class LogTools
{
    public const int MaxOutputChars = 16000;
    public const int MaxLineChars = 300;
    public const int MaxEntryChars = 3000;
    public const int PatternSample = 50000;

    private readonly ILogContext _ctx;
    private readonly IJsonLogReader _json;
    private readonly Redactor _redactor;

    public LogTools(ILogContext context, IJsonLogReader json, Redactor redactor)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(redactor);
        _ctx = context;
        _json = json;
        _redactor = redactor;
        Definitions =
        [
            new("get_overview",
                "Start here. Summarises the open log: files, entry counts by level, time span, " +
                "time zone, the user's current filters and the busiest error windows.",
                GetOverview),
            new("search_log",
                "Finds entries by text, level, time range or a field value, the way the user's own " +
                "filters do. Returns how many entries match, their levels, and the first matches with " +
                "line numbers. Words must all appear; \"quoted phrase\" matches exactly; -word excludes.",
                SearchLog),
            new("get_entries",
                "Reads whole entries (with stack traces) around a line number, to see what happened " +
                "before and after it.",
                GetEntries),
            new("count_over_time",
                "Counts matching entries in equal time slices, with warnings and errors, to find when " +
                "something started, peaked or stopped.",
                CountOverTime),
            new("find_patterns",
                "Groups entries that differ only in numbers, ids and addresses, and counts each shape. " +
                "The most common shapes are the normal noise; rare ones are often the problem.",
                FindPatterns),
            new("list_fields",
                "Lists the structured fields the log records (JSON properties or key=value pairs), " +
                "most common first.",
                ListFields),
            new("get_field_values",
                "Lists the values one field takes, most common first, with counts.",
                GetFieldValues),
        ];
    }

    /// <summary>Raised after each tool call, for the transcript. May fire on any thread.</summary>
    public event Action<ToolActivity> Activity;

    /// <summary>
    /// Raised with exactly what a tool is about to hand the model (already redacted), so
    /// the host can keep a record of everything that left the machine.
    /// </summary>
    public event Action<string, string> Sending;

    public IReadOnlyList<ToolDefinition> Definitions { get; }

    // ── tools ────────────────────────────────────────────────────────

    [Description("Summarises the open log.")]
    public string GetOverview()
    {
        return Run("get_overview", () =>
        {
            var doc = RequireDocument();
            var r = Scoped(Everything());
            var sb = new StringBuilder();
            sb.Append("Files: ").Append(doc.Srcs.Count).AppendLine();
            foreach (var s in doc.Srcs)
            {
                sb.Append("  ").Append(s.Name).Append(" — ").Append(DisplayFormatter.Number(s.Lines)).Append(" lines, ")
                  .Append(DisplayFormatter.Bytes(s.Size)).Append(", ")
                  .Append(s.IsJson ? "JSON" : "plain text").Append(", time zone ")
                  .Append(TimeZones.Describe(s, TimeZoneInfo.Local)).AppendLine();
            }
            sb.Append("Times are given in ").Append(_ctx.ClockName).AppendLine(".");
            var filters = _ctx.FilterSummary;
            sb.Append("User's current filters: ").Append(string.IsNullOrEmpty(filters) ? "none" : filters).AppendLine();
            sb.Append("Scope: ").Append(_ctx.ScopeToView ? "only the entries the user's filters show" : "the whole log").AppendLine();
            var headers = Headers(doc, r, false);
            sb.Append("Entries: ").Append(DisplayFormatter.Number(headers.Count)).AppendLine();
            sb.Append("By level: ").Append(LevelBreakdown(doc, headers)).AppendLine();
            if (!double.IsNaN(r.Lo))
            {
                var reference = TimeZones.Reference(doc);
                sb.Append("From ").Append(_ctx.FormatTime(reference, r.Lo)).Append(" to ")
                  .Append(_ctx.FormatTime(reference, r.Hi))
                  .Append(" (").Append(DisplayFormatter.Duration(r.Hi - r.Lo)).AppendLine(")");
                AppendBusiestErrors(sb, doc, r);
            }
            Report("get_overview", "Read the overview");
            return sb.ToString();
        });
    }

    [Description("Searches the log.")]
    public string SearchLog(
        [Description("Words to find. All must appear; \"a phrase\" matches exactly; -word excludes. Empty matches everything.")] string query = "",
        [Description("True to read query as a .NET regular expression.")] bool regex = false,
        [Description("True to match upper and lower case exactly.")] bool matchCase = false,
        [Description("Lowest level to include: trace, debug, info, warn, error or fatal. Empty for all.")] string minLevel = "",
        [Description("Start of a time range, e.g. \"2026-03-01 08:00\" or \"08:00\", in the log's display clock. Empty for none.")] string from = "",
        [Description("End of the time range. Empty for none.")] string to = "",
        [Description("A field name to filter on, e.g. CorrelationId. Empty for none.")] string field = "",
        [Description("The value that field must have.")] string fieldValue = "",
        [Description("How many matching entries to list, 1 to 100.")] int limit = 30)
    {
        return Run("search_log", () =>
        {
            var doc = RequireDocument();
            var spec = BuildSpec(query, regex, matchCase, minLevel, from, to, field, fieldValue, out var problem);
            if (problem != null)
            {
                return problem;
            }
            var r = Scoped(_ctx.Engine.Run(doc, spec, null, null));
            var hits = string.IsNullOrEmpty(query) ? Headers(doc, r, false) : Matches(doc, r);
            limit = Math.Clamp(limit, 1, 100);
            var sb = new StringBuilder();
            sb.Append("Matched ").Append(DisplayFormatter.Number(hits.Count)).Append(" entries");
            if (hits.Count > 0)
            {
                sb.Append(" (").Append(LevelBreakdown(doc, hits)).Append(')');
            }
            sb.AppendLine(".");
            if (hits.Count > 0)
            {
                sb.Append(hits.Count > limit ? "First " + limit + ":" : "All of them:").AppendLine();
                AppendLines(sb, doc, hits.Take(limit));
            }
            var request = new SearchRequest(query ?? "", regex, matchCase, minLevel ?? "", from ?? "", to ?? "", field ?? "", fieldValue ?? "");
            Report("search_log", "Searched " + Describe(request) + " — " + DisplayFormatter.Number(hits.Count) + " entries", request);
            return sb.ToString();
        });
    }

    [Description("Reads whole entries around a line.")]
    public string GetEntries(
        [Description("The line number to read, as the tools report it (e.g. 4211 for #4211).")] int line,
        [Description("How many entries before it to include, 0 to 10.")] int before = 2,
        [Description("How many entries after it to include, 0 to 10.")] int after = 2)
    {
        return Run("get_entries", () =>
        {
            var doc = RequireDocument();
            if (line < 1 || line > doc.N)
            {
                return "There is no line " + line + ". The log has lines 1 to " + doc.N + ".";
            }
            var at = HeaderOf(doc, line - 1);
            var first = at;
            var taken = 0;
            while (taken < Math.Clamp(before, 0, 10) && first > 0)
            {
                first = HeaderOf(doc, first - 1);
                taken++;
            }
            var sb = new StringBuilder();
            using var reader = _ctx.OpenReader();
            var entry = first;
            var remaining = taken + 1 + Math.Clamp(after, 0, 10);
            while (entry < doc.N && remaining-- > 0 && sb.Length < MaxOutputChars)
            {
                var next = NextHeader(doc, entry);
                sb.Append(entry == at ? ">> " : "   ");
                AppendEntry(sb, doc, reader, entry, next);
                entry = next;
            }
            Report("get_entries", "Read the entries around #" + line);
            return sb.ToString();
        });
    }

    [Description("Counts entries over time.")]
    public string CountOverTime(
        [Description("Words to count, as in search_log. Empty counts everything.")] string query = "",
        [Description("Lowest level to include. Empty for all.")] string minLevel = "",
        [Description("Start of the time range. Empty for the start of the log.")] string from = "",
        [Description("End of the time range. Empty for the end of the log.")] string to = "",
        [Description("How many time slices, 2 to 60.")] int slices = 24)
    {
        return Run("count_over_time", () =>
        {
            var doc = RequireDocument();
            var spec = BuildSpec(query, false, false, minLevel, from, to, "", "", out var problem);
            if (problem != null)
            {
                return problem;
            }
            var r = Scoped(_ctx.Engine.Run(doc, spec, null, null));
            var hits = Headers(doc, r, true);
            if (hits.Count == 0)
            {
                return "No timed entries match.";
            }
            double lo = double.MaxValue, hi = double.MinValue;
            foreach (var i in hits)
            {
                lo = Math.Min(lo, doc.Ts[i]);
                hi = Math.Max(hi, doc.Ts[i]);
            }
            slices = Math.Clamp(slices, 2, 60);
            var width = Math.Max(1.0, (hi - lo) / slices);
            var total = new int[slices];
            var warn = new int[slices];
            var err = new int[slices];
            foreach (var i in hits)
            {
                var b = Math.Min(slices - 1, (int)((doc.Ts[i] - lo) / width));
                total[b]++;
                if (doc.Sev[i] == 4)
                {
                    warn[b]++;
                }
                else if (doc.Sev[i] >= 5)
                {
                    err[b]++;
                }
            }
            var reference = TimeZones.Reference(doc);
            var sb = new StringBuilder();
            sb.Append("Slices of ").Append(DisplayFormatter.Duration(width)).Append(", times in ").Append(_ctx.ClockName).AppendLine(":");
            for (var b = 0; b < slices; b++)
            {
                sb.Append(_ctx.FormatTime(reference, lo + b * width)).Append("  total ").Append(total[b])
                  .Append("  warn ").Append(warn[b]).Append("  error+ ").Append(err[b]).AppendLine();
            }
            Report("count_over_time", "Counted " + (string.IsNullOrEmpty(query) ? "entries" : "\"" + query + "\"") + " over time");
            return sb.ToString();
        });
    }

    [Description("Groups entries by shape.")]
    public string FindPatterns(
        [Description("Words to narrow the entries first, as in search_log. Empty for all.")] string query = "",
        [Description("Lowest level to include. Empty for all.")] string minLevel = "",
        [Description("True lists the rarest shapes first instead of the most common.")] bool rarest = false,
        [Description("How many shapes to list, 1 to 50.")] int limit = 15)
    {
        return Run("find_patterns", () =>
        {
            var doc = RequireDocument();
            var spec = BuildSpec(query, false, false, minLevel, "", "", "", "", out var problem);
            if (problem != null)
            {
                return problem;
            }
            var r = Scoped(_ctx.Engine.Run(doc, spec, null, null));
            var hits = Headers(doc, r, false);
            if (hits.Count == 0)
            {
                return "No entries match.";
            }
            var step = Math.Max(1, hits.Count / PatternSample);
            var groups = new Dictionary<string, (int Count, int Example, byte Worst)>(StringComparer.Ordinal);
            var sampled = 0;
            using (var reader = _ctx.OpenReader())
            {
                for (var k = 0; k < hits.Count; k += step)
                {
                    var i = hits[k];
                    var shape = Shapes.Of(Message(doc, reader, i));
                    sampled++;
                    groups[shape] = groups.TryGetValue(shape, out var g)
                        ? (g.Count + 1, g.Example, Math.Max(g.Worst, doc.Sev[i]))
                        : (1, i, doc.Sev[i]);
                }
            }
            var ordered = rarest
                ? groups.OrderBy(g => g.Value.Count).ThenByDescending(g => g.Value.Worst)
                : groups.OrderByDescending(g => g.Value.Count);
            var sb = new StringBuilder();
            sb.Append(DisplayFormatter.Number(groups.Count)).Append(" distinct shapes in ")
              .Append(DisplayFormatter.Number(hits.Count)).Append(" entries");
            if (step > 1)
            {
                sb.Append(" (every ").Append(step).Append(" entries sampled; counts are scaled)");
            }
            sb.AppendLine(". <n> is a number, <id> an id, <str> a quoted value.");
            foreach (var g in ordered.Take(Math.Clamp(limit, 1, 50)))
            {
                var count = g.Value.Count * step;
                sb.Append(DisplayFormatter.Number(count)).Append("  ")
                  .Append((count * 100.0 / hits.Count).ToString("0.0", CultureInfo.InvariantCulture)).Append("%  ")
                  .Append(LevelName(g.Value.Worst)).Append("  ").Append(g.Key)
                  .Append("  e.g. #").Append(g.Value.Example + 1).AppendLine();
            }
            Report("find_patterns", "Grouped " + DisplayFormatter.Number(sampled) + " entries into " + DisplayFormatter.Number(groups.Count) + " shapes");
            return sb.ToString();
        });
    }

    [Description("Lists the log's fields.")]
    public string ListFields([Description("How many fields to list, 1 to 100.")] int limit = 40)
    {
        return Run("list_fields", () =>
        {
            RequireDocument();
            var fields = _ctx.Fields();
            if (fields.Count == 0)
            {
                return "No structured fields found.";
            }
            var sb = new StringBuilder("Field — entries that carry it:\n");
            foreach (var (key, entries) in fields.Take(Math.Clamp(limit, 1, 100)))
            {
                sb.Append(key).Append(" — ").Append(DisplayFormatter.Number(entries)).AppendLine();
            }
            Report("list_fields", "Listed the log's fields");
            return sb.ToString();
        });
    }

    [Description("Lists one field's values.")]
    public string GetFieldValues(
        [Description("The field name, as list_fields gives it.")] string field,
        [Description("How many values to list, 1 to 100.")] int limit = 25)
    {
        return Run("get_field_values", () =>
        {
            RequireDocument();
            if (string.IsNullOrWhiteSpace(field))
            {
                return "Give a field name; list_fields lists them.";
            }
            var values = _ctx.FieldValues(field);
            if (values.Count == 0)
            {
                return "No values found for " + field + ".";
            }
            var sb = new StringBuilder();
            sb.Append(field).Append(" takes ").Append(values.Count).AppendLine(" values in the sample, most common first:");
            foreach (var (value, count) in values.Take(Math.Clamp(limit, 1, 100)))
            {
                sb.Append(value).Append(" — ").Append(DisplayFormatter.Number(count)).AppendLine();
            }
            Report("get_field_values", "Listed the values of " + field);
            return sb.ToString();
        });
    }

    // ── plumbing ─────────────────────────────────────────────────────

    /// <summary>Runs a tool, so a failure comes back to the model as text rather than
    /// ending the conversation, and every result is capped and redacted.</summary>
    private string Run(string tool, Func<string> body)
    {
        string text;
        try
        {
            text = body();
        }
        catch (InvalidOperationException ex)
        {
            text = ex.Message;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or RegexMatchTimeoutException or System.IO.IOException)
        {
            text = tool + " failed: " + ex.Message;
        }
        text = _redactor.Redact(text ?? "");
        text = text.Length > MaxOutputChars ? text[..MaxOutputChars] + "\n… (cut off; narrow the request)" : text;
        Sending?.Invoke(tool, text);
        return text;
    }

    private void Report(string tool, string summary, SearchRequest search = null)
    {
        Activity?.Invoke(new ToolActivity(tool, _redactor.Redact(summary), search));
    }

    private LogDocument RequireDocument()
    {
        var doc = _ctx.Document;
        if (doc == null || doc.N == 0)
        {
            throw new InvalidOperationException("No log is open in Logzilla.");
        }
        return doc;
    }

    private FilterResult Everything()
    {
        return _ctx.ScopeToView && _ctx.View != null
            ? _ctx.View
            : _ctx.Engine.Run(_ctx.Document, QuerySpec.Build("", false, false, 0x7F, -1, double.NaN, double.NaN, 0, true, true), null, null);
    }

    /// <summary>With the scope set to the user's view, drops whatever their filters hide.</summary>
    private FilterResult Scoped(FilterResult r)
    {
        var view = _ctx.View;
        if (!_ctx.ScopeToView || view == null || ReferenceEquals(r, view))
        {
            return r;
        }
        var doc = _ctx.Document;
        var inView = new BitArray(doc.N);
        foreach (var i in view.View)
        {
            if (i < doc.N)
            {
                inView[i] = true;
            }
        }
        var bits = inView;
        r.View.RemoveAll(i => i >= doc.N || !bits[i]);
        r.Matches.RemoveAll(i => i >= doc.N || !bits[i]);
        return r;
    }

    private QuerySpec BuildSpec(string query, bool regex, bool matchCase, string minLevel, string from, string to,
        string field, string fieldValue, out string problem)
    {
        problem = null;
        if (regex && !string.IsNullOrEmpty(query))
        {
            try
            {
                _ = new Regex(query, RegexOptions.None, TimeSpan.FromSeconds(1));
            }
            catch (ArgumentException ex)
            {
                problem = "That regular expression is not valid: " + ex.Message;
                return null;
            }
        }
        var mask = 0x7F;
        if (!string.IsNullOrWhiteSpace(minLevel))
        {
            var level = LevelIndex(minLevel);
            if (level < 0)
            {
                problem = "Unknown level \"" + minLevel + "\". Use trace, debug, info, warn, error or fatal.";
                return null;
            }
            mask = 0;
            for (var l = level; l < Levels.Count; l++)
            {
                mask |= 1 << l;
            }
        }
        double t0 = double.NaN, t1 = double.NaN;
        if (!string.IsNullOrWhiteSpace(from) && !_ctx.TryParseTime(from, out t0))
        {
            problem = "Could not read the time \"" + from + "\". Use e.g. 2026-03-01 08:00 or 08:00.";
            return null;
        }
        if (!string.IsNullOrWhiteSpace(to) && !_ctx.TryParseTime(to, out t1))
        {
            problem = "Could not read the time \"" + to + "\".";
            return null;
        }
        if (!double.IsNaN(t0) || !double.IsNaN(t1))
        {
            t0 = double.IsNaN(t0) ? double.MinValue : t0;
            t1 = double.IsNaN(t1) ? double.MaxValue : t1;
        }
        var spec = QuerySpec.Build(query ?? "", regex, matchCase, mask, -1, t0, t1, 0, true, true);
        if (!string.IsNullOrWhiteSpace(field) && !string.IsNullOrEmpty(fieldValue))
        {
            spec.Fields.Add(new FieldFilter(field, FieldOp.Is, [fieldValue]));
        }
        return spec;
    }

    /// <summary>The first line of each entry the result keeps.</summary>
    private static List<int> Headers(LogDocument doc, FilterResult r, bool timedOnly)
    {
        var list = new List<int>();
        var prev = -2;
        foreach (var i in r.View)
        {
            if ((doc.Flag[i] == 0 || i != prev + 1) && (!timedOnly || !double.IsNaN(doc.Ts[i])))
            {
                list.Add(i);
            }
            prev = i;
        }
        return list;
    }

    private static List<int> Matches(LogDocument doc, FilterResult r)
    {
        var list = new List<int>(r.Matches.Count);
        foreach (var i in r.Matches)
        {
            if (i < doc.N)
            {
                list.Add(i);
            }
        }
        return list;
    }

    private static int HeaderOf(LogDocument doc, int line)
    {
        var src = doc.Src[line];
        while (line > 0 && doc.Flag[line] == 1 && doc.Src[line - 1] == src)
        {
            line--;
        }
        return line;
    }

    private static int NextHeader(LogDocument doc, int header)
    {
        var src = doc.Src[header];
        var i = header + 1;
        while (i < doc.N && doc.Flag[i] == 1 && doc.Src[i] == src)
        {
            i++;
        }
        return i;
    }

    private void AppendLines(StringBuilder sb, LogDocument doc, IEnumerable<int> lines)
    {
        using var reader = _ctx.OpenReader();
        foreach (var i in lines)
        {
            if (sb.Length > MaxOutputChars)
            {
                break;
            }
            var text = OneLine(Message(doc, reader, i));
            var extra = NextHeader(doc, i) - i - 1;
            sb.Append(Label(doc, i)).Append(' ').Append(text);
            if (extra > 0)
            {
                sb.Append("  (+").Append(extra).Append(extra == 1 ? " line)" : " lines)");
            }
            sb.AppendLine();
        }
    }

    private void AppendEntry(StringBuilder sb, LogDocument doc, ILineReader reader, int header, int end)
    {
        var text = new StringBuilder();
        var src = doc.SrcOf(header);
        if (src.IsJson)
        {
            text.Append(_json.RenderFull(reader.Text(header)));
        }
        else
        {
            text.Append(Message(doc, reader, header));
            for (var i = header + 1; i < end && text.Length < MaxEntryChars; i++)
            {
                text.Append('\n').Append(reader.Text(i));
            }
        }
        var s = text.Length > MaxEntryChars ? text.ToString(0, MaxEntryChars) + " …" : text.ToString();
        sb.Append(Label(doc, header)).Append(' ').Append(s).AppendLine();
    }

    private string Label(LogDocument doc, int i)
    {
        var sb = new StringBuilder();
        sb.Append('#').Append(i + 1);
        if (!double.IsNaN(doc.Ts[i]))
        {
            sb.Append(' ').Append(_ctx.FormatTime(doc.SrcOf(i), doc.Ts[i]));
        }
        if (doc.Sev[i] > 0)
        {
            sb.Append(' ').Append(Levels.Name[doc.Sev[i]]);
        }
        if (doc.Srcs.Count > 1)
        {
            sb.Append(" [").Append(doc.SrcOf(i).Label).Append(']');
        }
        return sb.ToString();
    }

    /// <summary>What the line says, without the timestamp and level already in its label.</summary>
    private string Message(LogDocument doc, ILineReader reader, int i)
    {
        var raw = reader.Text(i);
        if (doc.SrcOf(i).IsJson)
        {
            return _json.RenderLine(raw);
        }
        var skip = doc.Skip[i];
        return skip > 0 && skip < raw.Length ? raw[skip..] : raw;
    }

    private static string OneLine(string s)
    {
        s = s.Replace('\r', ' ').Replace('\n', ' ');
        return s.Length > MaxLineChars ? s[..MaxLineChars] + "…" : s;
    }

    private static string LevelBreakdown(LogDocument doc, List<int> lines)
    {
        var counts = new int[Levels.Count];
        foreach (var i in lines)
        {
            counts[doc.Sev[i]]++;
        }
        var parts = new List<string>();
        for (var l = Levels.Count - 1; l >= 0; l--)
        {
            if (counts[l] > 0)
            {
                parts.Add(LevelName((byte)l) + " " + DisplayFormatter.Number(counts[l]));
            }
        }
        return string.Join(", ", parts);
    }

    private void AppendBusiestErrors(StringBuilder sb, LogDocument doc, FilterResult r)
    {
        if (r.BucketErr == null || r.Hi <= r.Lo)
        {
            return;
        }
        var top = Enumerable.Range(0, r.BucketErr.Length)
            .Where(b => r.BucketErr[b] > 0)
            .OrderByDescending(b => r.BucketErr[b])
            .Take(5)
            .ToList();
        if (top.Count == 0)
        {
            return;
        }
        var reference = TimeZones.Reference(doc);
        var width = (r.Hi - r.Lo) / r.BucketErr.Length;
        sb.Append("Busiest error windows (").Append(DisplayFormatter.Duration(width)).AppendLine(" each):");
        foreach (var b in top)
        {
            sb.Append("  ").Append(_ctx.FormatTime(reference, r.Lo + b * width)).Append(" — ")
              .Append(r.BucketErr[b]).AppendLine(" errors");
        }
    }

    private static string LevelName(byte level) => level == 0 ? "UNLEVELLED" : Levels.Name[level];

    public static int LevelIndex(string text)
    {
        var t = text.Trim().ToUpperInvariant();
        t = t switch
        {
            "WARNING" => "WARN",
            "ERR" => "ERROR",
            "INFORMATION" => "INFO",
            "CRITICAL" or "CRIT" => "FATAL",
            "VERBOSE" => "TRACE",
            _ => t,
        };
        return Array.IndexOf(Levels.Name, t) is var i and > 0 ? i : -1;
    }

    private static string Describe(SearchRequest r)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(r.Query))
        {
            parts.Add((r.Regex ? "/" + r.Query + "/" : "\"" + r.Query + "\""));
        }
        if (!string.IsNullOrEmpty(r.MinLevel))
        {
            parts.Add(r.MinLevel.ToUpperInvariant() + " and above");
        }
        if (!string.IsNullOrEmpty(r.Field))
        {
            parts.Add(r.Field + " is " + r.FieldValue);
        }
        if (!string.IsNullOrEmpty(r.From) || !string.IsNullOrEmpty(r.To))
        {
            parts.Add((string.IsNullOrEmpty(r.From) ? "start" : r.From) + " to " + (string.IsNullOrEmpty(r.To) ? "end" : r.To));
        }
        return parts.Count == 0 ? "everything" : string.Join(", ", parts);
    }
}
