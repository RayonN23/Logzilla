using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Logzilla.App.Controls;
using Logzilla.App.Infrastructure;
using Logzilla.Core.Common;
using Logzilla.Core.Formats;
using Logzilla.Core.Logs;
using Logzilla.Core.Query;

namespace Logzilla.App.Views;

// The right-hand analysis panels: summary, sources, fields, patterns,
// entry, marks and colour rules.

internal sealed partial class MainForm : Form
{
    private static readonly string[] TemplateKeys = ["@mt", "MessageTemplate"];

    private void FillSide()
    {
        SyncSideTabs();
        if (_sideTab == "ask")
        {
            RefreshAsk();
            return;
        }
        var f = F;
        if (f == null || _res == null)
        {
            var empty = new List<PanelRow>();
            empty.Add(Head("NOTHING OPEN"));
            empty.Add(Kv("Ctrl+O to open a log,", ""));
            empty.Add(Kv("or drop files on the window.", ""));
            _side.SetItems(empty); _entry.Text = "";
            return;
        }
        if (_sideTab == "summary")
        {
            _side.SetItems(BuildSummary(f));
        }
        else if (_sideTab == "sources")
{
    _side.SetItems(BuildSources(f));
}
        else if (_sideTab == "fields" || _sideTab == "patterns")
        {
            FillSampledPanel(f);
        }
        else if (_sideTab == "marks")
        {
            _side.SetItems(BuildMarks(f));
        }
        else if (_sideTab == "colors")
        {
            _side.SetItems(BuildColors());
        }
        else if (_sideTab == "detail")
        {
            FillEntry(f);
        }
    }

    private List<PanelRow> BuildSources(LogDocument f)
    {
        var l = new List<PanelRow>();
        if (!f.Merged)
        {
            var s0 = f.Srcs[0];
            l.Add(Head("SINGLE FILE"));
            l.Add(Kv("Name", s0.Name));
            l.Add(Kv("Size", DisplayFormatter.Bytes(s0.Size)));
            l.Add(Kv("Encoding", s0.Encoding.WebName.ToUpperInvariant()));
            l.Add(Kv("Timestamps", s0.Kind == TsKind.None ? "none found" : s0.Kind.ToString().ToLowerInvariant()));
            AddZoneRow(l, s0, 0);
            l.Add(Kv("Entries", DisplayFormatter.Number(f.N)));
            l.Add(Head("FOLDER"));
            var fo = new PanelRow(Path.GetDirectoryName(s0.Path), "");
            fo.Accent = Theme.Text3; fo.Tag = "folder:0";
            l.Add(fo);
            l.Add(Head("MERGE"));
            var mg = new PanelRow("Open another log, then Ctrl+M", "");
            mg.Accent = Theme.Accent; mg.Tag = "merge";
            l.Add(mg);
            return l;
        }
        l.Add(Head("MERGED SOURCES  (click to show or hide)"));
        var mx = 1;
        for (var i = 0; i < _res.SrcCounts.Length; i++)
        {
            if (_res.SrcCounts[i] > mx)
{
    mx = _res.SrcCounts[i];
}
        }
        for (var i = 0; i < f.Srcs.Count; i++)
        {
            var on = ((_srcMask >> i) & 1L) != 0;
            var it = new PanelRow(f.Srcs[i].Label, DisplayFormatter.Number(_res.SrcCounts[i]));
            it.Accent = on ? Palette.Pick(f.Srcs[i].TintIndex) : Theme.TextDim;
            it.Bar = (float)_res.SrcCounts[i] / mx;
            it.Tag = "src:" + i;
            l.Add(it);
        }
        l.Add(Head("ACTIONS"));
        var a = new PanelRow("Show every source", ""); a.Accent = Theme.Accent; a.Tag = "srcall"; l.Add(a);
        l.Add(Head("FILES"));
        for (var i = 0; i < f.Srcs.Count; i++)
        {
            var it = new PanelRow(f.Srcs[i].Name, DisplayFormatter.Bytes(f.Srcs[i].Size));
            it.Accent = Palette.Pick(f.Srcs[i].TintIndex); it.Indent = 1; it.Tag = "folder:" + i;
            l.Add(it);
        }
        l.Add(Head("TIME ZONES  (click to set one the stamps don't give)"));
        for (var i = 0; i < f.Srcs.Count; i++)
        {
            AddZoneRow(l, f.Srcs[i], i);
        }
        return l;
    }

