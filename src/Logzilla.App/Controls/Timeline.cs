using System;
using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Windows.Forms;
using Logzilla.App.Infrastructure;
using Logzilla.Core.Common;
using Logzilla.Core.Logs;
using Logzilla.Core.Query;

namespace Logzilla.App.Controls;

/// <summary>
/// 96px total: a label strip, the stacked-severity chart, and an axis. Bars are
/// stacked bottom-to-top INFO -> WARN -> ERROR so the timeline shows *where*
/// trouble clusters, not just how busy the file was.
/// </summary>
internal sealed class Timeline : Control
{
    public LogDocument File;
    public FilterResult Res;
    public LogView View;
    public double T0 = double.NaN, T1 = double.NaN;

    /// <summary>The clock the labels read in. The chart itself is laid out in stored
    /// time, so the bars do not move when this changes; only what they are called.</summary>
    public TimeMode TimeMode = TimeMode.AsWritten;
    public TimeZoneInfo LocalZone = TimeZoneInfo.Local;
    public event EventHandler<RangeArgs> RangeSelected;
    public event EventHandler<SeekArgs> Seek;

    public sealed class RangeArgs : EventArgs { public double A, B; public RangeArgs(double a, double b) { A = a; B = b; } }
    public sealed class SeekArgs : EventArgs { public double T; public SeekArgs(double t) { T = t; } }

    private int _dragX = -1, _curX = -1;
    private bool _resetPending;
    private Rectangle _resetZoomRect;
    private bool _resetHot;

    public Timeline()
    {
        AccessibleRole = AccessibleRole.Chart;
        AccessibleName = "Timeline";
        AccessibleDescription = "Entries per time bucket, stacked by severity. Drag to narrow the range, click to seek, double-click to reset.";
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.BgChromeLow;
        Height = Dpi.S(Theme.TimelineHeight);
    }

    // 96px: 6 top pad, 16 label strip, 6 gap, 48 chart, 16 axis, 4 bottom pad.
    private const int Inset = 12;
    private const int StripTop = 6;
    private static int ChartTop => Dpi.S(StripTop + Theme.TimelineLabelHeight + 6);
    private static int ChartBottom => ChartTop + Dpi.S(Theme.TimelineChartHeight);

    private double XToT(int x)
    {
        if (Res == null || double.IsNaN(Res.Lo) || Res.Hi <= Res.Lo)
        {
            return double.NaN;
        }
        var f = (double)x / Math.Max(1, ClientSize.Width);
        return Res.Lo + f * (Res.Hi - Res.Lo);
    }

    private int ToX(double t)
    {
        if (Res == null || double.IsNaN(Res.Lo) || Res.Hi <= Res.Lo)
        {
            return 0;
        }
        return (int)((t - Res.Lo) / (Res.Hi - Res.Lo) * ClientSize.Width);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (_resetZoomRect.Contains(e.Location))
        {
            return;
        }
        _dragX = e.X;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var wasHot = _resetHot;
        _resetHot = _resetZoomRect.Contains(e.Location);
        _curX = e.X;
        Cursor = _resetHot ? Cursors.Hand : Cursors.Default;
        if (_resetHot != wasHot)
        {
            Invalidate();
        }
        else
        {
            Invalidate(new Rectangle(0, ChartTop, ClientSize.Width, ChartBottom - ChartTop + Dpi.S(20)));
        }
    }

    protected override void OnMouseLeave(EventArgs e) { _curX = -1; Invalidate(); base.OnMouseLeave(e); }

