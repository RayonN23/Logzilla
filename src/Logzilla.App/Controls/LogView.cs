using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Globalization;
using System.Linq;
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
            _maxMsgChars = 0;
            Sync();
        }
    }
    private bool _raw;
    /// <summary>Wraps a long message across as many display lines as it needs instead
    /// of running it off the right edge. Mutually exclusive with horizontal scrolling:
    /// there is nothing left to scroll to once everything is on screen. Changing it
    /// re-measures the scroll range, since every row's height changes with it.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Wrap
    {
        get => _wrap;
        set
        {
            if (_wrap == value)
            {
                return;
            }
            _wrap = value;
            _maxMsgChars = 0;
            _expandScroll = 0;
            Sync();
            Invalidate();
        }
    }
    private bool _wrap;
    public int Selected = -1;
    public bool ShowSource = true;
    /// <summary>Shows the date-and-time column, which carries the whole
    /// "yyyy-MM-dd HH:mm:ss.fff" stamp. On by default — a timestamp is the reason
    /// most people open a log. Turning it off collapses the column away entirely and
    /// hands its width to the message; the once-per-day band then keeps the day
    /// visible.</summary>
    public bool ShowTime = true;

    /// <summary>The clock the time column reads in. A file whose zone nobody knows
    /// shows its stamps as written whatever this says, dimmed so it does not pass for
    /// a converted time.</summary>
    public TimeMode TimeMode = TimeMode.AsWritten;
    public TimeZoneInfo LocalZone = TimeZoneInfo.Local;

    public event EventHandler SelectionChanged;
    public event EventHandler CopyEntryRequested;
    public event EventHandler CopyAsJsonRequested;
    public event EventHandler NextErrorRequested;
    public event EventHandler BookmarkToggleRequested;


    /// <summary>A field value the user clicked in an expanded row, to go to the clipboard.</summary>
    public sealed class FieldCopyEventArgs(string key, string value) : EventArgs
    {
        public string Key { get; } = key;
        public string Value { get; } = value;
    }

    /// <summary>Clicking a value in an expanded row's field list copies it.</summary>
    public event EventHandler<FieldCopyEventArgs> CopyFieldRequested;

    /// <summary>
    /// A piece of text as last painted: where it sits, what it says, where its column 0
    /// lands, and — for a value in the field list — which field it is. What a click
    /// lands on is read from these, so it is the word under the pointer as drawn: on a
    /// wrapped line, in the field list, scrolled sideways, or in a JSON row whose raw
    /// text looks nothing like what the grid shows.
    /// </summary>
    private readonly record struct TextRun(Rectangle Bounds, string Text, int OriginX, string FieldKey);

    private readonly List<TextRun> _runs = new();

    private readonly ThinScrollBar _v = new(true);
    private readonly ThinScrollBar _h = new(false);
    private int _maxMsgChars;
    private ILineReader _feed;
    private float _cw = 8f;
    private float _digitW = 7f;   // one MonoSmall digit, for sizing the line-number gutter
    private int _gutterW = Dpi.S(ColGutter);
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
    private (int Line, int Width, List<string> Lines) _expandLines = (-2, -1, null);

    /// <summary>Pixels the page is scrolled past the top of its first row. Only ever
    /// non-zero while an expanded row is taller than the window: the wheel then reads
    /// down through it instead of stepping over it a whole row at a time.</summary>
    private int _expandScroll;
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

    /// <summary>A safety stop on how many lines one wrapped row may take. It was 40, which
    /// cut long messages short with Wrap on; a row taller than the window now scrolls
    /// with the wheel instead, like an expanded one.</summary>
    private const int MaxWrapLines = 5000;

    public LogView()
    {
        AccessibleRole = AccessibleRole.List;
        AccessibleName = "Log entries";
        AccessibleDescription = "One row per log entry. Arrow keys move, Shift with them selects a run, Shift+click or dragging does the same with the mouse, Enter jumps to the next search match, click a row to expand it.";
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.BgData;
        Font = Theme.MonoBody;
        _v.Dock = DockStyle.Right; _v.Width = Dpi.S(10); _v.Name = "GridVerticalScroll";
        _v.ValueChanged += delegate { _expandScroll = 0; Invalidate(); };
        Controls.Add(_v);
        _h.Dock = DockStyle.Bottom; _h.Height = Dpi.S(10); _h.Name = "GridHorizontalScroll";
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
            _digitW = TextRenderer.MeasureText(g, new string('0', 10), Theme.MonoSmall,
                new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width / 10f;
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
        // The fixed gutter holds six digits; past 999,999 lines it widens, or the
        // right-aligned number is clipped on the left and 1,000,000 reads as 000000.
        var digits = _file == null ? 1 : Math.Max(1, _file.N).ToString(CultureInfo.InvariantCulture).Length;
        _gutterW = Math.Max(Dpi.S(ColGutter), (int)Math.Ceiling(digits * _digitW) + Dpi.S(14));

        _srcColW = 0;
        if (_file != null && _file.Merged && ShowSource)
        {
            var longest = _file.Srcs.Select(sc => sc.Label.Length).DefaultIfEmpty(0).Max();
            _srcColW = (int)(Math.Min(17, longest + 1) * _cw) + Dpi.S(6);
        }
    }

    private int GutterX => _gutterW;
    private int SpineX => GutterX;
    private int TimeX => SpineX + Dpi.S(ColSpine);
    private int LevelX => TimeX + TimeColW;
    private int SrcX => LevelX + Dpi.S(ColLevel);
    private int MessageX => SrcX + _srcColW;

    public void Attach(LogDocument f, FilterResult r)
    {
        // A new result for the same file — a severity switched back on, a search changed,
        // the file grew — keeps the reader's place: the line they had picked out stays
        // where it was on screen. The top row used to be kept by its number, which in
        // the new result is some other line entirely, so the place was lost.
        var (anchorLine, rowsAbove) = f != null && f == _file && r != null ? PlaceToKeep() : (-1, 0);
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
        if (r != Res)
        {
            // A new result set has its own longest line; the old one must not keep
            // the horizontal bar alive, nor its absence hide the new one's.
            _maxMsgChars = 0;
        }
        _file = f; Res = r;
        if (f != null)
        {
            _feed = Indexer.OpenReader(f, LineReaderBuffer);
        }
        Regutter();
        Sync();
        if (anchorLine >= 0)
        {
            // The anchor line, or the nearest one before it the new filter still shows.
            var p = Math.Max(0, ViewPosOf(anchorLine));
            _expandScroll = 0;
            SetTop(p - rowsAbove);
        }
        Invalidate();
    }

    /// <summary>
    /// What to hold still across a new result: the selected row while it is on screen,
    /// and how many rows sat above it; otherwise the top row. (-1, 0) when there is no
    /// result yet to hold a place in.
    /// </summary>
    private (int Line, int RowsAbove) PlaceToKeep()
    {
        if (Res == null || Res.View.Count == 0)
        {
            return (-1, 0);
        }
        var top = Math.Clamp(_v.Value, 0, Res.View.Count - 1);
        if (Selected >= 0)
        {
            var p = ViewPosOf(Selected);
            if (p >= top && p < Res.View.Count && Res.View[p] == Selected && HeaderOnScreen(p))
            {
                return (Selected, p - top);
            }
        }
        return (Res.View[top], 0);
    }

    private static void IgnoreNonCritical(Exception ex, string context)
    {
        Debug.WriteLine(context + ": " + ex.Message);
    }

    public int VisibleRows { get { return Math.Max(1, (ClientSize.Height - _h.Height) / _rowH); } }
    public int TopRow { get { return _v.Value; } }
    public int RowHeight { get { return _rowH; } }

    /// <summary>
    /// Sizes the vertical bar to how far the top row can actually go. Measured from the
    /// rows themselves, not as "count minus a page": with Wrap on, an expanded row or
    /// date bands, rows are taller than one line, and counting them as one line hid the
    /// bar — and stopped the wheel — while the last results were still off screen.
    /// </summary>
    private int Sync()
    {
        var top = MaxTop();
        var vis = VisibleRows;
        _v.LargeChange = Math.Max(1, vis);
        _v.Maximum = top + _v.LargeChange - 1;
        _v.Enabled = top > 0;
        if (_v.Value > top)
        {
            _v.Value = top;
        }
        _v.Invalidate();
        return top;
    }

    /// <summary>
    /// Moves the top row, clamped to the range as it stands now. The bar is re-sized
    /// first: it clamps whatever it is given to its own maximum, so handing it a row
    /// past a stale maximum — the last row having just expanded, say — quietly stopped
    /// short and left that row hanging off the bottom of the window.
    /// </summary>
    private void SetTop(int row)
    {
        _v.Value = Math.Max(0, Math.Min(Sync(), row));
        Invalidate();
    }

    /// <summary>The furthest the top row can scroll and still leave the last row on
    /// screen: rows measured back from the end until the window is full.</summary>
    private int MaxTop()
    {
        var cnt = Res == null ? 0 : Res.View.Count;
        if (cnt == 0 || _file == null)
        {
            return 0;
        }
        var vh = ClientSize.Height - _h.Height;
        var used = 0;
        for (var p = cnt - 1; p >= 0; p--)
        {
            used += HeightOf(Res.View[p]);
            if (used > vh)
            {
                return Math.Min(p + 1, cnt - 1);
            }
            if (p > 0 && BandBetween(Res.View[p - 1], Res.View[p]))
            {
                used += Dpi.S(Theme.DateBandHeight);
            }
        }
        return 0;
    }

    /// <summary>How tall line <paramref name="i"/>'s row is drawn, as BuildSlots lays it out.</summary>
    private int HeightOf(int i)
    {
        if (i == Selected && CanExpand)
        {
            return _rowH + GetExpansionHeight(i);
        }
        if (Wrap && _feed != null)
        {
            return Math.Max(1, WrapSegments(DisplayText(i), MessageWidth, _cw).Count) * _rowH;
        }
        return _rowH;
    }

    /// <summary>Whether a date band is drawn between two neighbouring rows.</summary>
    private bool BandBetween(int a, int b)
    {
        if (ShowTime || _file == null)
        {
            return false;
        }
        var ka = DateKeyOf(ShownTime(a));
        var kb = DateKeyOf(ShownTime(b));
        return ka != null && kb != null && ka != kb;
    }

    /// <summary>Whether the row at view position <paramref name="p"/> starts inside the
    /// window with its first line showing — by height, so a wrapped row below a few tall
    /// ones counts as off screen even when few enough rows sit above it.</summary>
    private bool HeaderOnScreen(int p)
    {
        var top = _v.Value;
        if (p < top)
        {
            return false;
        }
        var vh = ClientSize.Height - _h.Height;
        var y = -_expandScroll;
        for (var q = top; q < p; q++)
        {
            y += HeightOf(Res.View[q]);
            if (BandBetween(Res.View[q], Res.View[q + 1]))
            {
                y += Dpi.S(Theme.DateBandHeight);
            }
            if (y >= vh)
            {
                return false;
            }
        }
        var line = Res.View[p];
        var h = line == Selected && CanExpand ? _rowH : HeightOf(line);
        return y >= 0 && y + Math.Min(h, vh) <= vh;
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
        var before = _h.Value;
        _h.Maximum = Math.Max(0, need - 1);
        _h.LargeChange = Math.Max(1, avail);
        _h.Enabled = need > avail;
        if (_h.Value != before)
        {
            // The bar shrank under a scrolled-right view (a search left only short
            // lines, or Wrap went on). This frame was drawn at the old offset, so draw
            // it again rather than leave the text cut off with no bar to scroll back.
            Invalidate();
        }
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); _expandScroll = 0; Sync(); }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); Measure(); ForgetExpansion(); Sync(); Invalidate(); }


    /// <summary>
    /// The movement keys belong to the grid. Without this Windows treats the arrows as
    /// "move focus to the next control", so after clicking a row the first Up or Down
    /// moved focus to the minimap instead of moving the selection.
    /// </summary>
    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Up or Keys.Down or Keys.Left or Keys.Right or
            Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End or Keys.Escape
        || base.IsInputKey(keyData);

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if ((ModifierKeys & Keys.Shift) == Keys.Shift)
        {
            _h.Value += -Math.Sign(e.Delta) * Dpi.S(60);
        }
        else
        {
            WheelBy(-Math.Sign(e.Delta) * 3);
        }
    }

    /// <summary>
    /// Wheel scrolling. Rows step as usual, except through an expanded row taller than
    /// the window: that one scrolls a few lines at a time, so a long XML payload or
    /// stack trace can be read to its end in place.
    /// </summary>
    private void WheelBy(int rows)
    {
        var vh = ClientSize.Height - _h.Height;
        var ex = TallSlot(vh);
        if (rows > 0 && ex is { } s && s.Y + s.Height > vh)
        {
            _expandScroll += Math.Min(rows * _rowH, s.Y + s.Height - vh);
            Invalidate();
            return;
        }
        if (rows < 0 && _expandScroll > 0)
        {
            _expandScroll = Math.Max(0, _expandScroll + rows * _rowH);
            Invalidate();
            return;
        }
        var before = _v.Value;
        ScrollBy(rows);
        // Coming back up onto a row taller than the window lands on its end, so reading
        // back up through it is as continuous as reading down.
        if (rows < 0 && Res != null)
        {
            for (var p = before - 1; p >= _v.Value; p--)
            {
                var h = HeightOf(Res.View[p]);
                if (h > vh)
                {
                    _v.Value = p;
                    _expandScroll = h - vh;
                    Invalidate();
                    break;
                }
            }
        }
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new GridAccessible(this);

    /// <summary>
    /// Which results are fully on screen, as "showing 1 to 20 of 30" (positions in the
    /// filtered view, from the last paint). A screen reader can say where the user is,
    /// and the end-to-end tests can tell the last result really can be scrolled to.
    /// </summary>
    private sealed class GridAccessible(LogView owner) : ControlAccessibleObject(owner)
    {
        public override string Value => owner.ShowingText();
    }

    private string ShowingText()
    {
        var cnt = Res == null ? 0 : Res.View.Count;
        if (cnt == 0 || _slots.Count == 0)
        {
            return "showing 0 of " + cnt.ToString(CultureInfo.InvariantCulture);
        }
        var vh = ClientSize.Height - _h.Height;
        int first = -1, last = -1;
        foreach (var s in _slots)
        {
            if (s.Line < 0)
            {
                continue;
            }
            // A row taller than the window counts when it fills the window, from its
            // own top down (or scrolled within) — not when it hangs off the bottom.
            var fits = s.Height <= vh ? s.Y >= 0 && s.Y + s.Height <= vh : s.Y <= 0;
            if (!fits)
            {
                continue;
            }
            var p = ViewPosOf(s.Line);
            if (first < 0)
            {
                first = p;
            }
            last = p;
        }
        if (first < 0)
        {
            return "showing 0 of " + cnt.ToString(CultureInfo.InvariantCulture);
        }
        return "showing " + (first + 1).ToString(CultureInfo.InvariantCulture) + " to " +
               (last + 1).ToString(CultureInfo.InvariantCulture) + " of " + cnt.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The row the wheel should read down through rather than step past: the
    /// expanded one when it runs off the bottom, or any row taller than the window.</summary>
    private RowSlot? TallSlot(int vh)
    {
        if (ExpandedSlot() is { } ex)
        {
            return ex;
        }
        foreach (var s in _slots)
        {
            if (s.Line >= 0 && s.Height > vh)
            {
                return s;
            }
        }
        return null;
    }

    private RowSlot? ExpandedSlot()
    {
        if (Selected < 0 || !CanExpand)
        {
            return null;
        }
        foreach (var s in _slots)
        {
            if (s.Line == Selected)
            {
                return s;
            }
        }
        return null;
    }

    public void ScrollBy(int rows) => SetTop(_v.Value + rows);

    public void ScrollTo(int viewPos, bool center) => SetTop(center ? viewPos - VisibleRows / 2 : viewPos);

    public void ScrollFraction(float f)
    {
        var cnt = Res == null ? 0 : Res.View.Count;
        SetTop((int)(f * cnt) - VisibleRows / 2);
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
        _expandScroll = 0;
        ForgetExpansion();
        Sync();
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
        _expandScroll = 0;
        _collapsed = false;
        ForgetExpansion();
        var p = ViewPosOf(line);
        if (p >= 0 && (center || !HeaderOnScreen(p)))
        {
            ScrollTo(p, center);
        }
        // The selected row expands, so the bottom of the range moves with it.
        Sync();
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
    private bool CanExpand => !HasRange && !_dragging && !_collapsed;

    /// <summary>The selected row was clicked again (or Esc pressed) to fold it back to
    /// one line. Choosing any other row expands that one as usual.</summary>
    private bool _collapsed;

    /// <summary>True while the selected row is shown expanded.</summary>
    public bool IsExpanded => Selected >= 0 && CanExpand;

    /// <summary>Folds the expanded row back to one line, keeping it selected. False when
    /// there was nothing expanded to fold.</summary>
    public bool Collapse()
    {
        if (!IsExpanded)
        {
            return false;
        }
        SetCollapsed(true);
        return true;
    }

    private void SetCollapsed(bool collapsed)
    {
        _collapsed = collapsed;
        _expandScroll = 0;
        ForgetExpansion();
        Sync();
        Invalidate();
    }

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

    /// <summary>A safety stop for a runaway line (a whole file with no line breaks),
    /// far past any real payload; the Entry panel and Ctrl+C still hand over the
    /// whole thing.</summary>
    private const int MaxExpandedChars = 2 * 1024 * 1024;

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

    /// <summary>
    /// The document's lines were read again from the top — a followed file was truncated
    /// or rotated — so every line number now names a different line. What was cached
    /// about the old ones is dropped, and so is a selection that pointed at one of them.
    /// </summary>
    public void ForgetLines()
    {
        _prefixCache.Clear();
        _maxMsgChars = 0;
        Selected = -1;
        _anchor = -1;
        ForgetExpansion();
    }

    private void ForgetExpansion()
    {
        _expandCache = (-2, -1, -1, -1);
        _expandText = (-2, null);
        _expandLines = (-2, -1, null);
    }

    /// <summary>The expanded row's message broken into display lines. Cached: a long
    /// payload runs to thousands of lines, and the row repaints on every hover.</summary>
    private List<string> ExpandedLinesOf(Graphics g, int line, int width)
    {
        if (_expandLines.Line == line && _expandLines.Width == width)
        {
            return _expandLines.Lines;
        }
        var lines = WrapText(g, ExpandedTextOf(line), Font, width);
        _expandLines = (line, width, lines);
        return lines;
    }

    /// <summary>The text as shown in a collapsed row: GridTextOf, whole.</summary>
    private string DisplayText(int line) => Clip(GridTextOf(line));

    /// <summary>A safety stop for a runaway line (a whole file with no line breaks), far
    /// past any real message. It was 4,000 characters, which cut genuine long messages
    /// short with "..." in the row.</summary>
    private const int MaxRowChars = 100_000;

    private static string Clip(string text)
    {
        if (text.Length > MaxRowChars)
        {
            text = string.Concat(text.AsSpan(0, MaxRowChars), " ...");
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
        Sync();
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
        var shift = (ModifierKeys & Keys.Shift) == Keys.Shift;
        if (!shift && s.Line == Selected && !HasRange)
        {
            // Clicking the selected row again folds it away, and once more opens it —
            // every click, however quickly it follows the last. A quick second click used
            // to count as a double-click, which picked a word into the Find box instead:
            // the row stayed open and the view re-filtered on whatever word was under it.
            SetCollapsed(!_collapsed);
            return;
        }
        if (shift)
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

    /// <summary>
    /// The word under <paramref name="p"/> as the grid last drew it, or "" if there is
    /// none. A value in an expanded row's field list comes back whole, with its key in
    /// <paramref name="fieldKey"/>; anywhere else <paramref name="fieldKey"/> is null.
    /// </summary>
    public string PickAt(Point p, out string fieldKey)
    {
        fieldKey = null;
        foreach (var run in _runs)
        {
            if (!run.Bounds.Contains(p))
            {
                continue;
            }
            if (run.FieldKey != null)
            {
                fieldKey = run.FieldKey;
                return run.Text;
            }
            var t = run.Text;
            var col = (int)((p.X - run.OriginX) / _cw);
            if (col < 0 || col >= t.Length || !IsWord(t[col]))
            {
                return "";
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
            return t.Substring(a, b - a);
        }
        return "";
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
                AddRegexMatches(list, text, Query.Rx.ToString(), Query.Rx.Options, Theme.MatchText, true, 200);
            }
            else
            {
                var hay = Query.Cs ? text : text.ToLowerInvariant();
                foreach (var needle in Query.IncludeTerms)
                {
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
                AddRegexMatches(list, text, r.Pattern, RegexOptions.IgnoreCase, r.C, false, 120);
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

    /// <summary>Longest one highlight pattern may run on one row. Highlighting runs on
    /// the UI thread for every row on screen, so a pattern that backtracks badly must
    /// not be able to freeze the window.</summary>
    private static readonly TimeSpan PaintRegexTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>Highlight patterns, compiled once rather than on every row of every paint.
    /// A null entry is a pattern that is invalid or proved too slow: it is skipped from
    /// then on instead of throwing again for each row.</summary>
    private readonly Dictionary<(string Pattern, RegexOptions Options), Regex> _paintRegex = new();

    private void AddRegexMatches(List<Highlight> list, string text, string pattern, RegexOptions options, Color color, bool isMatch, int max)
    {
        var key = (pattern, options);
        if (!_paintRegex.TryGetValue(key, out var rx))
        {
            try { rx = new Regex(pattern, options, PaintRegexTimeout); }
            catch (ArgumentException) { rx = null; }
            if (_paintRegex.Count > 64)
            {
                _paintRegex.Clear();
            }
            _paintRegex[key] = rx;
        }
        if (rx == null)
        {
            return;
        }
        try
        {
            var guard = 0;
            for (var m = rx.Match(text); m.Success; m = m.NextMatch())
            {
                if (m.Length == 0 || guard++ > max)
                {
                    break;
                }
                list.Add(new Highlight(m.Index, m.Index + m.Length, color, isMatch));
            }
        }
        catch (RegexMatchTimeoutException)
        {
            _paintRegex[key] = null;
        }
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

    /// <summary>Line <paramref name="i"/>'s stored time in the clock the column reads in.</summary>
    private double ShownTime(int i) => TimeZones.Display(_file.Ts[i], _file.SrcOf(i), TimeMode, LocalZone);

    /// <summary>False only when a conversion was asked for and this line's file could not
    /// give one, because nobody knows what zone its stamps are in.</summary>
    private bool Converted(int i) => TimeMode == TimeMode.AsWritten || TimeZones.IsKnown(_file.SrcOf(i));

    /// <summary>Builds the visible-page layout: date bands where the day changes,
    /// and the selected row taller than the rest to hold its expansion.</summary>
    private List<RowSlot> BuildSlots()
    {
        var slots = new List<RowSlot>();
        if (_file == null || Res == null)
        {
            return slots;
        }
        var y = -_expandScroll;
        var vh = ClientSize.Height - _h.Height;
        string lastDate = null;
        for (var p = _v.Value; p < Res.View.Count && y < vh; p++)
        {
            var i = Res.View[p];
            var dk = DateKeyOf(ShownTime(i));
            if (!ShowTime && dk != null && lastDate != null && dk != lastDate)
            {
                var bandH = Dpi.S(Theme.DateBandHeight);
                slots.Add(new RowSlot(-1, y, bandH, DateBandLabel(ShownTime(i))));
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
        _runs.Clear();
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
        // _maxMsgChars only grows while the same results are shown: sized from the
        // rows on screen alone, the bar came and went as you scrolled.
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

        var ts = ShownTime(i);
        if (ShowTime && !double.IsNaN(ts))
        {
            TextRenderer.DrawText(g, TimeText(ts), Theme.MonoSmall, new Point(TimeX + TimeTextPad, y + TopFor(Theme.MonoSmall)),
                neutral || !Converted(i) ? Theme.Text3 : Theme.TextBright, Theme.TextFlagsPoint);
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
        _runs.Add(new TextRun(msgRect, text, ox, null));

        DrawHighlightBackdrops(g, text, msgRect, ox);

        g.SetClip(msgRect);
        DrawMessageRun(g, VisiblePart(text, msgRect.Right - ox, prefixEnd), msgColor, prefixEnd, ox, y);
        g.ResetClip();
    }

    /// <summary>
    /// The start of <paramref name="text"/> up to the column that reaches
    /// <paramref name="width"/> pixels from its origin, plus a little slack. The rest
    /// would be clipped anyway, but GDI still lays out every character it is handed:
    /// a row holding a 100,000-character payload cost that on every paint and hover.
    /// The cut never falls inside the dimmed prefix, so the row is coloured the same.
    /// </summary>
    private string VisiblePart(string text, int width, int prefixEnd)
    {
        var keep = Math.Max((int)Math.Ceiling(Math.Max(0, width) / Math.Max(1f, _cw)) + 8, prefixEnd + 1);
        return text.Length > keep ? text[..keep] : text;
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
            _runs.Add(new TextRun(lineRect, lineText, MessageX, null));

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
        var ts = ShownTime(i);
        if (ShowTime && !double.IsNaN(ts))
        {
            TextRenderer.DrawText(g, TimeText(ts), Theme.MonoSmall, new Point(TimeX + TimeTextPad, y + TopFor(Theme.MonoSmall)), Converted(i) ? Theme.TextBright : Theme.Text3, Theme.TextFlagsPoint);
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
        var msgColor = _file.Flag[i] == 1 ? Theme.TextDim : Theme.MsgColor[sev];
        var cy = DrawFullMessage(g, i, msgColor, MessageX, y + _textTop, ExpandedTextRight(w));
        cy = DrawFieldGrid(g, i, MessageX, cy, w - Dpi.S(Theme.ColMessageRightPad));
        DrawActionRow(g, i, MessageX, cy, w - Dpi.S(Theme.ColMessageRightPad));
    }

    /// <summary>Right edge of wrapped text in an expanded row — clear of the copy/bookmark icons.</summary>
    private static int ExpandedTextRight(int w) => w - Dpi.S(20) - Dpi.S(22) - Dpi.S(8);

    private int TopFor(Font f) => Math.Max(0, (_rowH - f.Height) / 2);

    /// <summary>The whole message, however long. Only the lines inside the window are
    /// drawn; the rest just take up their height.</summary>
    private int DrawFullMessage(Graphics g, int line, Color color, int x, int y, int right)
    {
        var width = Math.Max(20, right - x);
        var lines = ExpandedLinesOf(g, line, width);
        var bottom = ClientSize.Height;
        var first = y < 0 ? Math.Min(lines.Count, -y / _rowH) : 0;
        y += first * _rowH;
        for (var k = first; k < lines.Count; k++)
        {
            if (y < bottom)
            {
                // Highlights and search matches carry on into the expanded row: they used
                // to vanish the moment a highlighted line was clicked open.
                DrawHighlightBackdrops(g, lines[k], new Rectangle(x, y, Math.Max(1, right - x), _rowH), x);
                TextRenderer.DrawText(g, lines[k], Font, new Point(x, y), color, Theme.TextFlagsPoint);
                _runs.Add(new TextRun(new Rectangle(x, y, Math.Max(1, right - x), _rowH), lines[k], x, null));
            }
            y += _rowH;
        }
        if (lines.Count >= MaxMeasuredLines)
        {
            if (y < bottom)
            {
                TextRenderer.DrawText(g, "The entry continues — Ctrl+C copies all of it, the Entry panel shows it",
                    Theme.MonoTiny, new Point(x, y), Theme.TextDim, Theme.TextFlagsPoint);
            }
            y += _rowH;
        }
        return y - _textTop + Dpi.S(6);
    }

    /// <summary>A safety stop on how much of one entry is ever laid out; no real
    /// payload comes near it.</summary>
    private const int MaxMeasuredLines = 200_000;

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

    /// <summary>Fields shown in an expanded row. Twelve, not six: with six, a record's
    /// ThreadId was usually the one left off. The ids people follow come first.</summary>
    private List<(string Key, string Value)> ExtractFields(int line) =>
        ExtractFields(line, 24, keepIds: false)
            .OrderBy(f => FieldNames.FollowRank(f.Key))
            .Take(12)
            .ToList();

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


    /// <summary>One field in an expanded row's list: where it sits and its value broken
    /// into the lines it needs.</summary>
    private readonly record struct FieldCell(string Key, string Value, Rectangle Bounds, List<string> Lines);

    /// <summary>
    /// Lays out the field list two to a row. A value too long for its column wraps onto
    /// as many lines as it needs and its row grows to fit — it used to be cut off with
    /// "..." ("Soti.MobiControl.WindowsModern.Ser..."). Drawing and measuring both use
    /// this, so the row is always exactly as tall as what is drawn in it.
    /// </summary>
    private (List<FieldCell> Cells, int Height) LayoutFields(Graphics g, int line, int x, int y, int right)
    {
        var cells = new List<FieldCell>();
        var fields = ExtractFields(line);
        if (fields.Count == 0)
        {
            return (cells, 0);
        }
        var colW = Math.Max(Dpi.S(140), (right - x) / 2);
        var lineH = Dpi.S(18);
        var valueW = Math.Max(20, colW - Dpi.S(100));
        var cw = Math.Max(1f, TextRenderer.MeasureText(g, "0000000000", Theme.MonoTiny, new Size(short.MaxValue, short.MaxValue), Theme.TextFlags).Width / 10f);
        var perLine = Math.Max(4, (int)(valueW / cw));
        var top = y;
        for (var k = 0; k < fields.Count; k += 2)
        {
            var pair = new List<(string Key, string Value, List<string> Lines)>();
            for (var c = k; c < Math.Min(k + 2, fields.Count); c++)
            {
                var lines = new List<string>();
                WrapParagraph(lines, fields[c].Value.Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' '), perLine);
                pair.Add((fields[c].Key, fields[c].Value, lines));
            }
            var rowLines = pair.Max(f => Math.Max(1, f.Lines.Count));
            for (var c = 0; c < pair.Count; c++)
            {
                var bounds = new Rectangle(x + c * colW, top, colW, rowLines * lineH);
                cells.Add(new FieldCell(pair[c].Key, pair[c].Value, bounds, pair[c].Lines));
            }
            top += rowLines * lineH;
        }
        return (cells, top - y + Dpi.S(6));
    }

    private int DrawFieldGrid(Graphics g, int line, int x, int y, int right)
    {
        var (cells, height) = LayoutFields(g, line, x, y, right);
        var lineH = Dpi.S(18);
        foreach (var cell in cells)
        {
            var (key, value) = (cell.Key, cell.Value);
            var b = cell.Bounds;
            _runs.Add(new TextRun(b, value, b.X, key));
            _hitActions.Add((b, () => CopyFieldRequested?.Invoke(this, new FieldCopyEventArgs(key, value))));
            TextRenderer.DrawText(g, FieldNames.Display(key), Theme.MonoTiny, new Rectangle(b.X, b.Y, Dpi.S(90), lineH), Theme.TextDim, Theme.TextFlags);
            var vx = b.X + Dpi.S(94);
            var vw = Math.Max(20, b.Width - Dpi.S(100));
            for (var k = 0; k < cell.Lines.Count; k++)
            {
                TextRenderer.DrawText(g, cell.Lines[k], Theme.MonoTiny, new Rectangle(vx, b.Y + k * lineH, vw + Dpi.S(4), lineH), Theme.Text, Theme.TextFlags);
            }
        }
        return y + height;
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
        var width = Math.Max(20, ExpandedTextRight(w) - MessageX);
        var lines = ExpandedLinesOf(g, line, width);
        var msgLines = lines.Count + (lines.Count >= MaxMeasuredLines ? 1 : 0);
        var msgH = (msgLines - 1) * _rowH + Dpi.S(6);

        var gridH = LayoutFields(g, line, MessageX, 0, w - Dpi.S(Theme.ColMessageRightPad)).Height;

        var actionH = Dpi.S(22) + Dpi.S(10);
        return msgH + gridH + actionH;
    }
}