    private List<PanelRow> BuildSummary(LogDocument f)
    {
        var l = new List<PanelRow>();
        var span = (!double.IsNaN(_res.Lo) && _res.Hi > _res.Lo) ? _res.Hi - _res.Lo : 0;
        var errs = _res.Counts[5] + _res.Counts[6];
        l.Add(Head("VOLUME"));
        l.Add(Kv("Entries shown", DisplayFormatter.Number(_res.View.Count)));
        l.Add(Kv("Entries indexed", DisplayFormatter.Number(f.N)));
        l.Add(Kv(f.Merged ? "Files" : "File size", f.Merged ? f.Srcs.Count.ToString(CultureInfo.InvariantCulture) : DisplayFormatter.Bytes(f.Size)));
        l.Add(Kv("Time span", span > 0 ? DisplayFormatter.Duration(span) : "-"));
        l.Add(Kv("First", DisplayFormatter.Time(ShownTime(_res.Lo))));
        l.Add(Kv("Last", DisplayFormatter.Time(ShownTime(_res.Hi))));
        l.Add(Kv("Average rate", span > 0 ? (_res.View.Count / (span / 60000.0)).ToString("0.0", CultureInfo.InvariantCulture) : "-", span > 0 ? "/min" : ""));
        l.Add(Kv("Errors or worse", DisplayFormatter.Number(errs),
            _res.View.Count > 0 ? (errs * 100.0 / _res.View.Count).ToString("0.00", CultureInfo.InvariantCulture) + "%" : ""));
        l.Add(Head("SEVERITY  ·  log scale  ·  click to isolate"));
        // Whole-file counts, so an excluded severity still shows how many lines it hides.
        var totals = SeverityTotals(f);
        var mx = 1;
        for (var i = 0; i < 7; i++)
        {
            if (totals[i] > mx)
            {
                mx = totals[i];
            }
        }
        var logMax = Math.Log(mx + 1);
        for (var i = 6; i >= 1; i--)
        {
            var included = ((_levelMask >> i) & 1) == 1;
            var it = new PanelRow(Levels.Abbreviation[i], DisplayFormatter.Number(totals[i]));
            it.Accent = Theme.SevColor[i];
            it.Dot = included;
            it.Bar = logMax > 0 ? (float)(Math.Log(totals[i] + 1) / logMax) : 0;
            it.BarOutline = !included;
            it.Unit = f.N > 0 ? (totals[i] * 100.0 / f.N).ToString("0.0", CultureInfo.InvariantCulture) + "%" : "";
            it.Tag = "lv:" + i;
            l.Add(it);
        }
        var bursts = BuildBursts();
        if (bursts.Count > 0) { l.Add(Head("BUSIEST ERROR WINDOWS")); l.AddRange(bursts); }
        return l;
    }

    private List<PanelRow> BuildBursts()
    {
        var l = new List<PanelRow>();
        if (_res.BucketErr == null || double.IsNaN(_res.Lo) || _res.Hi <= _res.Lo)
        {
            return l;
        }
        var idx = new List<int>();
        for (var i = 0; i < FilterResult.Buckets; i++)
        {
            if (_res.BucketErr[i] > 0)
{
    idx.Add(i);
}
        }
        idx.Sort(delegate (int a, int b) { return _res.BucketErr[b].CompareTo(_res.BucketErr[a]); });
        var span = _res.Hi - _res.Lo;
        var top = idx.Count > 0 ? _res.BucketErr[idx[0]] : 1;
        for (var k = 0; k < Math.Min(5, idx.Count); k++)
        {
            var t = _res.Lo + (double)idx[k] / FilterResult.Buckets * span;
            var it = new PanelRow(DisplayFormatter.Time(ShownTime(t)), DisplayFormatter.Number(_res.BucketErr[idx[k]]));
            it.Accent = Theme.SevColor[5];
            it.Chevron = true;
            it.Unit = "errors";
            it.Bar = (float)_res.BucketErr[idx[k]] / Math.Max(1, top);
            it.Tag = "time:" + t.ToString("R", CultureInfo.InvariantCulture);
            l.Add(it);
        }
        return l;
    }