    /// <summary>
    /// Double-clicking the chart clears the time range, the way it always has — the
    /// "Reset zoom" link is the discoverable form of the same thing, not a
    /// replacement for it.
    /// </summary>
    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        _resetPending = true;
        _dragX = -1;
        ResetRange();
        Invalidate();
    }

    /// <summary>Clears the selected time range — a NaN range means "no filter".</summary>
    private void ResetRange()
    {
        RangeSelected?.Invoke(this, new RangeArgs(double.NaN, double.NaN));
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_resetZoomRect.Contains(e.Location))
        {
            _dragX = -1;
            ResetRange();
            return;
        }
        // OnMouseDoubleClick runs just before the mouse-up that completes it, so the
        // second click must not also be read as a click-to-seek.
        if (_resetPending)
        {
            _resetPending = false;
            _dragX = -1;
            return;
        }
        if (_dragX < 0)
        {
            return;
        }
        int a = Math.Min(_dragX, e.X), b = Math.Max(_dragX, e.X);
        var start = _dragX; _dragX = -1;
        if (b - a > 4)
        {
            double ta = XToT(a), tb = XToT(b);
            if (!double.IsNaN(ta) && RangeSelected != null)
            {
                RangeSelected(this, new RangeArgs(ta, tb));
            }
        }
        else
        {
            var t = XToT(start);
            if (!double.IsNaN(t) && Seek != null)
            {
                Seek(this, new SeekArgs(t));
            }
        }
        Invalidate();
    }

    [SuppressMessage("Major Code Smell", "S3776", Justification = "Paint path draws several stacked layers and is kept flat for clarity.")]
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.BgChromeLow);
        int w = ClientSize.Width, h = ClientSize.Height;
        int chartTop = ChartTop, baseY = ChartBottom;

        DrawLabelStrip(g, w);

        if (Res == null || Res.Bucket == null || Res.View.Count == 0)
        {
            var tmsg = (Res != null && Res.View.Count == 0)
                ? "No entries match the current filters"
                : "Timeline appears once entries carry timestamps";
            TextRenderer.DrawText(g, tmsg, Theme.UiBody,
                new Rectangle(0, chartTop, w, h - chartTop), Theme.TextDim, Theme.TextFlagsCenter);
            return;
        }

        using (var p = new Pen(Theme.Line))
        {
            g.DrawLine(p, 0, baseY, w, baseY);
        }

        var nb = FilterResult.Buckets;
        var peak = 1;
        for (var i = 0; i < nb; i++)
        {
            if (Res.Bucket[i] > peak)
            {
                peak = Res.Bucket[i];
            }
        }
        var bw = (float)w / nb;
        var plotH = baseY - chartTop - 4;

        using (var bAll = new SolidBrush(Theme.HistInfo))
        using (var bW = new SolidBrush(Theme.SevColor[4]))
        using (var bE = new SolidBrush(Theme.SevColor[5]))
        {
            for (var i = 0; i < nb; i++)
            {
                if (Res.Bucket[i] == 0)
                {
                    continue;
                }
                var x = i * bw;
                var hAll = Math.Max(1f, (float)Res.Bucket[i] / peak * plotH);
                var hE = (float)Res.BucketErr[i] / peak * plotH;
                var hW = (float)Res.BucketWarn[i] / peak * plotH;
                g.FillRectangle(bAll, x, baseY - hAll, Math.Max(1f, bw - 1f), hAll);
                if (hW > 0)
                {
                    g.FillRectangle(bW, x, baseY - hW - hE, Math.Max(1f, bw - 1f), Math.Max(1f, hW));
                }
                if (hE > 0)
                {
                    g.FillRectangle(bE, x, baseY - hE, Math.Max(1f, bw - 1f), Math.Max(1f, hE));
                }
            }
        }

        DrawViewport(g, baseY, chartTop);

        if (!double.IsNaN(T0) && !double.IsNaN(Res.Lo) && Res.Hi > Res.Lo)
        {
            var x0 = ToX(T0);
            var x1 = ToX(T1);
            using (var b = new SolidBrush(Color.FromArgb(34, Theme.SevColor[4])))
            {
                g.FillRectangle(b, x0, chartTop, Math.Max(1, x1 - x0), baseY - chartTop);
            }
            using (var p = new Pen(Theme.SevColor[4]))
            {
                g.DrawLine(p, x0, chartTop, x0, baseY); g.DrawLine(p, x1, chartTop, x1, baseY);
            }
        }

        if (_dragX >= 0 && _curX >= 0)
        {
            int a = Math.Min(_dragX, _curX), b = Math.Max(_dragX, _curX);
            using (var br = new SolidBrush(Color.FromArgb(50, Theme.SevColor[4])))
            {
                g.FillRectangle(br, a, chartTop, b - a, baseY - chartTop);
            }
        }

        DrawAxis(g, w, baseY);

        if (_curX >= 0 && !double.IsNaN(Res.Lo) && _dragX < 0)
        {
            DrawHoverCard(g, w, chartTop, baseY, bw, nb);
        }
    }

    private void DrawLabelStrip(Graphics g, int w)
    {
        const float track = 0.9f;
        var pad = Dpi.S(Inset);
        var strip = new Rectangle(0, Dpi.S(StripTop), w, Dpi.S(Theme.TimelineLabelHeight));
        var headY = strip.Y + (strip.Height - Theme.UiHead.Height) / 2;
        TextUtil.DrawTracked(g, "VOLUME OVER TIME", Theme.UiHead, new Point(pad, headY), Theme.TextDim, track);
        var headW = TextUtil.MeasureTracked(g, "VOLUME OVER TIME", Theme.UiHead, track);
        var y = strip.Y;

        if (Res != null && Res.Bucket != null && Res.View.Count > 0)
        {
            var nb = FilterResult.Buckets;
            var peak = 1;
            for (var i = 0; i < nb; i++)
            {
                if (Res.Bucket[i] > peak)
                {
                    peak = Res.Bucket[i];
                }
            }
            var span = (!double.IsNaN(Res.Lo) && Res.Hi > Res.Lo) ? (Res.Hi - Res.Lo) / nb : 0;
            var info = DisplayFormatter.Duration(span) + " / bucket  ·  peak " + DisplayFormatter.Number(peak);
            TextRenderer.DrawText(g, info, Theme.MonoTiny, new Rectangle(pad + headW + Dpi.S(12), y, w, strip.Height), Theme.Text3, Theme.TextFlags);
        }

        var resetText = "Reset zoom";
        var resetOn = !double.IsNaN(T0);
        var resetW = TextRenderer.MeasureText(g, resetText, Theme.MonoTiny, new Size(short.MaxValue, short.MaxValue), Theme.TextFlags).Width;
        var resetX = w - pad - resetW;
        _resetZoomRect = new Rectangle(resetX - 4, 0, resetW + 8, strip.Bottom + Dpi.S(2));
        if (resetOn)
        {
            TextRenderer.DrawText(g, resetText, Theme.MonoTiny, new Rectangle(resetX, y, resetW + 2, strip.Height),
                _resetHot ? Theme.Accent : Theme.Text3, Theme.TextFlags);
        }

        if (Res != null && !double.IsNaN(Res.Lo))
        {
            var rangeText = DisplayFormatter.Time(Shown(Res.Lo)) + "  –  " + DisplayFormatter.Time(Shown(Res.Hi));
            var rangeW = TextRenderer.MeasureText(g, rangeText, Theme.MonoTiny, new Size(short.MaxValue, short.MaxValue), Theme.TextFlags).Width;
            var rangeX = (resetOn ? resetX - Dpi.S(16) : w - pad) - rangeW;
            TextRenderer.DrawText(g, rangeText, Theme.MonoTiny, new Rectangle(rangeX, y, rangeW + 2, strip.Height), Theme.TextDim, Theme.TextFlags);
        }
    }

    private void DrawViewport(Graphics g, int baseY, int chartTop)
    {
        if (View == null || File == null || Res == null || Res.View.Count == 0 || double.IsNaN(Res.Lo) || Res.Hi <= Res.Lo)
        {
            return;
        }
        var top = Math.Clamp(View.TopRow, 0, Res.View.Count - 1);
        var bottom = Math.Clamp(View.TopRow + Math.Max(1, View.VisibleRows) - 1, 0, Res.View.Count - 1);
        var t0 = File.Ts[Res.View[top]];
        var t1 = File.Ts[Res.View[bottom]];
        if (double.IsNaN(t0))
        {
            t0 = Res.Lo;
        }
        if (double.IsNaN(t1))
        {
            t1 = Res.Hi;
        }
        var x0 = ToX(t0);
        var x1 = Math.Max(x0 + 2, ToX(t1));
        using (var b = new SolidBrush(Theme.AccentFaint))
        {
            g.FillRectangle(b, x0, chartTop, x1 - x0, baseY - chartTop);
        }
        using (var p = new Pen(Theme.AccentEdge))
        {
            g.DrawLine(p, x0, chartTop, x0, baseY);
            g.DrawLine(p, x1, chartTop, x1, baseY);
        }
    }

    /// <summary>A stored time in the clock the labels read in, by the document's reference file.</summary>
    private double Shown(double ms) => TimeZones.Display(ms, TimeZones.Reference(File), TimeMode, LocalZone);

    private static string ShortAxisLabel(double ms)
    {
        var full = DisplayFormatter.Time(ms);
        // "yyyy-MM-dd HH:mm:ss.fff" -> "HH:mm:ss"; the date rarely earns its
        // width on an axis this thin, and the label strip already shows the
        // full first/last timestamps.
        return full.Length >= 19 ? full.Substring(11, 8) : full;
    }

    private void DrawAxis(Graphics g, int w, int baseY)
    {
        if (Res == null || double.IsNaN(Res.Lo) || Res.Hi <= Res.Lo)
        {
            return;
        }
        var y = baseY + Dpi.S(3);
        var sampleW = Math.Max(1, TextRenderer.MeasureText(g, "00:00:00", Theme.MonoTiny,
            new Size(short.MaxValue, short.MaxValue), Theme.TextFlagsPoint).Width);
        var marks = Math.Clamp(w / (sampleW + Dpi.S(28)), 2, 7);
        for (var i = 0; i <= marks; i++)
        {
            var t = Res.Lo + (Res.Hi - Res.Lo) * i / marks;
            var x = (int)((double)i / marks * w);
            var label = ShortAxisLabel(Shown(t));
            var lw = TextRenderer.MeasureText(g, label, Theme.MonoTiny, new Size(short.MaxValue, short.MaxValue), Theme.TextFlagsPoint).Width;
            var lx = Math.Clamp(x - lw / 2, Dpi.S(Inset), w - lw - Dpi.S(Inset));
            TextRenderer.DrawText(g, label, Theme.MonoTiny, new Point(lx, y), Theme.TextDim, Theme.TextFlagsPoint);
        }
    }

    private void DrawHoverCard(Graphics g, int w, int chartTop, int baseY, float bw, int nb)
    {
        var bi = Math.Max(0, Math.Min(nb - 1, (int)(_curX / bw)));
        var timeLabel = DisplayFormatter.Time(Shown(XToT(_curX)));
        var entries = DisplayFormatter.Number(Res.Bucket[bi]) + " entries";
        var errText = DisplayFormatter.Number(Res.BucketErr[bi]) + " err";

        var lines = new[] { timeLabel, entries };
        var w1 = 0;
        foreach (var l in lines)
        {
            w1 = Math.Max(w1, TextRenderer.MeasureText(g, l, Theme.MonoTiny, new Size(short.MaxValue, short.MaxValue), Theme.TextFlags).Width);
        }
        var errW = TextRenderer.MeasureText(g, errText, Theme.MonoTiny, new Size(short.MaxValue, short.MaxValue), Theme.TextFlags).Width;
        w1 = Math.Max(w1, errW + 12);

        var cardW = w1 + 16;
        var cardH = Dpi.S(46);
        var tx = Math.Min(w - cardW - 4, _curX + 8);
        var ty = chartTop + 4;

        using (var b = new SolidBrush(Theme.BgRaised))
        {
            g.FillRectangle(b, tx, ty, cardW, cardH);
        }
        using (var p = new Pen(Theme.LineControlStrong))
        {
            g.DrawRectangle(p, tx, ty, cardW, cardH);
        }
        TextRenderer.DrawText(g, timeLabel, Theme.MonoTiny, new Point(tx + 8, ty + 4), Theme.Text, Theme.TextFlagsPoint);
        TextRenderer.DrawText(g, entries, Theme.MonoTiny, new Point(tx + 8, ty + 18), Theme.Text2, Theme.TextFlagsPoint);
        using (var sw = new SolidBrush(Theme.SevColor[5]))
        {
            g.FillRectangle(sw, tx + 8, ty + 33, 6, 6);
        }
        TextRenderer.DrawText(g, errText, Theme.MonoTiny, new Point(tx + 18, ty + 31), Theme.SevColor[5], Theme.TextFlagsPoint);

        using (var p = new Pen(Theme.AccentEdge))
        {
            g.DrawLine(p, _curX, chartTop, _curX, baseY);
        }
    }
}
