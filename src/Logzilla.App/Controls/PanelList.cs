using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Logzilla.App.Infrastructure;

namespace Logzilla.App.Controls;

/// <summary>
/// The Inspector's row list: VOLUME / SEVERITY / BUSIEST ERROR WINDOWS style rows,
/// each right-anchored against the panel's own right padding so numeric values
/// never clip (that was failure #1 in the redesign brief).
/// </summary>
internal sealed class PanelList : Control
{
    public List<PanelRow> Items = new();
    public event EventHandler<ItemArgs> Activate;
    private readonly ThinScrollBar _sb = new(true);
    private int _row = 22, _hot = -1;
    private static int RightPad => Dpi.S(14);
    private static int LeftPad => Dpi.S(12);

    /// <summary>A value that needs more than this many lines is the rare pathological
    /// case; past it the row would crowd out everything else in the panel.</summary>
    private const int MaxLabelLines = 4;

    private void Remeasure()
    {
        var text = Dpi.Text("Ag", Theme.MonoBody).Height + Dpi.S(9);
        _row = Math.Max(Dpi.S(22), text);
    }

    private int ContentWidth => ClientSize.Width - (_sb.Visible ? _sb.Width : 0);

    /// <summary>
    /// Greedy word wrap for a panel label. The inspector is narrow and its values
    /// (loggers, namespaces, paths) are long, so cutting them off with an ellipsis
    /// hides the very thing the panel exists to show — they wrap instead.
    /// </summary>
    private static List<string> WrapLabel(string text, Font font, int width, int maxLines)
    {
        var lines = new List<string>();
        if (string.IsNullOrEmpty(text) || width <= 0)
        {
            lines.Add(text ?? "");
            return lines;
        }
        var avg = Math.Max(1f, Dpi.Text("0123456789", font).Width / 10f);
        var pos = 0;
        while (pos < text.Length && lines.Count < maxLines)
        {
            var remaining = text.Length - pos;
            var take = Math.Min(remaining, Math.Max(1, (int)(width / avg)));
            while (pos + take < text.Length && Dpi.Text(text.Substring(pos, take + 1), font).Width <= width)
            {
                take++;
            }
            while (take > 1 && Dpi.Text(text.Substring(pos, take), font).Width > width)
            {
                take--;
            }
            if (pos + take < text.Length)
            {
                // Prefer a word boundary, but only when one exists past the margin.
                var brk = text.LastIndexOf(' ', pos + take - 1, take);
                if (brk > pos)
                {
                    take = brk - pos + 1;
                }
            }
            lines.Add(text.Substring(pos, take).TrimEnd());
            pos += take;
        }
        return lines;
    }

    /// <summary>Row height in pixels — a wrapped leader row is taller than one line.</summary>
    private int HeightOf(PanelRow it, int w)
    {
        if (it.Header || it.Dot.HasValue || it.Chevron || it.Bar >= 0)
        {
            return _row;
        }
        return LeaderLines(it, w).Count * _row;
    }

    private static List<string> LeaderLines(PanelRow it, int w)
    {
        var x = LeftPad + it.Indent * Dpi.S(12);
        var rw = it.Right.Length > 0 ? Dpi.Text(it.Right, Theme.MonoBody).Width : 0;
        var valueRight = w - RightPad - (it.Unit.Length > 0 ? UnitColW + Dpi.S(6) : 0);
        var labelRight = it.Right.Length > 0 ? valueRight - rw - Dpi.S(8) : w - RightPad;
        var font = it.UiLabel ? Theme.UiBody : Theme.MonoBody;
        return WrapLabel(it.Left, font, Math.Max(Dpi.S(40), labelRight - x), MaxLabelLines);
    }

    /// <summary>Item index at a y offset, walking the variable row heights.</summary>
    private int IndexAt(int y)
    {
        var w = ContentWidth;
        var top = 0;
        for (var idx = _sb.Value; idx < Items.Count; idx++)
        {
            var h = HeightOf(Items[idx], w);
            if (y >= top && y < top + h)
            {
                return idx;
            }
            top += h;
            if (top > Height)
            {
                break;
            }
        }
        return -1;
    }