    private static PanelRow Head(string t) { var i = new PanelRow(t, ""); i.Header = true; return i; }
    private static PanelRow Kv(string k, string v, string unit = "") { var i = new PanelRow(k, v); i.Accent = Theme.Text2; i.UiLabel = true; i.Unit = unit; return i; }

    [GeneratedRegex("([A-Za-z_][\\w.\\-]{1,32})=(?:\"([^\"]{0,120})\"|'([^']{0,120})'|([^\\s,;)\\]}\"']{1,120}))")]
    private static partial Regex RxKv { get; }
    [GeneratedRegex("\\b(?:\\d{1,3}\\.){3}\\d{1,3}\\b")]
    private static partial Regex RxIp { get; }
    [GeneratedRegex("\\[([^\\]\\[]{1,48})\\]")]
    private static partial Regex RxTag { get; }

    /// <summary>Bumped by every refresh of a sampled panel, so a build that finishes
    /// after a newer one has started is dropped instead of shown.</summary>
    private int _sampledGen;

    /// <summary>The last sampled panel built: which tab, for which result, and its rows.</summary>
    private (string Tab, FilterResult Res, List<PanelRow> Rows) _sampled;

    /// <summary>
    /// Fields and Patterns read and parse tens of thousands of entries: a few hundred
    /// milliseconds that used to freeze the window after every search, and every second
    /// while following a file. They are built on a worker thread now, and kept for the
    /// result they describe, so coming back to the tab costs nothing.
    /// </summary>
    private void FillSampledPanel(LogDocument f)
    {
        var tab = _sideTab;
        var res = _res;
        var gen = ++_sampledGen;
        if (_sampled.Tab == tab && ReferenceEquals(_sampled.Res, res))
        {
            _side.SetItems(_sampled.Rows);
            return;
        }
        if (_sampled.Tab != tab)
        {
            // The other panel's rows would be wrong meanwhile. This panel's rows for an
            // older result are only out of date, and less jarring than a blank.
            _side.SetItems([Head(tab == "fields" ? "READING FIELDS ..." : "FINDING PATTERNS ...")]);
        }
        ThreadPool.QueueUserWorkItem(delegate
        {
            List<PanelRow> rows;
            try
            {
                rows = tab == "fields" ? BuildFields(f, res) : BuildPatterns(f, res);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                IgnoreNonCritical(ex, "Build " + tab);
                return;
            }
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    if (gen != _sampledGen || _sideTab != tab)
                    {
                        return;
                    }
                    _sampled = (tab, res, rows);
                    _side.SetItems(rows);
                });
            }
            catch (InvalidOperationException ex) { IgnoreNonCritical(ex, "Show " + tab); }
        });
    }

    private List<PanelRow> BuildFields(LogDocument f, FilterResult res)
    {
        var outp = new List<PanelRow>();
        var fields = new Dictionary<string, Dictionary<string, int>>();
        var cnt = res.View.Count;
        var step = Math.Max(1, cnt / 40000);
        var scanned = 0;
        var named = new List<string>();

        using (var feed = _indexer.OpenReader(f, 1 << 20))
        {
            for (var k = 0; k < cnt; k += step)
            {
                var i = res.View[k];
                string s;
                try { s = feed.Text(i); }
                catch { continue; }
                if (f.SrcOf(i).IsJson)
                {
                    // Structured logs already name their fields; use them verbatim
                    // so a click filters on the exact JSON text in the file.
                    scanned++;
                    if (f.Sev[i] > 0)
                    {
                        Add(fields, "level", Levels.Name[f.Sev[i]]);
                    }
                    var kv = _jsonReader.Parse(s);
                    var taken = 0;
                    for (var q = 0; q < kv.Count && taken < 40; q++)
                    {
                        if (_jsonReader.IsMetadataKey(kv[q].Key))
                        {
                            continue;
                        }
                        var vv = kv[q].Value;
                        if (vv.Length == 0 || vv.Length > 120)
                        {
                            continue;
                        }
                        if (vv.Length > 0 && (vv[0] == '{' || vv[0] == '['))
                        {
                            continue;
                        }
                        Add(fields, kv[q].Key, vv);
                        taken++;
                    }
                    continue;
                }
                if (f.Skip[i] > 0 && f.Skip[i] < s.Length)
                {
                    s = s.Substring(f.Skip[i]);
                }
                if (s.Length > 1200)
                {
                    s = s.Substring(0, 1200);
                }
                scanned++;
                if (f.Sev[i] > 0)
                {
                    Add(fields, "level", Levels.Name[f.Sev[i]]);
                }
                if (f.Merged)
                {
                    Add(fields, "file", f.Srcs[f.Src[i]].Label);
                }
                var guard = 0;
                named.Clear();
                foreach (Match m in RxKv.Matches(s))
                {
                    if (guard++ > 20)
                    {
                        break;
                    }
                    var v = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Success ? m.Groups[3].Value : m.Groups[4].Value;
                    named.Add(v);
                    Add(fields, m.Groups[1].Value.ToLowerInvariant(), v);
                }
                guard = 0;
                foreach (Match m in RxIp.Matches(s))
                {
                    if (guard++ > 6)
                    {
                        break;
                    }
                    if (!named.Contains(m.Value))
                    {
                        Add(fields, "ip", m.Value);
                    }
                }
                guard = 0;
                foreach (Match m in RxTag.Matches(s))
                {
                    if (guard++ > 4)
                    {
                        break;
                    }
                    var t = m.Groups[1].Value.Trim();
                    if (t.Length > 1 && !IsNumeric(t))
                    {
                        Add(fields, "tag", t);
                    }
                }
            }
        }

        if (fields.Count == 0)
        {
            outp.Add(Head("NO STRUCTURED FIELDS FOUND"));
            outp.Add(Kv("Logzilla looks for key=value,", ""));
            outp.Add(Kv("IP addresses and [tags].", ""));
            outp.Add(Kv("Try the Patterns tab instead.", ""));
            return outp;
        }

        var keys = new List<KeyValuePair<string, Dictionary<string, int>>>(fields);
        keys.Sort((a, b) => Total(b.Value).CompareTo(Total(a.Value)));

        outp.Add(Head(DisplayFormatter.Number(scanned) + " ENTRIES SAMPLED"));
        outp.Add(Kv("Click a value to filter on it,", ""));
        outp.Add(Kv("Ctrl+click to hide it instead.", ""));
        var shown = 0;
        foreach (var kv in keys)
        {
            if (shown++ > 22)
            {
                break;
            }
            // level, ip, tag and file are read off the text rather than named in it,
            // so they stay search terms; real fields become field filters.
            var bare = kv.Key == "level" || kv.Key == "ip" || kv.Key == "tag" || kv.Key == "file";
            var h = new PanelRow(kv.Key, DisplayFormatter.Number(kv.Value.Count) + " distinct");
            h.Accent = Theme.Accent;
            if (!bare)
            {
                h.Tag = "fieldkey:" + kv.Key;
            }
            outp.Add(h);
            var vals = new List<KeyValuePair<string, int>>(kv.Value);
            vals.Sort(delegate (KeyValuePair<string, int> a, KeyValuePair<string, int> b) { return b.Value.CompareTo(a.Value); });
            for (var i = 0; i < Math.Min(6, vals.Count); i++)
            {
                var it = new PanelRow(vals[i].Key, DisplayFormatter.Number(vals[i].Value));
                it.Indent = 1; it.Accent = Theme.Text;
                it.Tag = bare ? "term:" + vals[i].Key : "field:" + kv.Key + FieldTagSeparator + vals[i].Key;
                outp.Add(it);
            }
        }
        return outp;
    }

    /// <summary>Between a field's key and value in a Fields panel row's tag. A key
    /// never contains it, and a value that did would only lose its tail.</summary>
    private const char FieldTagSeparator = '\u001f';

    private static bool IsNumeric(string s)
    {
        foreach (var c in s)
        {
            if (!char.IsDigit(c) && c != ':' && c != '.' && c != ',' && c != '-' && c != ' ')
{
    return false;
}
        }
        return true;
    }

    private static int Total(Dictionary<string, int> d)
    {
        var t = 0; foreach (var v in d.Values)
{
    t += v;
} return t;
    }

    private static void Add(Dictionary<string, Dictionary<string, int>> f, string k, string v)
    {
        if (string.IsNullOrEmpty(v))
        {
            return;
        }
        if (v.Length > 80)
        {
            v = v.Substring(0, 80);
        }
        Dictionary<string, int> m;
        if (!f.TryGetValue(k, out m)) { m = new Dictionary<string, int>(); f[k] = m; }
        if (m.Count > 3000)
        {
            return;
        }
        int c;
        m[v] = m.TryGetValue(v, out c) ? c + 1 : 1;
    }

    private sealed class Tpl { public int N; public int First; public byte Sev; }

    private List<PanelRow> BuildPatterns(LogDocument f, FilterResult res)
    {
        var map = new Dictionary<string, Tpl>();
        var cnt = res.View.Count;
        var step = Math.Max(1, cnt / 60000);
        var scanned = 0;
        using (var feed = _indexer.OpenReader(f, 1 << 20))
        {
            for (var k = 0; k < cnt; k += step)
            {
                var i = res.View[k];
                if (f.Flag[i] == 1)
                {
                    continue;
                }
                string s;
                try { s = feed.Text(i); }
                catch { continue; }
                string t;
                if (f.SrcOf(i).IsJson)
                {
                    // CLEF carries the template already, so clustering is exact
                    // rather than guessed: no normalising heuristics needed.
                    var kvp = _jsonReader.Parse(s);
                    var mt = _jsonReader.FirstValue(kvp, TemplateKeys);
                    if (string.IsNullOrEmpty(mt))
                    {
                        mt = _jsonReader.FirstValue(kvp, _jsonReader.MessageKeys);
                        mt = string.IsNullOrEmpty(mt) ? "" : Template(mt);
                    }
                    t = mt == null ? "" : mt.Replace('\r', ' ').Replace('\n', ' ');
                    if (t.Length > 150)
                    {
                        t = t.Substring(0, 150);
                    }
                }
                else
                {
                    if (f.Skip[i] > 0 && f.Skip[i] < s.Length)
                    {
                        s = s.Substring(f.Skip[i]);
                    }
                    if (s.Length > 400)
                    {
                        s = s.Substring(0, 400);
                    }
                    t = Template(s);
                }
                if (t.Length == 0)
                {
                    continue;
                }
                scanned++;
                Tpl o;
                if (!map.TryGetValue(t, out o)) { o = new Tpl(); o.First = i; o.Sev = f.Sev[i]; map[t] = o; }
                o.N++;
                if (f.Sev[i] > o.Sev)
                {
                    o.Sev = f.Sev[i];
                }
            }
        }
        var l = new List<PanelRow>();
        l.Add(Head(DisplayFormatter.Number(map.Count) + " SHAPES IN " + DisplayFormatter.Number(scanned) + " ENTRIES"));
        var all = new List<KeyValuePair<string, Tpl>>(map);
        all.Sort(delegate (KeyValuePair<string, Tpl> a, KeyValuePair<string, Tpl> b) { return b.Value.N.CompareTo(a.Value.N); });
        for (var i = 0; i < Math.Min(60, all.Count); i++)
        {
            var o = all[i].Value;
            var it = new PanelRow(all[i].Key, DisplayFormatter.Number(o.N) + "  " + (o.N * 100.0 / Math.Max(1, scanned)).ToString("0.0", CultureInfo.InvariantCulture) + "%");
            it.Accent = Theme.SevColor[o.Sev];
            it.Tag = "jump:" + o.First + "|" + Seed(all[i].Key);
            l.Add(it);
        }
        return l;
    }

    [GeneratedRegex("\\b\\d{4}-\\d{2}-\\d{2}[T ]\\d{2}:\\d{2}:\\d{2}(?:[.,]\\d+)?")]
    private static partial Regex TplTs1 { get; }
    [GeneratedRegex("\\b\\d{1,2}:\\d{2}:\\d{2}(?:[.,]\\d+)?")]
    private static partial Regex TplTs2 { get; }
    [GeneratedRegex("\\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\\b")]
    private static partial Regex TplUuid { get; }
    [GeneratedRegex("\\b(?:\\d{1,3}\\.){3}\\d{1,3}\\b")]
    private static partial Regex TplIp { get; }
    [GeneratedRegex("\\b0x[0-9a-fA-F]+\\b")]
    private static partial Regex TplHex { get; }
    [GeneratedRegex("\\b[0-9a-fA-F]{16,}\\b")]
    private static partial Regex TplHash { get; }
    [GeneratedRegex("\"[^\"]{0,200}\"")]
    private static partial Regex TplQ1 { get; }
    [GeneratedRegex("'[^']{0,200}'")]
    private static partial Regex TplQ2 { get; }
    [GeneratedRegex("([^\\w.]|^)\\d+(?:\\.\\d+)?")]
    private static partial Regex TplNum { get; }
    [GeneratedRegex("\\s+")]
    private static partial Regex TplWs { get; }

    private static string Template(string s)
    {
        s = TplTs1.Replace(s, "<ts>");
        s = TplTs2.Replace(s, "<ts>");
        s = TplUuid.Replace(s, "<uuid>");
        s = TplIp.Replace(s, "<ip>");
        s = TplHex.Replace(s, "<hex>");
        s = TplHash.Replace(s, "<hash>");
        s = TplQ1.Replace(s, "\"...\"");
        s = TplQ2.Replace(s, "'...'");
        s = TplNum.Replace(s, "$1#");
        s = TplWs.Replace(s, " ").Trim();
        return s.Length > 150 ? s.Substring(0, 150) : s;
    }

    private static string Seed(string tpl)
    {
        var parts = Regex.Split(tpl, "[^A-Za-z]+");
        string best = "", second = "";
        foreach (var p in parts)
        {
            if (p.Length > best.Length) { second = best; best = p; }
            else if (p.Length > second.Length)
            {
                second = p;
            }
        }
        var s = (best + " " + second).Trim();
        return s.Length > 3 ? s : best;
    }

    private List<PanelRow> BuildMarks(LogDocument f)
    {
        var l = new List<PanelRow>();
        if (f.Marks.Count == 0)
        {
            l.Add(Head("NO BOOKMARKS YET"));
            l.Add(Kv("M   bookmark the selected entry, or remove it", ""));
            l.Add(Kv("B / Shift+B   next / previous bookmark", ""));
            return l;
        }
        var ms = new List<int>(f.Marks);
        ms.Sort();
        l.Add(Head(DisplayFormatter.Number(ms.Count) + " BOOKMARKED  (B / Shift+B to navigate)"));
        var cp = new PanelRow("Copy all bookmarked entries", ""); cp.Accent = Theme.Accent; cp.Tag = "copymarks"; l.Add(cp);
        var cl = new PanelRow("Clear bookmarks", ""); cl.Accent = Theme.Text3; cl.Tag = "clearmarks"; l.Add(cl);
        l.Add(Head("ENTRIES"));
        // The message as the grid shows it: without the timestamp and level the right-hand
        // column already gives, and a JSON record as its sentence rather than raw JSON.
        foreach (var i in ms)
        {
            var t = _view.ReadableTextOf(i);
            var nlAt = t.IndexOf('\n');
            if (nlAt >= 0)
            {
                t = t[..nlAt].TrimEnd('\r');
            }
            if (t.Length > 120)
            {
                t = t.Substring(0, 120);
            }
            var timeStr = ShowLineTime(f, i);
            var right = "#" + DisplayFormatter.Number(i + 1) + (timeStr.Length > 0 ? "  " + timeStr : "");
            var it = new PanelRow(t, right);
            it.Accent = f.Sev[i] > 0 ? Theme.SevColor[f.Sev[i]] : Theme.SevColor[4];
            it.Tag = "jump:" + i + "|";
            l.Add(it);
        }
        return l;
    }

    private List<PanelRow> BuildColors()
    {
        var l = new List<PanelRow>();
        l.Add(Head("HIGHLIGHTS"));
        if (_rules.Count == 0)
        {
            l.Add(Kv("Nothing is highlighted yet.", ""));
            l.Add(Kv("Type a word or phrase above and press Enter.", ""));
            l.Add(Kv("Or right-click a word in the log and choose Highlight.", ""));
        }
        else
        {
            l.Add(Kv("click to switch on or off  ·  right-click for more", ""));
        }
        for (var i = 0; i < _rules.Count; i++)
        {
            var it = new PanelRow(_rules[i].Pattern, (_rules[i].IsRegex ? "regex  " : "") + (_rules[i].On ? "on" : "off"));
            it.Accent = _rules[i].On ? _rules[i].C : Theme.TextDim;
            it.Tag = "rule:" + i;
            l.Add(it);
        }
        l.Add(Head("SETUP"));
        var c = new PanelRow("Save setup to file", ""); c.Accent = Theme.Text3; c.Tag = "savecfg"; l.Add(c);
        var d = new PanelRow("Load setup from file", ""); d.Accent = Theme.Text3; d.Tag = "loadcfg"; l.Add(d);
        return l;
    }

    private void RefreshMarkBadge()
    {
        if (_bMarksSideTab == null)
        {
            return;
        }
        var f = F;
        var cnt = f != null ? f.Marks.Count : 0;
        _bMarksSideTab.Text = cnt > 0 ? "Bookmarks " + cnt : "Bookmarks";
        _bMarksSideTab.FitTo(40, Theme.TitleBarHeight, 8);
        // "Bookmarks 12" is wider than "Bookmarks", which can push the last tab onto a new row.
        FitSideTabs();
    }

    private void FillEntry(LogDocument f)
    {
        if (_view.Selected < 0) { _entry.Text = "Select an entry to inspect it."; return; }
        var i = _view.Selected;
        while (i > 0 && f.Flag[i] == 1)
        {
            i--;
        }
        var sb = new StringBuilder();
        sb.AppendLine("line      " + DisplayFormatter.Number(i + 1));
        var shown = ShowLineTime(f, i);
        sb.AppendLine("time      " + (shown.Length > 0 ? shown : "none"));
        sb.AppendLine("zone      " + TimeZones.Describe(f.SrcOf(i), _localZone) + ZoneShownNote(f.SrcOf(i)));
        sb.AppendLine("severity  " + (f.Sev[i] > 0 ? Levels.Name[f.Sev[i]] : "unclassified"));
        if (f.Merged)
        {
            sb.AppendLine("file      " + f.Srcs[f.Src[i]].Name);
        }
        sb.AppendLine("offset    " + f.Off[i] + "  (" + f.Len[i] + " bytes)");
        sb.AppendLine(new string('-', 46));
        if (f.SrcOf(i).IsJson)
        {
            sb.AppendLine(_jsonReader.Describe(_view.TextOf(i)));
            sb.AppendLine(new string('-', 46));
            sb.AppendLine("RAW");
            sb.AppendLine(_view.TextOf(i));
        }
        else
        {
            // The whole entry, header line plus every continuation under it — a
            // wrapped XML payload or a stack trace is one entry, not five rows.
            var body = new StringBuilder(_view.TextOf(i));
            var j = i + 1;
            for (; j < f.N && f.Flag[j] == 1 && j - i <= MaxContinuationLines; j++)
            {
                body.Append('\n').Append(_view.TextOf(j));
            }
            sb.AppendLine(Readable(body.ToString()));
            if (j < f.N && f.Flag[j] == 1)
            {
                sb.AppendLine("... the entry continues past line " + DisplayFormatter.Number(j));
            }
        }
        _entry.Text = sb.ToString();
        _entry.Select(0, 0);
    }

    /// <summary>How far past an entry's header line the panel will follow its
    /// continuations: every line of any real payload, but not an entire file whose
    /// lines carry no timestamps and so all hang off the first one.</summary>
    private const int MaxContinuationLines = 50_000;

    /// <summary>Indents an embedded XML payload; anything else comes back untouched.
    /// The panel is the one place with room to lay a payload out properly.</summary>
    private static string Readable(string text)
    {
        return XmlText.PrettyWithin(text).Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
    }

    private void SideActivate(object sender, PanelList.ItemArgs e)
    {
        if (e.Item == null || e.Item.Tag == null)
        {
            return;
        }
        var tag = Convert.ToString(e.Item.Tag, CultureInfo.InvariantCulture);
        var f = F;
        if (tag.StartsWith("lv:", StringComparison.Ordinal))
        {
            var lv = int.Parse(tag.AsSpan(3), NumberStyles.Integer, CultureInfo.InvariantCulture);
            _levelMask = _levelMask == (1 << lv) ? 0x7F : (1 << lv);
            SyncLevels(); StartFilter();
        }
        else if (tag.StartsWith("src:", StringComparison.Ordinal))
        {
            var si = int.Parse(tag.AsSpan(4), NumberStyles.Integer, CultureInfo.InvariantCulture);
            if (e.Ctrl)
            {
                _srcMask = 1L << si;
            }
            else
            {
                _srcMask ^= (1L << si);
            }
            if (_srcMask == 0)
            {
                _srcMask = -1;
            }
            StartFilter();
        }
        else if (tag == "srcall") { _srcMask = -1; StartFilter(); }
        else if (tag.StartsWith("zone:", StringComparison.Ordinal))
        {
            var si = int.Parse(tag.AsSpan(5), NumberStyles.Integer, CultureInfo.InvariantCulture);
            if (f != null && si < f.Srcs.Count)
            {
                AskZone(f.Srcs[si]);
            }
        }
        else if (tag == "merge")
        {
            DoMerge();
        }
        else if (tag.StartsWith("folder:", StringComparison.Ordinal))
        {
            var si = int.Parse(tag.AsSpan(7), NumberStyles.Integer, CultureInfo.InvariantCulture);
            if (f != null && si < f.Srcs.Count)
            {
                ShowInExplorer(f.Srcs[si].Path);
            }
        }
        else if (tag.StartsWith("term:", StringComparison.Ordinal))
        {
            AddTerm(tag[5..]);
        }
        else if (tag.StartsWith("field:", StringComparison.Ordinal))
        {
            var rest = tag[6..];
            var sep = rest.IndexOf(FieldTagSeparator);
            if (sep > 0)
            {
                AddFieldFilter(rest[..sep], e.Ctrl ? FieldOp.IsNot : FieldOp.Is, rest[(sep + 1)..]);
            }
        }
        else if (tag.StartsWith("fieldkey:", StringComparison.Ordinal))
        {
            OpenFieldEditor(-1, tag[9..]);
        }
        else if (tag.StartsWith("time:", StringComparison.Ordinal))
        {
            SeekTime(double.Parse(tag.AsSpan(5), NumberStyles.Float, CultureInfo.InvariantCulture));
        }
        else if (tag.StartsWith("jump:", StringComparison.Ordinal))
        {
            var rest = tag[5..];
            var bar = rest.IndexOf('|');
            var line = int.Parse(bar >= 0 ? rest.AsSpan(0, bar) : rest.AsSpan(), NumberStyles.Integer, CultureInfo.InvariantCulture);
            var seed = bar >= 0 ? rest[(bar + 1)..] : "";
            if (e.Ctrl && seed.Length > 0)
            {
                AddTerm(seed);
            }
            else
            {
                _view.Select(line, true);
            }
        }
        else if (tag.StartsWith("rule:", StringComparison.Ordinal))
        {
            var i = int.Parse(tag.AsSpan(5), NumberStyles.Integer, CultureInfo.InvariantCulture);
            if (i >= _rules.Count)
            {
                return;
            }
            if (e.Right)
            {
                ShowRuleMenu(i);
                return;
            }
            if (e.Ctrl)
            {
                RecolourRule(_rules[i]);
                return;
            }
            _rules[i].On = !_rules[i].On;
            RulesChanged();
        }
        else if (tag == "copymarks")
{
    CopyMarks();
}
        else if (tag == "clearmarks") { if (f != null) { f.Marks.Clear(); _view.Invalidate(); FillSide(); } }
        else if (tag == "savecfg")
{
    SaveSetup();
}
        else if (tag == "loadcfg")
{
    LoadSetup();
}
    }

}
