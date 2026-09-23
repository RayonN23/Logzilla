using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Logzilla.App.Infrastructure;

namespace Logzilla.App.Controls;


internal sealed class TabStrip : Control
{
    public List<string> Titles = new();
    public List<string> Subs = new();
    public List<bool> Merged = new();
    /// <summary>Worst severity byte (0-6) present in each file; -1 = unknown/none.</summary>
    public List<int> WorstSev = new();
    public int Active = -1;
    public event EventHandler<IdxArgs> Picked;
    public event EventHandler<IdxArgs> Closed;

    public sealed class IdxArgs : EventArgs { public int I; public IdxArgs(int i) { I = i; } }

    private readonly List<Rectangle> _rects = new();
    private readonly List<Rectangle> _closes = new();
    private int _hot = -1, _hotClose = -1;

    public TabStrip()
    {
        AccessibleRole = AccessibleRole.PageTabList;
        AccessibleName = "Open files";
        AccessibleDescription = "One tab per open log. Ctrl+Tab cycles through them.";
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.BgChrome;
        Font = Theme.UiBody;
        Height = Dpi.S(Theme.TitleBarHeight);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = -1, hc = -1;
        for (var i = 0; i < _rects.Count; i++)
        {
            if (_rects[i].Contains(e.Location))
            {
                h = i;
            }
            if (i < _closes.Count && _closes[i].Contains(e.Location))
            {
                hc = i;
            }
        }
        if (h != _hot || hc != _hotClose) { _hot = h; _hotClose = hc; Invalidate(); }
    }
    protected override void OnMouseLeave(EventArgs e) { _hot = -1; _hotClose = -1; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        for (var i = 0; i < _closes.Count; i++)
        {
            if (_closes[i].Contains(e.Location))
            {
                Closed?.Invoke(this, new IdxArgs(i));
                return;
            }
        }
        for (var i = 0; i < _rects.Count; i++)
        {
            if (_rects[i].Contains(e.Location))
            {
                if (e.Button == MouseButtons.Middle)
                {
                    Closed?.Invoke(this, new IdxArgs(i));
                }
                else
                {
                    Picked?.Invoke(this, new IdxArgs(i));
                }
                return;
            }
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.BgChrome);
        _rects.Clear(); _closes.Clear();
        var x = Dpi.S(4);
        var tabH = Dpi.S(28);
        var tabY = (Height - tabH) / 2;
        for (var i = 0; i < Titles.Count; i++)
        {
            var label = Titles[i];
            var sub = i < Subs.Count ? Subs[i] : "";
            var dotW = Dpi.S(14);
            var w = TextRenderer.MeasureText(g, label, Theme.UiBody).Width
                    + TextRenderer.MeasureText(g, sub, Theme.MonoTiny).Width + dotW + Dpi.S(44);
            w = Math.Min(w, Dpi.S(240));
            var r = new Rectangle(x, tabY, w, tabH);
            _rects.Add(r);
            var on = i == Active;
            var back = on ? Theme.BgRaised : (i == _hot ? Theme.BgRowHover : Theme.BgChrome);
            using (var path = TextUtil.RoundedRect(r, Dpi.S(Theme.RadiusChip)))
            using (var b = new SolidBrush(back))
            {
                g.FillPath(b, path);
            }
            if (on)
            {
                using (var p = new Pen(Theme.LineControlStrong))
                using (var path = TextUtil.RoundedRect(r, Dpi.S(Theme.RadiusChip)))
                {
                    g.DrawPath(p, path);
                }
            }

            var sev = i < WorstSev.Count ? WorstSev[i] : -1;
            var dotX = x + Dpi.S(10);
            if (sev >= 0)
            {
                var c = Theme.IsNeutral(sev) ? Theme.TextDim : Theme.SevColor[sev];
                using (var b = new SolidBrush(c))
                {
                    g.FillEllipse(b, dotX, tabY + tabH / 2 - Dpi.S(3), Dpi.S(6), Dpi.S(6));
                }
            }

            var mergedTag = i < Merged.Count && Merged[i];
            var tx = dotX + dotW;
            var closeW = Dpi.S(18);
            var ls = TextRenderer.MeasureText(g, label, Theme.UiBody);
            TextRenderer.DrawText(g, label, Theme.UiBody, new Rectangle(tx, tabY, w - (tx - x) - closeW, tabH),
                on ? Theme.Text : Theme.Text2, Theme.TextFlagsEllipsis);
            if (sub.Length > 0 && tx + ls.Width + Dpi.S(40) < x + w)
            {
                TextRenderer.DrawText(g, sub, Theme.MonoTiny, new Point(tx + ls.Width + Dpi.S(6), tabY + (tabH - Dpi.S(11)) / 2), Theme.TextDim, Theme.TextFlagsPoint);
            }
            if (mergedTag)
            {
                using (var p = new Pen(Theme.Accent, 1.2f))
                {
                    g.DrawEllipse(p, dotX - 1, tabY + tabH / 2 - Dpi.S(4), Dpi.S(8), Dpi.S(8));
                }
            }

            var cr = new Rectangle(x + w - closeW - Dpi.S(4), tabY + tabH / 2 - Dpi.S(8), Dpi.S(16), Dpi.S(16));
            _closes.Add(cr);
            if (i == _hotClose)
            {
                using (var b = new SolidBrush(Color.FromArgb(60, Theme.SevColor[5])))
                {
                    g.FillEllipse(b, cr);
                }
            }
            using (var p = new Pen(i == _hotClose ? Theme.Text : Theme.TextDim, 1.2f))
            {
                var cx = cr.X + cr.Width / 2f; var cy = cr.Y + cr.Height / 2f; var s = Dpi.S(3);
                g.DrawLine(p, cx - s, cy - s, cx + s, cy + s);
                g.DrawLine(p, cx + s, cy - s, cx - s, cy + s);
            }
            x += w + Dpi.S(4);
        }
    }
}
