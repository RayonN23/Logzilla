using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Logzilla.App.Infrastructure;
using Logzilla.Core.Common;
using Logzilla.Core.Formats;
using Logzilla.Core.Logs;
using Logzilla.Core.Query;

namespace Logzilla.App.Controls;

internal sealed class Highlight
{
    public int A, B; public Color C; public bool Fill;
    public Highlight(int a, int b, Color c, bool fill) { A = a; B = b; C = c; Fill = fill; }
}

internal sealed class HighlightRule
{
    public string Pattern = "";
    public bool IsRegex;
    public Color C = Palette.Pick(0);
    public bool On = true;
}

/// <summary>
/// The log grid. Owner-drawn: the dimmed prefix, per-run search highlights, the
/// date band and the inline expanded row all need manual run painting that
/// ListView owner-draw makes miserable.
/// </summary>
internal sealed partial class LogView : Control
{
    private const int LineReaderBuffer = 1 << 20;

    /// <summary>Display lines the expanded row gives the message before it stops and
    /// points at the Entry panel. Deep enough for a stack trace or a small XML
    /// payload to be read in place; short enough that one entry cannot swallow the
    /// whole viewport.</summary>
    private const int MaxExpandedLines = 24;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ILogIndexer Indexer { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public IJsonLogReader JsonReader { get; set; }

    private LogDocument _file;
    public FilterResult Res;
    public List<HighlightRule> Rules = new();
    public QuerySpec Query;

    /// <summary>Shows each line exactly as it sits in the file, prefix and JSON
    /// braces and all. Flipping it changes what every cached string says, so it is a
    /// property rather than a field.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Raw
    {
        get => _raw;
        set
        {
            if (_raw == value)
            {
                return;
            }
            _raw = value;
            _prefixCache.Clear();
            ForgetExpansion();
        }
    }
    private bool _raw;
    /// <summary>Wraps a long message across as many display lines as it needs instead
    /// of running it off the right edge. Mutually exclusive with horizontal scrolling:
    /// there is nothing left to scroll to once everything is on screen.</summary>
    public bool Wrap;
    public int Selected = -1;
    public bool ShowSource = true;
    /// <summary>Shows the date-and-time column, which carries the whole
    /// "yyyy-MM-dd HH:mm:ss.fff" stamp. On by default — a timestamp is the reason
    /// most people open a log. Turning it off collapses the column away entirely and
    /// hands its width to the message; the once-per-day band then keeps the day
    /// visible.</summary>
    public bool ShowTime = true;

    public event EventHandler SelectionChanged;
    public event EventHandler<WordPickedEventArgs> WordPicked;
    public event EventHandler CopyEntryRequested;
    public event EventHandler CopyAsJsonRequested;
    public event EventHandler NextErrorRequested;
    public event EventHandler BookmarkToggleRequested;

    public sealed class WordPickedEventArgs : EventArgs { public string Word; public WordPickedEventArgs(string w) { Word = w; } }

    private readonly ThinScrollBar _v = new(true);
    private readonly ThinScrollBar _h = new(false);
    private int _maxMsgChars;
    private ILineReader _feed;
    private float _cw = 8f;
    private int _rowH = 20;
    private int _textTop;       // baseline offset so mono text centers in _rowH
    private int _hotLine = -1;
    private readonly Dictionary<int, int> _prefixCache = new();

    private readonly struct RowSlot
    {
        public readonly int Line;   // doc line index, -1 for a date band
        public readonly int Y;
        public readonly int Height;
        public readonly string DateLabel;
        /// <summary>Message text, resolved once during layout. Null when not wrapping,
        /// in which case the paint path resolves it itself.</summary>
        public readonly string Text;
        /// <summary>One (start, length) pair per display line in wrap mode; null otherwise.</summary>
        public readonly List<(int Start, int Len)> Wrapped;

        public RowSlot(int line, int y, int height, string dateLabel,
                       string text = null, List<(int Start, int Len)> wrapped = null)
        {
            Line = line; Y = y; Height = height; DateLabel = dateLabel; Text = text; Wrapped = wrapped;
        }
    }

    private List<RowSlot> _slots = new();
    private readonly List<(Rectangle Rect, Action Fire)> _hitActions = new();
    private Rectangle _copyIconRect, _markIconRect;
    private (int Line, int Width, int MsgX, int Height) _expandCache = (-2, -1, -1, -1);
    private (int Line, string Text) _expandText = (-2, null);
    /// <summary>The selected run in doc line indices, resolved once per paint.</summary>
    private (int First, int Last) _paintedSelection = (-1, -2);

    private const int ColGutter = Theme.ColGutter;
    private const int ColSpine = Theme.ColSpine;
    private const int ColLevel = Theme.ColLevel;
    private int _srcColW;

    /// <summary>Measured width of the widest full stamp the column can hold. Measured
    /// rather than derived from the body font, because the stamp is drawn in MonoSmall
    /// — guessing left the year at risk of being painted over by the level tag.</summary>
    private int _timeFullW;

    /// <summary>Wide enough for the whole "yyyy-MM-dd HH:mm:ss.fff" stamp, padding
    /// included, while the column is open. Collapsed it keeps only the left pad, so
    /// the level tag does not end up flush against the severity spine, and every
    /// column to its right slides over to give the message the space.</summary>
    private int TimeColW => ShowTime ? TimeTextPad + _timeFullW + Dpi.S(12) : TimeTextPad;

    /// <summary>Gap between the severity spine and the stamp.</summary>
    private static int TimeTextPad => Dpi.S(10);

    /// <summary>A single pathological line must not be able to push every other entry
    /// off the screen when wrapping is on.</summary>
    private const int MaxWrapLines = 40;

    public LogView()
    {
        AccessibleRole = AccessibleRole.List;
        AccessibleName = "Log entries";
        AccessibleDescription = "One row per log entry. Arrow keys move, Shift with them selects a run, Shift+click or dragging does the same with the mouse, Enter jumps to the next search match, click a row to expand it.";
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.BgData;
        Font = Theme.MonoBody;
        _v.Dock = DockStyle.Right; _v.Width = Dpi.S(10);
        _v.ValueChanged += delegate { Invalidate(); };
        Controls.Add(_v);
        _h.Dock = DockStyle.Bottom; _h.Height = Dpi.S(10);
        _h.ValueChanged += delegate { Invalidate(); };
        Controls.Add(_h);
        _autoScroll.Tick += AutoScrollTick;
        Measure();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _autoScroll.Stop();
            _autoScroll.Dispose();
            if (_feed != null) { _feed.Dispose(); _feed = null; }
        }
        base.Dispose(disposing);
    }

