using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using Logzilla.App.Infrastructure;

namespace Logzilla.App.Controls;

/// <summary>
/// The pick list under the field filter editor's boxes: one row per field or value,
/// its count on the right, an optional tick box on the left. The highlighted row is
/// what Enter takes; the mouse moves the highlight as it passes.
/// </summary>
internal sealed class SuggestionList : Control
{
    public sealed class Item
    {
        public string Text = "";
        /// <summary>What the row shows, when it differs from <see cref="Text"/>.</summary>
        public string Label;
        public string Right = "";
        public bool Checked;
    }

    private List<Item> _items = new();
    private int _top;
    private int _highlight = -1;

    public event EventHandler<int> ItemClicked;
    public event EventHandler<int> ItemDoubleClicked;

    public SuggestionList()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.StandardDoubleClick, true);
        SetStyle(ControlStyles.Selectable, false);
        BackColor = Theme.BgData;
        Font = Theme.MonoBody;
        AccessibleRole = AccessibleRole.List;
    }

    /// <summary>Rows show a tick box and clicking one ticks it.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Checkable { get; set; }

    /// <summary>Shown when there is nothing to list.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string EmptyText { get; set; } = "";

    public IReadOnlyList<Item> Items => _items;

    public static int RowHeight => Math.Max(Dpi.S(24), Dpi.Text("Ag", Theme.MonoBody).Height + Dpi.S(8));

    private int VisibleRows => Math.Max(1, ClientSize.Height / RowHeight);

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Highlight
    {
        get => _highlight;
        set
        {
            _highlight = _items.Count == 0 ? -1 : Math.Clamp(value, -1, _items.Count - 1);
            if (_highlight >= 0)
            {
                if (_highlight < _top)
                {
                    _top = _highlight;
                }
                else if (_highlight >= _top + VisibleRows)
                {
                    _top = _highlight - VisibleRows + 1;
                }
            }
            Invalidate();
        }
    }

    public void SetItems(List<Item> items, int highlight)
    {
        _items = items ?? new List<Item>();
        _top = 0;
        AccessibleDescription = _items.Count + " suggestions";
        Highlight = highlight;
    }

    /// <summary>Arrow keys: moves the highlight, starting from the top when nothing
    /// was highlighted yet.</summary>
    public void Step(int delta)
    {
        if (_items.Count == 0)
        {
            return;
        }
        Highlight = _highlight < 0 ? (delta > 0 ? 0 : _items.Count - 1) : Math.Clamp(_highlight + delta, 0, _items.Count - 1);
    }

    private int IndexAt(int y)
    {
        var i = _top + y / RowHeight;
        return y >= 0 && i < _items.Count ? i : -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var i = IndexAt(e.Y);
        if (i >= 0 && i != _highlight)
        {
            Highlight = i;
        }
        Cursor = i >= 0 ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        var max = Math.Max(0, _items.Count - VisibleRows);
        _top = Math.Clamp(_top - Math.Sign(e.Delta) * 3, 0, max);
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        var i = IndexAt(e.Y);
        if (e.Button == MouseButtons.Left && i >= 0)
        {
            ItemClicked?.Invoke(this, i);
        }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        var i = IndexAt(e.Y);
        if (e.Button == MouseButtons.Left && i >= 0)
        {
            ItemDoubleClicked?.Invoke(this, i);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        if (_items.Count == 0)
        {
            TextRenderer.DrawText(g, EmptyText, Theme.UiBody,
                new Rectangle(Dpi.S(10), 0, Width - Dpi.S(20), Height), Theme.TextDim,
                TextFormatFlags.WordBreak | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            return;
        }
        var rowH = RowHeight;
        var scrollW = _items.Count > VisibleRows ? Dpi.S(4) : 0;
        var w = Width - scrollW;
        for (var i = _top; i < _items.Count && (i - _top) * rowH < Height; i++)
        {
            var it = _items[i];
            var y = (i - _top) * rowH;
            if (i == _highlight)
            {
                using var b = new SolidBrush(Theme.BgRaised);
                g.FillRectangle(b, 0, y, w, rowH);
            }
            var x = Dpi.S(10);
            if (Checkable)
            {
                DrawTick(g, new Rectangle(x, y + (rowH - Dpi.S(12)) / 2, Dpi.S(12), Dpi.S(12)), it.Checked);
                x += Dpi.S(12) + Dpi.S(8);
            }
            var rightW = it.Right.Length > 0 ? Dpi.Text(it.Right, Theme.MonoSmall).Width : 0;
            var textRight = w - Dpi.S(10) - (rightW > 0 ? rightW + Dpi.S(10) : 0);
            TextRenderer.DrawText(g, it.Label ?? it.Text, Theme.MonoBody, new Rectangle(x, y, Math.Max(10, textRight - x), rowH),
                it.Checked || i == _highlight ? Theme.Text : Theme.Text2, Theme.TextFlagsEllipsis);
            if (rightW > 0)
            {
                TextRenderer.DrawText(g, it.Right, Theme.MonoSmall, new Rectangle(w - Dpi.S(10) - rightW, y, rightW + 1, rowH),
                    Theme.TextDim, Theme.TextFlags);
            }
        }
        if (scrollW > 0)
        {
            // Where the visible rows sit in the whole list: a hint, not a control.
            var total = _items.Count * rowH;
            var thumbH = Math.Max(Dpi.S(16), Height * Height / total);
            var thumbY = (int)((long)(Height - thumbH) * _top / Math.Max(1, _items.Count - VisibleRows));
            using var b = new SolidBrush(Theme.LineControlStrong);
            g.FillRectangle(b, Width - scrollW, thumbY, scrollW, thumbH);
        }
    }

    private static void DrawTick(Graphics g, Rectangle r, bool on)
    {
        if (on)
        {
            using var b = new SolidBrush(Theme.Accent);
            g.FillRectangle(b, r);
            using var p = new Pen(Theme.BgData, Math.Max(1.5f, Dpi.Factor * 1.6f));
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.DrawLines(p, [
                new PointF(r.X + r.Width * 0.22f, r.Y + r.Height * 0.52f),
                new PointF(r.X + r.Width * 0.42f, r.Y + r.Height * 0.72f),
                new PointF(r.X + r.Width * 0.78f, r.Y + r.Height * 0.30f)]);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
        }
        else
        {
            using var p = new Pen(Theme.LineControlStrong);
            g.DrawRectangle(p, r.X, r.Y, r.Width - 1, r.Height - 1);
        }
    }
}