    /// <summary>Height of every item together, used to decide whether the list
    /// overflows at all.</summary>
    private int TotalHeight()
    {
        var w = ContentWidth;
        var sum = 0;
        foreach (var it in Items)
        {
            sum += HeightOf(it, w);
        }
        return sum;
    }

    /// <summary>
    /// How many items fit on one page, measured from the top. Measuring from the
    /// current scroll position instead would report a one-item page while scrolled
    /// to the bottom, and the scrollbar would never hide once the panel grew.
    /// </summary>
    private int VisibleItems()
    {
        var w = ContentWidth;
        int used = 0, count = 0;
        foreach (var it in Items)
        {
            used += HeightOf(it, w);
            if (used > Height)
            {
                break;
            }
            count++;
        }
        return Math.Max(1, count);
    }

    public sealed class ItemArgs : EventArgs
    {
        public PanelRow Item; public bool Ctrl;
        public ItemArgs(PanelRow i, bool c) { Item = i; Ctrl = c; }
    }

    public PanelList()
    {
        AccessibleRole = AccessibleRole.List;
        AccessibleName = "Inspector";
        AccessibleDescription = "Volume, severity and busiest-window statistics for the entries shown.";
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.BgChrome;
        Font = Theme.MonoBody;
        _sb.Dock = DockStyle.Right;
        _sb.ValueChanged += delegate { Invalidate(); };
        _sb.Width = Dpi.S(10);
        Controls.Add(_sb);
        Remeasure();
    }

    public void SetItems(List<PanelRow> items)
    {
        Items = items;
        _sb.Value = 0;
        Sync();
        Invalidate();
    }