    private void Measure()
    {
        using (var g = CreateGraphics())
        {
            var s = TextRenderer.MeasureText(g, new string('0', 40), Font,
                new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
            _cw = s.Width / 40f;
            _rowH = Math.Max(Dpi.S(Theme.RowHeight), s.Height + Dpi.S(2));
            _textTop = Math.Max(0, (_rowH - Font.Height) / 2);
            _timeFullW = TextRenderer.MeasureText(g, "0000-00-00 00:00:00.000", Theme.MonoSmall,
                new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width;
        }
        Regutter();
    }

    /// <summary>
    /// Splits a message into display lines for wrap mode. The body font is monospace,
    /// so this is character arithmetic rather than per-row measuring — it runs for
    /// every visible row on every paint. Returns (start, length) pairs so search
    /// highlights can still be mapped onto each individual line.
    /// </summary>
    private static List<(int Start, int Len)> WrapSegments(string text, int width, float cw)
    {
        var segs = new List<(int Start, int Len)>();
        if (string.IsNullOrEmpty(text))
        {
            segs.Add((0, 0));
            return segs;
        }
        var perLine = Math.Max(8, (int)(width / Math.Max(1f, cw)));
        var pos = 0;
        while (pos < text.Length && segs.Count < MaxWrapLines)
        {
            var take = Math.Min(perLine, text.Length - pos);
            if (pos + take < text.Length)
            {
                // Prefer breaking after a space so words ("table=orders") stay whole.
                var brk = text.LastIndexOf(' ', pos + take - 1, take);
                if (brk > pos + take / 3)
                {
                    take = brk - pos + 1;
                }
            }
            segs.Add((pos, take));
            pos += take;
        }
        return segs;
    }

    private void Regutter()
    {
        _srcColW = 0;
        if (_file != null && _file.Merged && ShowSource)
        {
            var longest = _file.Srcs.Select(sc => sc.Label.Length).DefaultIfEmpty(0).Max();
            _srcColW = (int)(Math.Min(17, longest + 1) * _cw) + Dpi.S(6);
        }
    }

    private static int GutterX => Dpi.S(ColGutter);
    private static int SpineX => GutterX;
    private static int TimeX => SpineX + Dpi.S(ColSpine);
    private int LevelX => TimeX + TimeColW;
    private int SrcX => LevelX + Dpi.S(ColLevel);
    private int MessageX => SrcX + _srcColW;

    public void Attach(LogDocument f, FilterResult r)
    {
        if (_feed != null) { _feed.Dispose(); _feed = null; }
        if (f != _file)
        {
            _prefixCache.Clear();
            Selected = -1;
            _anchor = -1;
        }
        // Tailing re-attaches the same document with new bytes behind it, so the
        // expanded row's text has to be read again either way.
        ForgetExpansion();
        _file = f; Res = r;
        if (f != null)
        {
            _feed = Indexer.OpenReader(f, LineReaderBuffer);
        }
        Regutter();
        Sync();
        Invalidate();
    }

    private static void IgnoreNonCritical(Exception ex, string context)
    {
        Debug.WriteLine(context + ": " + ex.Message);
    }

    public int VisibleRows { get { return Math.Max(1, (ClientSize.Height - _h.Height) / _rowH); } }
    public int TopRow { get { return _v.Value; } }
    public int RowHeight { get { return _rowH; } }

    private void Sync()
    {
        var cnt = Res == null ? 0 : Res.View.Count;
        var vis = VisibleRows;
        _v.Maximum = Math.Max(0, cnt - 1);
        _v.LargeChange = Math.Max(1, vis);
        _v.Enabled = cnt > vis;
        if (_v.Value > Math.Max(0, cnt - vis))
        {
            _v.Value = Math.Max(0, cnt - vis);
        }
    }

    /// <summary>Pixels available to the message column, scrollbar and padding removed.</summary>
    private int MessageWidth =>
        Math.Max(1, ClientSize.Width - _v.Width - MessageX - Dpi.S(Theme.ColMessageRightPad));

    /// <summary>Sizes the horizontal bar to the longest message among the rows drawn
    /// this frame. Enabled-only (like _v), so it never shifts row layout by
    /// appearing or disappearing — it's just an inert track when nothing overflows.
    ///
    /// Maximum is the message's whole width, not the part that overflows: the bar
    /// derives its own scrollable range as Maximum - LargeChange + 1, so handing it
    /// the overflow left nothing to scroll unless the text ran past *twice* the
    /// column — which is why a plain-text log, whose lines usually overflow by a
    /// little, showed no bar at all while a JSON log showed one.</summary>
    private void SyncHorizontal(int w)
    {
        var avail = Math.Max(1, w - MessageX - Dpi.S(Theme.ColMessageRightPad));
        var need = Wrap ? 0 : (int)(_maxMsgChars * _cw);
        _h.Maximum = Math.Max(0, need - 1);
        _h.LargeChange = Math.Max(1, avail);
        _h.Enabled = need > avail;
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); Sync(); }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); Measure(); Sync(); Invalidate(); }

    /// <summary>Which character column a mouse X lands on, within the message text.</summary>
    public int ColumnAt(int mouseX) { return (int)((mouseX - MessageX + _h.Value) / _cw); }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if ((ModifierKeys & Keys.Shift) == Keys.Shift)
        {
            _h.Value += -Math.Sign(e.Delta) * Dpi.S(60);
        }
        else
        {
            ScrollBy(-Math.Sign(e.Delta) * 3);
        }
    }

    public void ScrollBy(int rows)
    {
        var cnt = Res == null ? 0 : Res.View.Count;
        var max = Math.Max(0, cnt - VisibleRows);
        _v.Value = Math.Max(0, Math.Min(max, _v.Value + rows));
        Invalidate();
    }

    public void ScrollTo(int viewPos, bool center)
    {
        var cnt = Res == null ? 0 : Res.View.Count;
        var max = Math.Max(0, cnt - VisibleRows);
        var t = center ? viewPos - VisibleRows / 2 : viewPos;
        _v.Value = Math.Max(0, Math.Min(max, t));
        Invalidate();
    }

    public void ScrollFraction(float f)
    {
        var cnt = Res == null ? 0 : Res.View.Count;
        var max = Math.Max(0, cnt - VisibleRows);
        _v.Value = Math.Max(0, Math.Min(max, (int)(f * cnt) - VisibleRows / 2));
        Invalidate();
    }

    public int ViewPosOf(int line)
    {
        if (Res == null)
        {
            return -1;
        }
        var v = Res.View;
        int lo = 0, hi = v.Count - 1, best = -1;
        while (lo <= hi)
        {
            var m = (lo + hi) / 2;
            if (v[m] < line) { best = m; lo = m + 1; }
            else if (v[m] > line)
            {
                hi = m - 1;
            }
            else
            {
                return m;
            }
        }
        return best;
    }

    /// <summary>Selects one row, dropping any range that was selected.</summary>
    public void Select(int line, bool center)
    {
        _anchor = -1;
        MoveTo(line, center);
    }

    /// <summary>
    /// Stretches the selection from where it started to <paramref name="line"/> —
    /// Shift+click and Shift+arrow. The first extension pins the row that was
    /// selected as the anchor, so shrinking back toward it works as well.
    /// </summary>
    public void SelectTo(int line, bool center)
    {
        if (_anchor < 0)
        {
            _anchor = Selected < 0 ? line : Selected;
        }
        MoveTo(line, center);
    }

    /// <summary>Drops the selection entirely — a new tab or a closed file has
    /// nothing selected, and the anchor must not outlive the rows it pointed at.</summary>
    public void ClearSelection()
    {
        Selected = -1;
        _anchor = -1;
        ForgetExpansion();
    }

    /// <summary>Selects every row the current filter left showing.</summary>
    public void SelectAll()
    {
        if (Res == null || Res.View.Count == 0)
        {
            return;
        }
        _anchor = Res.View[0];
        MoveTo(Res.View[^1], false);
    }

    private void MoveTo(int line, bool center)
    {
        Selected = line;
        ForgetExpansion();
        var p = ViewPosOf(line);
        if (p >= 0 && (center || p < _v.Value || p >= _v.Value + VisibleRows))
        {
            ScrollTo(p, center);
        }
        Invalidate();
        if (Selected >= 0 && _file is not null)
        {
            var n = SelectionCount;
            AccessibleDescription = n > 1
                ? DisplayFormatter.Number(n) + " entries selected, to line " + DisplayFormatter.Number(Selected + 1)
                : "Line " + DisplayFormatter.Number(Selected + 1) + ": " + TextOf(Selected);
            AccessibilityNotifyClients(AccessibleEvents.Focus, -1);
        }
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── the selected range ─────────────────────────────────────────────
    //
    // Held as two doc line indices rather than a set: the grid selects a
    // contiguous run, which is what Shift+click and Shift+arrow produce, and a
    // run needs no per-row bookkeeping to survive a re-filter. Marks (the M key)
    // remain the way to collect rows that are not next to each other.

    /// <summary>The far end of the selection, or -1 when only one row is selected.</summary>
    private int _anchor = -1;

    /// <summary>View positions of the selected run, or (-1, -1) when nothing is selected.</summary>
    private (int Lo, int Hi) SelectionSpan()
    {
        if (Res == null || Selected < 0)
        {
            return (-1, -1);
        }
        var a = ViewPosOf(Selected);
        if (a < 0)
        {
            return (-1, -1);
        }
        var b = _anchor < 0 ? a : ViewPosOf(_anchor);
        if (b < 0)
        {
            b = a;
        }
        return (Math.Min(a, b), Math.Max(a, b));
    }

    /// <summary>How many rows are selected. 1 for an ordinary click, 0 for none.</summary>
    public int SelectionCount
    {
        get
        {
            var (lo, hi) = SelectionSpan();
            return lo < 0 ? 0 : hi - lo + 1;
        }
    }

    /// <summary>True while more than one row is selected — the expanded row stands
    /// down for it, because expanding one row of forty says nothing useful.</summary>
    public bool HasRange => SelectionCount > 1;

    /// <summary>
    /// Whether the selected row shows its inline expansion. It does not while a run
    /// is selected, and not while the button is still down either: the expansion
    /// makes the row several times taller, and having the layout shift under a drag
    /// in progress would move the rows out from under the pointer.
    /// </summary>
    private bool CanExpand => !HasRange && !_dragging;

    /// <summary>The selected rows as doc line indices, in the order they are shown.</summary>
    public List<int> SelectedLines()
    {
        var (lo, hi) = SelectionSpan();
        var lines = new List<int>(Math.Max(0, hi - lo + 1));
        for (var p = lo; p >= 0 && p <= hi; p++)
        {
            lines.Add(Res.View[p]);
        }
        return lines;
    }

    /// <summary>The selected run as doc line indices. View lines only ever increase,
    /// so a row is selected exactly when its line falls inside this range.</summary>
    private (int First, int Last) SelectedLineRange()
    {
        var (lo, hi) = SelectionSpan();
        return lo < 0 ? (-1, -2) : (Res.View[lo], Res.View[hi]);
    }

    public bool IsSelected(int line)
    {
        var (first, last) = SelectedLineRange();
        return line >= first && line <= last;
    }

    public string TextOf(int line)
    {
        if (_feed == null || _file == null || line < 0 || line >= _file.N)
        {
            return "";
        }
        try { return _feed.Text(line); }
        catch (Exception ex)
        {
            IgnoreNonCritical(ex, "Read line text");
            return "";
        }
    }

    /// <summary>
    /// The human-readable form of a line: JSON-rendered, or skip-stripped of its
    /// timestamp/level prefix, unless Raw. Unlike DisplayText this is never clipped
    /// and an exception keeps its whole stack trace, so it's what Copy/Copy entry
    /// should put on the clipboard — the raw JSON record is what TextOf is for.
    /// </summary>
    public string ReadableTextOf(int line) => Readable(line, true);

    /// <summary>The summary form a one-line grid row can hold: the same message, but
    /// with only the head of any exception.</summary>
    private string GridTextOf(int line) => Readable(line, false);

    private string Readable(int line, bool full)
    {
        var text = TextOf(line);
        if (Raw || _file == null)
        {
            return text;
        }
        if (_file.SrcOf(line).IsJson)
        {
            return full ? JsonReader.RenderFull(text) : JsonReader.RenderLine(text);
        }
        var sk = _file.Skip[line];
        if (sk > 0 && sk < text.Length)
        {
            text = text.Substring(sk);
        }
        return text;
    }

    /// <summary>Beyond this the row is a data dump, not something anyone reads in a
    /// grid; the Entry panel and Ctrl+C still hand over the whole thing.</summary>
    private const int MaxExpandedChars = 64 * 1024;

    /// <summary>
    /// The whole entry laid out for the expanded row: the exception intact, and an
    /// XML payload indented instead of run together into one unreadable line. Cached,
    /// because the row is laid out once and then painted on every hover and scroll.
    /// </summary>
    private string ExpandedTextOf(int line)
    {
        if (_expandText.Line == line)
        {
            return _expandText.Text;
        }
        var text = ReadableTextOf(line);
        if (text.Length > MaxExpandedChars)
        {
            text = string.Concat(text.AsSpan(0, MaxExpandedChars), "\n...");
        }
        _expandText = (line, XmlText.PrettyWithin(text));
        return _expandText.Text;
    }

    private void ForgetExpansion()
    {
        _expandCache = (-2, -1, -1, -1);
        _expandText = (-2, null);
    }

    /// <summary>The text as shown in a collapsed row: GridTextOf, clipped for paint performance.</summary>
    private string DisplayText(int line) => Clip(GridTextOf(line));

    private static string Clip(string text)
    {
        if (text.Length > 4000)
        {
            text = string.Concat(text.AsSpan(0, 4000), " ...");
        }
        return text.Replace('\t', ' ');
    }

    /// <summary>
    /// Splits a dimmed logger/namespace prefix ("Foo.Bar:", "Tcp.645 [x]:") off the
    /// front of a message so the varying tail is the brightest thing in the row.
    /// Cached per line — re-measuring on every paint shows up in live tail.
    /// </summary>
    private int PrefixEndOf(int line, string text)
    {
        if (_prefixCache.TryGetValue(line, out var cached))
        {
            return cached;
        }
        var end = FindPrefixEnd(text);
        if (_prefixCache.Count > 20000)
        {
            _prefixCache.Clear();
        }
        _prefixCache[line] = end;
        return end;
    }

    private static int FindPrefixEnd(string s)
    {
        var limit = Math.Min(s.Length, 80);
        for (var i = 1; i < limit; i++)
        {
            if (s[i] != ':')
            {
                continue;
            }
            if (i + 2 < s.Length && s[i + 1] == '/' && s[i + 2] == '/')
            {
                continue; // http://, etc.
            }
            var ok = true;
            for (var k = 0; k < i; k++)
            {
                var c = s[k];
                if (!(char.IsLetterOrDigit(c) || c is '.' or '_' or '-' or ' ' or '[' or ']'))
                {
                    ok = false;
                    break;
                }
            }
            if (ok)
            {
                var end = i + 1;
                while (end < s.Length && s[end] == ' ')
                {
                    end++;
                }
                return end;
            }
        }
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging)
        {
            ExtendDragTo(e.Y);
            return;   // the hover tint would only fight the selection mid-drag
        }
        var slot = SlotAt(e.Y);
        var line = slot?.Line ?? -1;
        if (line != _hotLine) { _hotLine = line; Invalidate(); }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_dragging)
        {
            return;
        }
        _dragging = false;
        _autoScrollDir = 0;
        _autoScroll.Stop();
        // The row could not expand while the button was down; now it can.
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_hotLine != -1) { _hotLine = -1; Invalidate(); }
        base.OnMouseLeave(e);
    }

    // ── drag to select ─────────────────────────────────────────────────
    //
    // Pressing and sweeping down the rows is the other half of Shift+click: the
    // press picks one row, and crossing into another stretches the run to it.
    // Nothing happens until a row boundary is crossed, so an ordinary click —
    // press and release without moving — still selects exactly one row.

    private bool _dragging;

    /// <summary>-1 while the pointer is held above the grid, +1 below it, 0 inside.</summary>
    private int _autoScrollDir;

    /// <summary>Feeds rows in while the pointer is held past an edge. Without it a
    /// drag could only ever reach what was already on screen.</summary>
    private readonly System.Windows.Forms.Timer _autoScroll = new() { Interval = 60 };

    private void ExtendDragTo(int y)
    {
        var bottom = ClientSize.Height - _h.Height;
        _autoScrollDir = y < 0 ? -1 : y >= bottom ? 1 : 0;
        if (_autoScrollDir != 0)
        {
            if (!_autoScroll.Enabled)
            {
                _autoScroll.Start();
            }
            return;
        }
        _autoScroll.Stop();
        var slot = SlotAt(y);
        if (slot is { Line: >= 0 } s && s.Line != Selected)
        {
            SelectTo(s.Line, false);
        }
    }

    private void AutoScrollTick(object sender, EventArgs e)
    {
        if (!_dragging || _autoScrollDir == 0 || Res == null || Res.View.Count == 0)
        {
            _autoScroll.Stop();
            return;
        }
        var p = ViewPosOf(Selected);
        if (p < 0)
        {
            return;
        }
        var next = Math.Clamp(p + _autoScrollDir, 0, Res.View.Count - 1);
        if (next != p)
        {
            // SelectTo scrolls to whatever it cannot see, so walking one row per
            // tick is all the scrolling this needs.
            SelectTo(Res.View[next], false);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        if (e.Button == MouseButtons.Left)
        {
            foreach (var (rect, fire) in _hitActions)
            {
                if (rect.Contains(e.Location))
                {
                    fire();
                    return;
                }
            }
        }
        var slot = SlotAt(e.Y);
        if (slot is not { Line: >= 0 } s)
        {
            return;
        }
        if (e.Button == MouseButtons.Right)
        {
            // Right-clicking inside a selection keeps it, so the context menu acts
            // on what is selected rather than on the one row under the pointer.
            if (!IsSelected(s.Line))
            {
                Select(s.Line, false);
            }
            return;
        }
        if ((ModifierKeys & Keys.Shift) == Keys.Shift)
        {
            SelectTo(s.Line, false);
        }
        else
        {
            Select(s.Line, false);
        }
        // Either way the button is now down on a row, so sweeping from here
        // stretches the run. Shift+drag carries on from the existing anchor.
        _dragging = true;
    }

    private RowSlot? SlotAt(int y)
    {
        foreach (var s in _slots)
        {
            if (y >= s.Y && y < s.Y + s.Height)
            {
                return s;
            }
        }
        return null;
    }

    [SuppressMessage("Major Code Smell", "S3776", Justification = "Interaction path normalizes raw and parsed text for precise word picks.")]
    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        if (Res == null || Selected < 0)
        {
            return;
        }
        var t = DisplayText(Selected);
        var col = ColumnAt(e.X);
        if (col < 0 || col >= t.Length)
        {
            return;
        }
        int a = col, b = col;
        while (a > 0 && IsWord(t[a - 1]))
        {
            a--;
        }
        while (b < t.Length && IsWord(t[b]))
        {
            b++;
        }
        if (b > a)
        {
            WordPicked?.Invoke(this, new WordPickedEventArgs(t.Substring(a, b - a)));
        }
    }

    private static bool IsWord(char c) { return char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == '-' || c == ':' || c == '/' || c == '='; }

    [SuppressMessage("Major Code Smell", "S3776", Justification = "Highlight extraction handles regex and literal modes with safety guards.")]
    private List<Highlight> Ranges(string text)
    {
        var list = new List<Highlight>();
        if (Query != null)
        {
            if (Query.Rx != null)
            {
                try
                {
                    var guard = 0;
                    foreach (Match m in Query.Rx.Matches(text))
                    {
                        if (m.Length == 0 || guard++ > 200)
                        {
                            break;
                        }
                        list.Add(new Highlight(m.Index, m.Index + m.Length, Theme.MatchText, true));
                    }
                }
                catch (Exception ex) { IgnoreNonCritical(ex, "Regex highlight"); }
            }
            else
            {
                var hay = Query.Cs ? text : text.ToLowerInvariant();
                foreach (var nb in Query.Inc)
                {
                    var needle = Encoding.UTF8.GetString(nb);
                    int p = 0, guard = 0;
                    while (guard++ < 200)
                    {
                        var k = hay.IndexOf(needle, p, StringComparison.Ordinal);
                        if (k < 0)
                        {
                            break;
                        }
                        list.Add(new Highlight(k, k + needle.Length, Theme.MatchText, true));
                        p = k + Math.Max(1, needle.Length);
                    }
                }
            }
            // The values a field filter is showing, so it is plain why a row is here.
            foreach (var field in Query.Fields)
            {
                if (field.Op == FieldOp.IsNot)
                {
                    continue;
                }
                foreach (var v in field.Values)
                {
                    int p = 0, guard = 0;
                    while (v.Length > 0 && guard++ < 50)
                    {
                        var k = text.IndexOf(v, p, StringComparison.OrdinalIgnoreCase);
                        if (k < 0)
                        {
                            break;
                        }
                        list.Add(new Highlight(k, k + v.Length, Theme.MatchText, true));
                        p = k + v.Length;
                    }
                }
            }
        }
        foreach (var r in Rules)
        {
            if (!r.On || r.Pattern.Length == 0)
            {
                continue;
            }
            if (r.IsRegex)
            {
                try
                {
                    var guard = 0;
                    foreach (Match m in Regex.Matches(text, r.Pattern, RegexOptions.IgnoreCase))
                    {
                        if (m.Length == 0 || guard++ > 120)
                        {
                            break;
                        }
                        list.Add(new Highlight(m.Index, m.Index + m.Length, r.C, false));
                    }
                }
                catch (Exception ex) { IgnoreNonCritical(ex, "Rule highlight"); }
            }
            else
            {
                int p = 0, guard = 0;
                while (guard++ < 120)
                {
                    var k = text.IndexOf(r.Pattern, p, StringComparison.OrdinalIgnoreCase);
                    if (k < 0)
                    {
                        break;
                    }
                    list.Add(new Highlight(k, k + r.Pattern.Length, r.C, false));
                    p = k + Math.Max(1, r.Pattern.Length);
                }
            }
        }
        return list;
    }

    private static string DateKeyOf(double ms)
    {
        if (double.IsNaN(ms))
        {
            return null;
        }
        var full = DisplayFormatter.Time(ms);
        return full.Length >= 10 ? full.Substring(0, 10) : null;
    }

    private static string DateBandLabel(double ms)
    {
        var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        try
        {
            return epoch.AddMilliseconds(ms).ToString("ddd d MMM yyyy", CultureInfo.InvariantCulture).ToUpperInvariant();
        }
        catch (ArgumentOutOfRangeException) { return ""; }
    }

    /// <summary>Time column text for a row: the full "yyyy-MM-dd HH:mm:ss.fff" stamp.
    /// There is no shortened form — the column is either open and says all of it, or
    /// collapsed and says none of it, in which case the once-per-day band carries the
    /// date instead.</summary>
    private static string TimeText(double ms) => DisplayFormatter.Time(ms);

    /// <summary>Builds the visible-page layout: date bands where the day changes,
    /// and the selected row taller than the rest to hold its expansion.</summary>
    private List<RowSlot> BuildSlots()
    {
        var slots = new List<RowSlot>();
        if (_file == null || Res == null)
        {
            return slots;
        }
        var y = 0;
        var vh = ClientSize.Height - _h.Height;
        string lastDate = null;
        for (var p = _v.Value; p < Res.View.Count && y < vh; p++)
        {
            var i = Res.View[p];
            var dk = DateKeyOf(_file.Ts[i]);
            if (!ShowTime && dk != null && lastDate != null && dk != lastDate)
            {
                var bandH = Dpi.S(Theme.DateBandHeight);
                slots.Add(new RowSlot(-1, y, bandH, DateBandLabel(_file.Ts[i])));
                y += bandH;
            }
            if (dk != null)
            {
                lastDate = dk;
            }
            if (i == Selected && CanExpand)
            {
                var eh = _rowH + GetExpansionHeight(i);
                slots.Add(new RowSlot(i, y, eh, null));
                y += eh;
            }
            else if (Wrap)
            {
                var text = DisplayText(i);
                var segs = WrapSegments(text, MessageWidth, _cw);
                var wh = Math.Max(1, segs.Count) * _rowH;
                slots.Add(new RowSlot(i, y, wh, null, text, segs));
                y += wh;
            }
            else
            {
                slots.Add(new RowSlot(i, y, _rowH, null));
                y += _rowH;
            }
        }
        return slots;
    }

    private int GetExpansionHeight(int line)
    {
        var mx = MessageX;
        if (_expandCache.Line == line && _expandCache.Width == ClientSize.Width && _expandCache.MsgX == mx)
        {
            return _expandCache.Height;
        }
        using var g = CreateGraphics();
        var h = MeasureExpansion(g, line);
        _expandCache = (line, ClientSize.Width, mx, h);
        return h;
    }

    [SuppressMessage("Major Code Smell", "S3776", Justification = "Paint path is hot code and optimized around low allocation drawing.")]
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.BgData);
        _hitActions.Clear();
        if (_file == null || Res == null || Res.View.Count == 0)
        {
            var msg = _file == null
                ? "Open a log file with Ctrl+O, or drop one here"
                : "No entries match. Press Ctrl+R to clear the query, levels and time range.";
            var area = new Rectangle(0, 0, ClientSize.Width - _v.Width, ClientSize.Height);
            TextRenderer.DrawText(g, msg, Theme.UiBody, area, Theme.TextDim, Theme.TextFlagsCenter);
            return;
        }

        _slots = BuildSlots();
        var w = ClientSize.Width - _v.Width;
        _maxMsgChars = 0;
        // Resolved once per frame rather than per row: it is a binary search each time.
        _paintedSelection = SelectedLineRange();

        foreach (var slot in _slots)
        {
            if (slot.Line < 0)
            {
                DrawDateBand(g, slot, w);
            }
            else if (slot.Line == Selected && CanExpand)
            {
                DrawExpandedRow(g, slot, w);
            }
            else
            {
                DrawRow(g, slot, w);
            }
        }

        SyncHorizontal(w);
    }

    private void DrawDateBand(Graphics g, RowSlot slot, int w)
    {
        using (var b = new SolidBrush(Theme.BgChrome))
        {
            g.FillRectangle(b, 0, slot.Y, w, slot.Height);
        }
        TextUtil.DrawTracked(g, slot.DateLabel, Theme.UiTag, new Point(MessageX, slot.Y + (slot.Height - Theme.UiTag.Height) / 2), Theme.TextDim, 0.6f);
    }

    private void DrawRow(Graphics g, RowSlot slot, int w)
    {
        var i = slot.Line;
        var y = slot.Y;
        var sev = _file.Sev[i];
        var neutral = Theme.IsNeutral(sev);
        var cont = _file.Flag[i] == 1;
        var hover = i == _hotLine;
        var selected = i >= _paintedSelection.First && i <= _paintedSelection.Last;

        if (selected)
        {
            using (var b = new SolidBrush(Theme.AccentGlow))
            {
                g.FillRectangle(b, 0, y, w, slot.Height);
            }
        }
        else if (hover)
        {
            using (var b = new SolidBrush(Theme.BgRowHover))
            {
                g.FillRectangle(b, 0, y, w, slot.Height);
            }
        }

        if (!neutral)
        {
            using (var b = new SolidBrush(Theme.SevColor[sev]))
            {
                g.FillRectangle(b, SpineX, y, Dpi.S(ColSpine), slot.Height);
            }
        }

        var marked = _file.Marks.Contains(i);
        // Height is _rowH, not slot.Height: on a wrapped row the gutter and source
        // label belong beside the first display line, not floating in the middle of
        // however many lines the message happens to need.
        TextRenderer.DrawText(g, (i + 1).ToString(CultureInfo.InvariantCulture), Theme.MonoSmall,
            new Rectangle(0, y, GutterX - Dpi.S(10), _rowH), marked ? Theme.Accent : Theme.TextDim,
            Theme.TextFlagsRight);

        var ts = _file.Ts[i];
        if (ShowTime && !double.IsNaN(ts))
        {
            TextRenderer.DrawText(g, TimeText(ts), Theme.MonoSmall, new Point(TimeX + TimeTextPad, y + TopFor(Theme.MonoSmall)),
                neutral ? Theme.Text3 : Theme.TextBright, Theme.TextFlagsPoint);
        }

        if (sev > 0)
        {
            TextUtil.DrawTracked(g, Levels.Name[sev], Theme.UiTag, new Point(LevelX, y + TopFor(Theme.UiTag)),
                neutral ? Theme.TextDim : Theme.SevColor[sev], 0.4f);
        }

        if (_srcColW > 0)
        {
            var sc = _file.Srcs[_file.Src[i]];
            TextRenderer.DrawText(g, sc.Label, Theme.MonoSmall, new Rectangle(SrcX, y, _srcColW - Dpi.S(4), _rowH),
                Palette.Pick(sc.TintIndex), Theme.TextFlagsEllipsis);
        }

        var text = slot.Text ?? DisplayText(i);
        if (!Wrap && text.Length > _maxMsgChars)
        {
            _maxMsgChars = text.Length;
        }
        var msgColor = cont ? Theme.TextDim : Theme.MsgColor[sev];
        var prefixEnd = cont ? -1 : PrefixEndOf(i, text);
        var msgW = Math.Max(0, w - MessageX - Dpi.S(Theme.ColMessageRightPad));

        if (slot.Wrapped != null)
        {
            DrawWrappedMessage(g, slot, text, msgColor, prefixEnd, msgW);
            return;
        }

        var msgRect = new Rectangle(MessageX, y, msgW, slot.Height);
        var ox = msgRect.X - _h.Value;

        DrawHighlightBackdrops(g, text, msgRect, ox);

        g.SetClip(msgRect);
        DrawMessageRun(g, text, msgColor, prefixEnd, ox, y);
        g.ResetClip();
    }

    /// <summary>Draws one line of message text, dimming a logger/namespace prefix when
    /// the line starts with one.</summary>
    private void DrawMessageRun(Graphics g, string text, Color msgColor, int prefixEnd, int x, int y)
    {
        if (prefixEnd > 0 && prefixEnd < text.Length)
        {
            TextRenderer.DrawText(g, text[..prefixEnd], Font, new Point(x, y + _textTop), Theme.TextPrefix, Theme.TextFlagsPoint);
            var pw = TextRenderer.MeasureText(g, text[..prefixEnd], Font, new Size(short.MaxValue, short.MaxValue), Theme.TextFlags).Width;
            TextRenderer.DrawText(g, text[prefixEnd..], Font, new Point(x + pw, y + _textTop), msgColor, Theme.TextFlagsPoint);
        }
        else
        {
            TextRenderer.DrawText(g, text, Font, new Point(x, y + _textTop), msgColor, Theme.TextFlagsPoint);
        }
    }

    /// <summary>Wrap mode: one display line per segment, each with its own highlight
    /// backdrops. Horizontal scrolling is off here, so every line starts at MessageX.</summary>
    private void DrawWrappedMessage(Graphics g, RowSlot slot, string text, Color msgColor, int prefixEnd, int msgW)
    {
        for (var k = 0; k < slot.Wrapped.Count; k++)
        {
            var (start, len) = slot.Wrapped[k];
            var lineY = slot.Y + k * _rowH;
            var lineText = text.Substring(start, len);
            var lineRect = new Rectangle(MessageX, lineY, msgW, _rowH);

            // Column 0 of the full message sits this far left of the visible line, so
            // a highlight's character offsets still land in the right place.
            DrawHighlightBackdrops(g, text, lineRect, MessageX - (int)(start * _cw));

            g.SetClip(lineRect);
            // The dimmed prefix only exists on the first line, and only if it fits there.
            var linePrefix = k == 0 && prefixEnd > 0 && prefixEnd < len ? prefixEnd : -1;
            DrawMessageRun(g, lineText, msgColor, linePrefix, MessageX, lineY);
            g.ResetClip();
        }
    }

    /// <summary>originX is the message's drawing origin (msgRect.X minus the current
    /// horizontal scroll offset) — where column 0 of the text would land, even when
    /// that's off-screen to the left.</summary>
    private void DrawHighlightBackdrops(Graphics g, string text, Rectangle msgRect, int originX)
    {
        var rs = Ranges(text);
        if (rs.Count == 0)
        {
            return;
        }
        foreach (var r in rs)
        {
            var rx = originX + (int)(r.A * _cw);
            var rw = (int)((r.B - r.A) * _cw);
            if (rx + rw < msgRect.X || rx > msgRect.Right)
            {
                continue;
            }
            var x0 = Math.Max(msgRect.X, rx);
            if (r.Fill)
            {
                using var b = new SolidBrush(Theme.MatchBg);
                g.FillRectangle(b, x0, msgRect.Y + 1, rw, msgRect.Height - 2);
            }
            else
            {
                using (var b = new SolidBrush(Color.FromArgb(46, r.C)))
                {
                    g.FillRectangle(b, x0, msgRect.Y + 1, rw, msgRect.Height - 2);
                }
                using var pn = new Pen(r.C);
                g.DrawLine(pn, x0, msgRect.Bottom - 2, rx + rw, msgRect.Bottom - 2);
            }
        }
    }

    // ── row expansion ──────────────────────────────────────────────────

    [SuppressMessage("Major Code Smell", "S3776", Justification = "Expanded-row layout has several independently sized sections.")]
    private void DrawExpandedRow(Graphics g, RowSlot slot, int w)
    {
        var i = slot.Line;
        var y = slot.Y;
        var sev = _file.Sev[i];
        var neutral = Theme.IsNeutral(sev);

        using (var b = new SolidBrush(Theme.BgRaised))
        {
            g.FillRectangle(b, 0, y, w, slot.Height);
        }
        using (var p = new Pen(Theme.AccentEdge))
        {
            g.DrawLine(p, 0, y, w, y);
            g.DrawLine(p, 0, y + slot.Height - 1, w, y + slot.Height - 1);
        }
        if (!neutral)
        {
            using (var b = new SolidBrush(Theme.SevColor[sev]))
            {
                g.FillRectangle(b, SpineX, y, Dpi.S(ColSpine), slot.Height);
            }
        }

        TextRenderer.DrawText(g, (i + 1).ToString(CultureInfo.InvariantCulture), Theme.MonoSmall,
            new Rectangle(0, y, GutterX - Dpi.S(10), _rowH), Theme.Accent, Theme.TextFlagsRight);
        var ts = _file.Ts[i];
        if (ShowTime && !double.IsNaN(ts))
        {
            TextRenderer.DrawText(g, TimeText(ts), Theme.MonoSmall, new Point(TimeX + TimeTextPad, y + TopFor(Theme.MonoSmall)), Theme.TextBright, Theme.TextFlagsPoint);
        }
        if (sev > 0)
        {
            TextUtil.DrawTracked(g, Levels.Name[sev], Theme.UiTag, new Point(LevelX, y + TopFor(Theme.UiTag)),
                neutral ? Theme.TextDim : Theme.SevColor[sev], 0.4f);
        }

        // row-level icon buttons at the far right of the header line
        var mx = w - Dpi.S(20);
        _markIconRect = new Rectangle(mx, y + (_rowH - Dpi.S(16)) / 2, Dpi.S(16), Dpi.S(16));
        DrawBookmarkIcon(g, _markIconRect, _file.Marks.Contains(i));
        _hitActions.Add((_markIconRect, () => BookmarkToggleRequested?.Invoke(this, EventArgs.Empty)));
        mx -= Dpi.S(22);
        _copyIconRect = new Rectangle(mx, y + (_rowH - Dpi.S(16)) / 2, Dpi.S(16), Dpi.S(16));
        DrawCopyIcon(g, _copyIconRect);
        _hitActions.Add((_copyIconRect, () => CopyEntryRequested?.Invoke(this, EventArgs.Empty)));

        // The full message starts on the header line itself and wraps below it;
        // every line stops short of the row icons so nothing is drawn under them.
        var text = ExpandedTextOf(i);
        var msgColor = _file.Flag[i] == 1 ? Theme.TextDim : Theme.MsgColor[sev];
        var cy = DrawFullMessage(g, text, msgColor, MessageX, y + _textTop, ExpandedTextRight(w));
        cy = DrawFieldGrid(g, i, MessageX, cy, w - Dpi.S(Theme.ColMessageRightPad));
        DrawActionRow(g, i, MessageX, cy, w - Dpi.S(Theme.ColMessageRightPad));
    }

    /// <summary>Right edge of wrapped text in an expanded row — clear of the copy/bookmark icons.</summary>
    private static int ExpandedTextRight(int w) => w - Dpi.S(20) - Dpi.S(22) - Dpi.S(8);

    private int TopFor(Font f) => Math.Max(0, (_rowH - f.Height) / 2);

    private int DrawFullMessage(Graphics g, string text, Color color, int x, int y, int right)
    {
        var width = Math.Max(20, right - x);
        var lines = WrapText(g, text, Font, width);
        var shown = Math.Min(lines.Count, MaxExpandedLines);
        for (var k = 0; k < shown; k++)
        {
            TextRenderer.DrawText(g, lines[k], Font, new Point(x, y), color, Theme.TextFlagsPoint);
            y += _rowH;
        }
        if (lines.Count > MaxExpandedLines)
        {
            TextRenderer.DrawText(g, (lines.Count - MaxExpandedLines) + " more lines — Ctrl+C copies the whole entry, the Entry panel shows it",
                Theme.MonoTiny, new Point(x, y), Theme.TextDim, Theme.TextFlagsPoint);
            y += _rowH;
        }
        return y - _textTop + Dpi.S(6);
    }

    /// <summary>Hard cap on how much of one entry is ever laid out, however wide the
    /// text or narrow the column.</summary>
    private const int MaxMeasuredLines = 400;

    /// <summary>
    /// Breaks text into display lines: on its own line breaks first — an indented XML
    /// payload or a stack trace has already decided where its lines go — and then on
    /// width within each of those.
    /// </summary>
    private static List<string> WrapText(Graphics g, string text, Font font, int width)
    {
        var lines = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            lines.Add("");
            return lines;
        }
        var cw = Math.Max(1f, TextRenderer.MeasureText(g, "0", font, new Size(short.MaxValue, short.MaxValue), Theme.TextFlags).Width);
        var perLine = Math.Max(10, (int)(width / cw));
        foreach (var para in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (lines.Count >= MaxMeasuredLines)
            {
                break;
            }
            WrapParagraph(lines, para.Replace('\t', ' '), perLine);
        }
        return lines;
    }

    private static void WrapParagraph(List<string> lines, string text, int perLine)
    {
        if (text.Length == 0)
        {
            lines.Add("");
            return;
        }
        var pos = 0;
        while (pos < text.Length && lines.Count < MaxMeasuredLines)
        {
            var take = Math.Min(perLine, text.Length - pos);
            if (pos + take < text.Length)
            {
                // Prefer breaking after a space so words ("table=orders") stay whole.
                var brk = text.LastIndexOf(' ', pos + take - 1, take);
                if (brk > pos + take / 3)
                {
                    take = brk - pos + 1;
                }
            }
            lines.Add(text.Substring(pos, take).TrimEnd());
            pos += take;
        }
    }

    [GeneratedRegex("([A-Za-z_][\\w.\\-]{1,32})=(?:\"([^\"]{0,120})\"|'([^']{0,120})'|([^\\s,;)\\]}\"']{1,120}))")]
    private static partial Regex FieldRx { get; }

    private List<(string Key, string Value)> ExtractFields(int line) => ExtractFields(line, 6, keepIds: false);

    /// <summary>
    /// The entry's named fields: a JSON record's properties, or the key=value pairs of
    /// a plain-text line. <paramref name="keepIds"/> keeps the trace, span and event ids
    /// a CLEF record files under @-keys, which the field grid leaves out as plumbing
    /// but which are exactly what "show me this conversation" filters on.
    /// </summary>
    private List<(string Key, string Value)> ExtractFields(int line, int max, bool keepIds)
    {
        var text = TextOf(line);
        var result = new List<(string, string)>();
        if (_file.SrcOf(line).IsJson)
        {
            foreach (var kv in JsonReader.Parse(text))
            {
                if (string.IsNullOrWhiteSpace(kv.Key) || string.IsNullOrEmpty(kv.Value))
                {
                    continue;
                }
                if (JsonReader.IsMetadataKey(kv.Key) && !(keepIds && kv.Key is "@tr" or "@sp" or "@i"))
                {
                    continue;
                }
                result.Add((kv.Key, kv.Value));
                if (result.Count >= max)
                {
                    break;
                }
            }
            return result;
        }
        var sk = _file.Skip[line];
        if (sk > 0 && sk < text.Length)
        {
            text = text.Substring(sk);
        }
        foreach (Match m in FieldRx.Matches(text))
        {
            var v = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Success ? m.Groups[3].Value : m.Groups[4].Value;
            result.Add((m.Groups[1].Value, v));
            if (result.Count >= max)
            {
                break;
            }
        }
        return result;
    }

    /// <summary>The fields of an entry that can be filtered on — scalar values only —
    /// for the "Show only where…" and "Hide where…" menus.</summary>
    public List<(string Key, string Value)> FieldsOf(int line, int max)
    {
        if (_file == null || JsonReader == null || line < 0 || line >= _file.N)
        {
            return new List<(string, string)>();
        }
        var all = ExtractFields(line, max * 2, keepIds: true);
        return all.Where(f => f.Value.Length is > 0 and <= 200 && f.Value[0] is not ('{' or '['))
                  .Take(max).ToList();
    }


    private int DrawFieldGrid(Graphics g, int line, int x, int y, int right)
    {
        var fields = ExtractFields(line);
        if (fields.Count == 0)
        {
            return y;
        }
        var colW = Math.Max(Dpi.S(140), (right - x) / 2);
        var rowH = Dpi.S(18);
        for (var k = 0; k < fields.Count; k++)
        {
            var col = k % 2;
            var row = k / 2;
            var cx = x + col * colW;
            var cy = y + row * rowH;
            var (key, value) = fields[k];
            TextRenderer.DrawText(g, key, Theme.MonoTiny, new Rectangle(cx, cy, Dpi.S(90), rowH), Theme.TextDim, Theme.TextFlags);
            var valueRect = new Rectangle(cx + Dpi.S(94), cy, Math.Max(20, colW - Dpi.S(100)), rowH);
            TextRenderer.DrawText(g, value, Theme.MonoTiny, valueRect, Theme.Text, Theme.TextFlagsEllipsis);
        }
        var rows = (fields.Count + 1) / 2;
        return y + rows * rowH + Dpi.S(6);
    }

    private void DrawActionRow(Graphics g, int line, int x, int y, int right)
    {
        var isJson = _file.SrcOf(line).IsJson;
        var cx = x;
        cx = DrawPill(g, isJson ? "Copy as JSON" : "Copy entry", Theme.Text2, Theme.LineControl, cx, y,
            () => (isJson ? CopyAsJsonRequested : CopyEntryRequested)?.Invoke(this, EventArgs.Empty));

        DrawPill(g, "Next ERROR", Theme.SevColor[5], Theme.SevColor[5], cx, y,
            () => NextErrorRequested?.Invoke(this, EventArgs.Empty));
    }

    private int DrawPill(Graphics g, string label, Color textColor, Color border, int x, int y, Action onClick)
    {
        var pad = Dpi.S(10);
        var textW = TextRenderer.MeasureText(g, label, Theme.UiBody, new Size(short.MaxValue, short.MaxValue), Theme.TextFlags).Width;
        var h = Dpi.S(22);
        var w = textW + pad * 2;
        var r = new Rectangle(x, y, w, h);
        using (var path = TextUtil.RoundedRect(r, Dpi.S(Theme.RadiusChip)))
        using (var pen = new Pen(border))
        {
            g.DrawPath(pen, path);
        }
        TextRenderer.DrawText(g, label, Theme.UiBody, r, textColor, Theme.TextFlagsCenter);
        _hitActions.Add((r, onClick));
        return x + w + Dpi.S(6);
    }

    private static void DrawBookmarkIcon(Graphics g, Rectangle r, bool on)
    {
        var pts = new[]
        {
            new Point(r.Left + 3, r.Top + 2), new Point(r.Right - 3, r.Top + 2),
            new Point(r.Right - 3, r.Bottom - 2), new Point(r.Left + (r.Width / 2), r.Bottom - 6),
            new Point(r.Left + 3, r.Bottom - 2),
        };
        using var pen = new Pen(on ? Theme.Accent : Theme.TextDim, 1.2f);
        if (on)
        {
            using var b = new SolidBrush(Theme.AccentFaint);
            g.FillPolygon(b, pts);
        }
        g.DrawPolygon(pen, pts);
    }

    private static void DrawCopyIcon(Graphics g, Rectangle r)
    {
        using var pen = new Pen(Theme.TextDim, 1.1f);
        g.DrawRectangle(pen, r.X + 4, r.Y + 4, r.Width - 7, r.Height - 7);
        g.DrawLine(pen, r.X + 2, r.Y + 2, r.X + 2, r.Bottom - 6);
        g.DrawLine(pen, r.X + 2, r.Y + 2, r.Right - 6, r.Y + 2);
    }

    [SuppressMessage("Major Code Smell", "S3776", Justification = "Expansion measurement mirrors the paint layout section by section.")]
    private int MeasureExpansion(Graphics g, int line)
    {
        if (_file == null || line < 0 || line >= _file.N)
        {
            return 0;
        }
        // Mirrors DrawExpandedRow: message wraps from the header line down, then the
        // field grid, then the action row. Returned height excludes the header line.
        var w = ClientSize.Width - _v.Width;
        var text = ExpandedTextOf(line);
        var width = Math.Max(20, ExpandedTextRight(w) - MessageX);
        var lines = WrapText(g, text, Font, width);
        var msgLines = Math.Min(lines.Count, MaxExpandedLines) + (lines.Count > MaxExpandedLines ? 1 : 0);
        var msgH = (msgLines - 1) * _rowH + Dpi.S(6);

        var fields = ExtractFields(line);
        var gridH = fields.Count == 0 ? 0 : ((fields.Count + 1) / 2) * Dpi.S(18) + Dpi.S(6);

        var actionH = Dpi.S(22) + Dpi.S(10);
        return msgH + gridH + actionH;
    }
}