    private void Sync()
    {
        var vis = VisibleItems();
        _sb.Maximum = Math.Max(0, Items.Count - 1);
        _sb.LargeChange = Math.Max(1, vis);
        _sb.Visible = TotalHeight() > Height;
        if (_sb.Value > Math.Max(0, Items.Count - vis))
        {
            _sb.Value = Math.Max(0, Items.Count - vis);
        }
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); Sync(); }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); Remeasure(); Sync(); Invalidate(); }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        var d = -Math.Sign(e.Delta) * 3;
        var max = Math.Max(0, _sb.Maximum - _sb.LargeChange + 1);
        _sb.Value = Math.Max(0, Math.Min(max, _sb.Value + d));
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var idx = IndexAt(e.Y);
        if (idx != _hot) { _hot = idx; Invalidate(); }
    }
    protected override void OnMouseLeave(EventArgs e) { _hot = -1; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        var idx = IndexAt(e.Y);
        if (idx >= 0 && idx < Items.Count && Activate != null)
        {
            Activate(this, new ItemArgs(Items[idx], (ModifierKeys & Keys.Control) == Keys.Control));
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.BgChrome);
        var w = ClientSize.Width - (_sb.Visible ? _sb.Width : 0);
        var y = 0;
        for (var idx = _sb.Value; idx < Items.Count && y < Height; idx++)
        {
            var it = Items[idx];
            var rowH = HeightOf(it, w);
            var clickable = !it.Header;
            if (idx == _hot && clickable)
            {
                using (var b = new SolidBrush(Theme.BgRaised))
                {
                    g.FillRectangle(b, 0, y, w, rowH);
                }
            }

            if (it.Header)
            {
                DrawHeader(g, it, y, w);
            }
            else if (it.Dot.HasValue)
            {
                DrawSeverityRow(g, it, y, w);
            }
            else if (it.Chevron)
            {
                DrawChevronRow(g, it, y, w);
            }
            else if (it.Bar >= 0)
            {
                DrawBarRow(g, it, y, w);
            }
            else
            {
                DrawLeaderRow(g, it, y, w);
            }
            y += rowH;
        }
    }

    // Section head sits on the lower part of its row with a hairline under it,
    // so consecutive sections read as separated blocks rather than one list.
    private void DrawHeader(Graphics g, PanelRow it, int y, int w)
    {
        var ruleY = y + _row - Dpi.S(3);
        var textY = ruleY - Dpi.S(5) - Theme.UiHead.Height;
        TextUtil.DrawTracked(g, it.Left, Theme.UiHead, new Point(LeftPad, textY), Theme.TextDim, 0.8f);
        using (var p = new Pen(Theme.Line))
        {
            g.DrawLine(p, LeftPad, ruleY, w - RightPad, ruleY);
        }
    }

    private void DrawLeaderRow(Graphics g, PanelRow it, int y, int w)
    {
        var x = LeftPad + it.Indent * Dpi.S(12);
        var rightText = it.Right;
        var rw = rightText.Length > 0 ? Dpi.Text(rightText, Theme.MonoBody).Width : 0;

        // Values right-align to one edge; a unit (/min, %) gets its own fixed-width
        // column after it so the numbers above and below still line up.
        var valueRight = w - RightPad;
        if (it.Unit.Length > 0)
        {
            var unitRect = new Rectangle(valueRight - UnitColW, y, UnitColW, _row);
            TextRenderer.DrawText(g, it.Unit, Theme.MonoTiny, unitRect, Theme.TextDim, Theme.TextFlagsRight);
            valueRight -= UnitColW + Dpi.S(6);
        }
        var valueX = valueRight - rw;

        var labelFont = it.UiLabel ? Theme.UiBody : Theme.MonoBody;
        var lines = LeaderLines(it, w);
        var labelW = Math.Max(20, (rightText.Length > 0 ? valueX - Dpi.S(8) : w - RightPad) - x);

        // The dot leader only makes sense when label and value share one line.
        if (lines.Count == 1 && rightText.Length > 0)
        {
            var leaderStart = x + Dpi.Text(lines[0], labelFont).Width + Dpi.S(8);
            var leaderEnd = valueX - Dpi.S(8);
            if (leaderEnd > leaderStart)
            {
                using var p = new Pen(Theme.LineLeader) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot };
                g.DrawLine(p, leaderStart, y + _row / 2 + Dpi.S(3), leaderEnd, y + _row / 2 + Dpi.S(3));
            }
        }

        for (var k = 0; k < lines.Count; k++)
        {
            TextRenderer.DrawText(g, lines[k], labelFont, new Rectangle(x, y + k * _row, labelW, _row),
                it.Accent, Theme.TextFlags);
        }
        if (rightText.Length > 0)
        {
            TextRenderer.DrawText(g, rightText, Theme.MonoBody, new Rectangle(valueX - Dpi.S(2), y, rw + Dpi.S(2), _row), Theme.Text, Theme.TextFlagsRight);
        }
    }

    private static int UnitColW => Dpi.Text("100.0%", Theme.MonoTiny).Width;
    private static int CountColW => Dpi.Text("000,000", Theme.MonoBody).Width;

    private void DrawSeverityRow(Graphics g, PanelRow it, int y, int w)
    {
        var cy = y + _row / 2;
        var dot = Dpi.S(7);
        var on = it.Dot.GetValueOrDefault();
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        if (on)
        {
            using var b = new SolidBrush(it.Accent);
            g.FillEllipse(b, LeftPad, cy - dot / 2f, dot, dot);
        }
        else
        {
            using var p = new Pen(it.Accent, Dpi.Factor * 1.5f);
            g.DrawEllipse(p, LeftPad + 0.75f, cy - dot / 2f + 0.75f, dot - 1.5f, dot - 1.5f);
        }
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;

        var labelX = LeftPad + dot + Dpi.S(10);
        var labelW = Dpi.Text("DEBUG", Theme.UiBody).Width + Dpi.S(8);
        TextRenderer.DrawText(g, it.Left, Theme.UiBody, new Rectangle(labelX, y, labelW, _row),
            on ? Theme.Text2 : Theme.TextDim, Theme.TextFlags);

        // Fixed count and percent columns so every row's bar ends at the same x.
        var unitRight = w - RightPad;
        if (it.Unit.Length > 0)
        {
            TextRenderer.DrawText(g, it.Unit, Theme.MonoTiny, new Rectangle(unitRight - UnitColW, y, UnitColW, _row), Theme.TextDim, Theme.TextFlagsRight);
        }
        var countRight = unitRight - UnitColW - Dpi.S(8);
        var countX = countRight - CountColW;
        TextRenderer.DrawText(g, it.Right, Theme.MonoBody, new Rectangle(countX, y, CountColW, _row), on ? Theme.Text : Theme.TextDim, Theme.TextFlagsRight);

        var barX = labelX + labelW;
        var barW = Math.Max(10, countX - Dpi.S(10) - barX);
        var barH = Dpi.S(6);
        var barY = cy - barH / 2;
        using (var b = new SolidBrush(Theme.BgData))
        {
            g.FillRectangle(b, barX, barY, barW, barH);
        }
        if (it.BarOutline)
        {
            using var p = new Pen(Theme.LineControl);
            g.DrawRectangle(p, barX, barY, barW - 1, barH - 1);
        }
        else if (it.Bar > 0)
        {
            var fw = Math.Max(Dpi.S(2), (int)(barW * Math.Clamp(it.Bar, 0f, 1f)));
            using var b = new SolidBrush(it.Accent);
            g.FillRectangle(b, barX, barY, fw, barH);
        }
    }

    private void DrawChevronRow(Graphics g, PanelRow it, int y, int w)
    {
        TextRenderer.DrawText(g, it.Left, Theme.MonoBody, new Rectangle(LeftPad, y, Dpi.S(96), _row), Theme.Text3, Theme.TextFlags);

        var chevW = Dpi.S(12);
        var chevX = w - RightPad - chevW;
        using (var p = new Pen(Theme.TextDim, 1.3f))
        {
            var cy = y + _row / 2;
            g.DrawLines(p, [new Point(chevX, cy - 4), new Point(chevX + 4, cy), new Point(chevX, cy + 4)]);
        }

        var countText = it.Right;
        var countW = TextRenderer.MeasureText(g, countText, Theme.MonoBody, new Size(short.MaxValue, short.MaxValue), Theme.TextFlags).Width;
        var wordW = TextRenderer.MeasureText(g, "errors", Theme.UiBody, new Size(short.MaxValue, short.MaxValue), Theme.TextFlags).Width;
        var wordX = chevX - 6 - wordW;
        var countX = wordX - 4 - countW;
        TextRenderer.DrawText(g, countText, Theme.MonoBody, new Rectangle(countX, y, countW + 2, _row), it.Accent, Theme.TextFlags);
        TextRenderer.DrawText(g, "errors", Theme.UiBody, new Rectangle(wordX, y, wordW + 2, _row), Theme.TextDim, Theme.TextFlags);

        var barX = LeftPad + Dpi.S(100);
        var barW = Math.Max(20, countX - 8 - barX);
        var barY = y + _row / 2 - 3;
        using (var b = new SolidBrush(Theme.BgData))
        {
            g.FillRectangle(b, barX, barY, barW, 6);
        }
        var fw = (int)(barW * Math.Clamp(it.Bar, 0f, 1f));
        if (fw > 0)
        {
            using (var b = new SolidBrush(it.Accent))
            {
                g.FillRectangle(b, barX, barY, fw, 6);
            }
        }
    }

    private void DrawBarRow(Graphics g, PanelRow it, int y, int w)
    {
        var x = LeftPad + it.Indent * Dpi.S(12);
        var countW = it.Right.Length > 0 ? Dpi.Text(it.Right, Theme.MonoBody).Width + Dpi.S(10) : 0;

        // The label gets the room it actually needs rather than a fixed 60px slot;
        // the proportion bar becomes a hairline under the text, so a long source or
        // field name reads in full instead of being cut to "Mainte…".
        var labelW = Math.Max(Dpi.S(40), w - RightPad - countW - x);
        TextRenderer.DrawText(g, it.Left, Theme.MonoBody, new Rectangle(x, y, labelW, _row),
            it.Accent, Theme.TextFlagsEllipsis);

        var barH = Math.Max(2, Dpi.S(2));
        var by = y + _row - barH - Dpi.S(3);
        using (var b = new SolidBrush(Theme.BgData))
        {
            g.FillRectangle(b, x, by, labelW, barH);
        }
        var fw = (int)(labelW * Math.Clamp(it.Bar, 0f, 1f));
        if (fw > 0)
        {
            using var b = new SolidBrush(it.Accent);
            g.FillRectangle(b, x, by, Math.Max(1, fw), barH);
        }
        if (it.Right.Length > 0)
        {
            TextRenderer.DrawText(g, it.Right, Theme.MonoBody, new Rectangle(0, y, w - RightPad, _row), Theme.Text2, Theme.TextFlagsRight);
        }
    }
}
