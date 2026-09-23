using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Logzilla.App.Infrastructure;
using Logzilla.Core.Query;

namespace Logzilla.App.Controls;

internal sealed class FieldChipBar : Control
{
    private const int PadX = 12;
    private const int PadY = 4;
    private const int RowH = 34;
    private const int Gap = 6;
    private const int ChipPadX = 10;
    private const int CloseW = 20;
    private const int MaxValueChars = 40;
    private const string Caption = "Field filters";

    private IReadOnlyList<FieldFilter> _filters = Array.Empty<FieldFilter>();
    private readonly List<(Rectangle Chip, Rectangle Close)> _chips = new();
    private Rectangle _add, _clear;
    private int _captionW;
    private int _hot = -1;          // chip index, or AddSlot / ClearSlot
    private bool _hotClose;
    private int _focus = -1;
    private readonly ToolTip _tip = new() { InitialDelay = 500 };

    private const int AddSlot = -10;
    private const int ClearSlot = -11;

    public sealed class IndexArgs : EventArgs
    {
        public IndexArgs(int index, Point location) { Index = index; Location = location; }
        public int Index { get; }
        public Point Location { get; }
    }

    /// <summary>A chip was clicked (or Enter pressed on it): edit that filter.</summary>
    public event EventHandler<IndexArgs> EditRequested;
    /// <summary>A chip's × was clicked (or Delete pressed on it).</summary>
    public event EventHandler<IndexArgs> RemoveRequested;
    /// <summary>A chip was right-clicked; the location is in this control's coordinates.</summary>
    public event EventHandler<IndexArgs> MenuRequested;
    public event EventHandler AddRequested;
    public event EventHandler ClearRequested;

    public FieldChipBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Theme.BgChromeLow;
        Font = Theme.UiBody;
        TabStop = true;
        AccessibleRole = AccessibleRole.ToolBar;
        AccessibleName = "Field filters";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tip.Dispose();
        }
        base.Dispose(disposing);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public IReadOnlyList<FieldFilter> Filters
    {
        get => _filters;
        set
        {
            _filters = value ?? Array.Empty<FieldFilter>();
            _hot = -1;
            _focus = Math.Min(_focus, _filters.Count);
            AccessibleDescription = _filters.Count == 0 ? "None" : string.Join("; ", _filters.Select(FieldNames.Describe));
            Relayout();
            Invalidate();
        }
    }

    // ── layout ─────────────────────────────────────────────────────────

    /// <summary>What a chip says: the key, the operator and its values. Long values
    /// are shortened, and a chip too wide for the strip shows its first value and how
    /// many more there are.</summary>
    internal static (string Key, string Op, List<string> Values, bool LastIsMore) Parts(FieldFilter f, bool compact)
    {
        var values = new List<string>();
        if (compact && f.Values.Count > 1)
        {
            values.Add(Shorten(f.Values[0]));
            values.Add("+" + (f.Values.Count - 1) + " more");
        }
        else
        {
            foreach (var v in f.Values)
            {
                values.Add(Shorten(v));
            }
        }
        return (FieldNames.Display(f.Key), FieldFilter.OpText(f.Op), values, compact && f.Values.Count > 1);
    }

    internal static string Shorten(string v) =>
        v.Length > MaxValueChars ? string.Concat(v.AsSpan(0, MaxValueChars - 1), "…") : v;

    private static int TextW(string s, Font f) => Dpi.Text(s, f).Width;

    private static int ChipWidth(FieldFilter f, bool compact)
    {
        var (key, op, values, _) = Parts(f, compact);
        var w = Dpi.S(ChipPadX) + TextW(key, Theme.UiBody) + Dpi.S(5) + TextW(op, Theme.UiBody) + Dpi.S(5);
        for (var i = 0; i < values.Count; i++)
        {
            if (i > 0)
            {
                w += TextW(" or ", Theme.UiBody);
            }
            w += TextW(values[i], Theme.MonoSmall);
        }
        return w + Dpi.S(4) + Dpi.S(CloseW);
    }

    private bool Compact(int index) => ChipWidth(_filters[index], false) > MaxChipWidth;

    private int MaxChipWidth => Math.Max(Dpi.S(160), _layoutW - Dpi.S(PadX) * 2 - _captionW - Dpi.S(Gap));
    private int _layoutW;

    /// <summary>Lays the chips out left to right, wrapping, and returns the height the
    /// strip needs at the given width.</summary>
    public int Relayout(int width = -1)
    {
        if (width < 0)
        {
            width = ClientSize.Width;
        }
        _layoutW = width;
        _chips.Clear();
        _captionW = TextW(Caption, Theme.UiBody) + Dpi.S(12);
        var left = Dpi.S(PadX) + _captionW;
        var right = Math.Max(left + Dpi.S(160), width - Dpi.S(PadX));
        var rowH = Dpi.S(RowH);
        var chipH = Dpi.S(Theme.ChipHeight);
        int x = left, row = 0;

        Rectangle Place(int w)
        {
            if (x > left && x + w > right)
            {
                row++;
                x = left;
            }
            var r = new Rectangle(x, Dpi.S(PadY) + row * rowH + (rowH - chipH) / 2, Math.Min(w, right - left), chipH);
            x += r.Width + Dpi.S(Gap);
            return r;
        }

        for (var i = 0; i < _filters.Count; i++)
        {
            var chip = Place(Math.Min(ChipWidth(_filters[i], Compact(i)), right - left));
            var close = new Rectangle(chip.Right - Dpi.S(CloseW) - Dpi.S(2), chip.Y, Dpi.S(CloseW), chip.Height);
            _chips.Add((chip, close));
        }
        _add = Place(TextW("+ Add", Theme.UiBody) + Dpi.S(ChipPadX) * 2);
        _clear = Place(TextW("Clear all", Theme.UiBody) + Dpi.S(8));
        return Dpi.S(PadY) * 2 + (row + 1) * rowH;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Relayout();
    }

    // ── painting ───────────────────────────────────────────────────────

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        var firstRowY = Dpi.S(PadY);
        TextRenderer.DrawText(g, Caption, Theme.UiBody,
            new Rectangle(Dpi.S(PadX), firstRowY, _captionW, Dpi.S(RowH)), Theme.Text2, Theme.TextFlags);

        for (var i = 0; i < _chips.Count && i < _filters.Count; i++)
        {
            DrawChip(g, i);
        }
        DrawAdd(g);
        var clearColor = _hot == ClearSlot || _focus == _filters.Count + 1 ? Theme.Text : Theme.TextDim;
        TextRenderer.DrawText(g, "Clear all", Theme.UiBody, _clear, clearColor, Theme.TextFlagsCenter);
        if (_focus == _filters.Count + 1 && Focused)
        {
            using var p = new Pen(Theme.AccentDim);
            g.DrawRectangle(p, _clear.X, _clear.Y, _clear.Width - 1, _clear.Height - 1);
        }

        using (var line = new Pen(Theme.Line))
        {
            g.DrawLine(line, 0, Height - 1, Width, Height - 1);
        }
    }

    private void DrawChip(Graphics g, int i)
    {
        var (chip, close) = _chips[i];
        var f = _filters[i];
        var hot = _hot == i;
        var focused = Focused && _focus == i;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var path = TextUtil.RoundedRect(new Rectangle(chip.X, chip.Y, chip.Width - 1, chip.Height - 1), Dpi.S(Theme.RadiusChip)))
        {
            using (var b = new SolidBrush(hot && !_hotClose ? Theme.BgRaised : Theme.BgChipOn))
            {
                g.FillPath(b, path);
            }
            var border = focused ? Theme.AccentDim : f.Op == FieldOp.IsNot ? Theme.SevColor[5] : Theme.LineControlStrong;
            using var pen = new Pen(border);
            g.DrawPath(pen, path);
        }
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;

        var (key, op, values, lastIsMore) = Parts(f, Compact(i));
        var x = chip.X + Dpi.S(ChipPadX);
        var limit = close.X - Dpi.S(2);
        x = Run(g, key, Theme.UiBody, Theme.Text2, x, chip, limit) + Dpi.S(5);
        x = Run(g, op, Theme.UiBody, f.Op == FieldOp.IsNot ? Theme.SevColor[5] : Theme.TextDim, x, chip, limit) + Dpi.S(5);
        for (var k = 0; k < values.Count && x < limit; k++)
        {
            if (k > 0)
            {
                x = Run(g, " or ", Theme.UiBody, Theme.TextDim, x, chip, limit);
            }
            var more = lastIsMore && k == values.Count - 1;
            x = Run(g, values[k], more ? Theme.UiBody : Theme.MonoSmall, more ? Theme.Text3 : Theme.Text, x, chip, limit);
        }

        // ×: a small target of its own, lit on hover so it is clearly separate from
        // the chip body, which edits.
        var closeHot = hot && _hotClose;
        if (closeHot)
        {
            using var b = new SolidBrush(Theme.BgControl);
            var d = Math.Min(close.Width, close.Height) - Dpi.S(8);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.FillEllipse(b, close.X + (close.Width - d) / 2, close.Y + (close.Height - d) / 2, d, d);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
        }
        TextRenderer.DrawText(g, "×", Theme.UiBody, close, closeHot ? Theme.Text : Theme.TextDim, Theme.TextFlagsCenter);
    }

    private static int Run(Graphics g, string text, Font font, Color color, int x, Rectangle chip, int limit)
    {
        if (x >= limit)
        {
            return x;
        }
        var w = Dpi.Text(text, font).Width;
        TextRenderer.DrawText(g, text, font, new Rectangle(x, chip.Y, Math.Min(w + 1, limit - x), chip.Height),
            color, x + w > limit ? Theme.TextFlagsEllipsis : Theme.TextFlags);
        return x + w;
    }

    private void DrawAdd(Graphics g)
    {
        var hot = _hot == AddSlot;
        var focused = Focused && _focus == _filters.Count;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var path = TextUtil.RoundedRect(new Rectangle(_add.X, _add.Y, _add.Width - 1, _add.Height - 1), Dpi.S(Theme.RadiusChip)))
        {
            if (hot)
            {
                using var b = new SolidBrush(Theme.BgRaised);
                g.FillPath(b, path);
            }
            using var pen = new Pen(focused ? Theme.AccentDim : Theme.LineControl);
            if (!focused)
            {
                pen.DashStyle = System.Drawing.Drawing2D.DashStyle.Dash;
            }
            g.DrawPath(pen, path);
        }
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
        TextRenderer.DrawText(g, "+ Add", Theme.UiBody, _add, hot ? Theme.Text : Theme.Text2, Theme.TextFlagsCenter);
    }

    // ── mouse ──────────────────────────────────────────────────────────

    /// <summary>Which chip, or the add or clear slot, is at a point; -1 for none.</summary>
    internal int HitTest(Point p, out bool onClose)
    {
        onClose = false;
        for (var i = 0; i < _chips.Count; i++)
        {
            if (_chips[i].Chip.Contains(p))
            {
                onClose = _chips[i].Close.Contains(p);
                return i;
            }
        }
        if (_add.Contains(p))
        {
            return AddSlot;
        }
        return _clear.Contains(p) ? ClearSlot : -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hit = HitTest(e.Location, out var onClose);
        if (hit == _hot && onClose == _hotClose)
        {
            return;
        }
        _hot = hit;
        _hotClose = onClose;
        Cursor = hit == -1 ? Cursors.Default : Cursors.Hand;
        string tip = null;
        if (hit >= 0 && hit < _filters.Count)
        {
            tip = onClose ? "Remove this filter" : FieldNames.Describe(_filters[hit]) +"\r\nClick to edit, right-click for more";
        }
        else if (hit == AddSlot)
        {
            tip = "Add another field filter  (Ctrl+Shift+F)";
        }
        else if (hit == ClearSlot)
        {
            tip = "Remove every field filter";
        }
        _tip.SetToolTip(this, tip);
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hot = -1;
        _hotClose = false;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        var hit = HitTest(e.Location, out var onClose);
        if (e.Button == MouseButtons.Right)
        {
            if (hit >= 0)
            {
                MenuRequested?.Invoke(this, new IndexArgs(hit, e.Location));
            }
            return;
        }
        if (e.Button != MouseButtons.Left)
        {
            return;
        }
        if (hit >= 0)
        {
            _focus = hit;
            (onClose ? RemoveRequested : EditRequested)?.Invoke(this, new IndexArgs(hit, ChipAnchor(hit)));
        }
        else if (hit == AddSlot)
        {
            AddRequested?.Invoke(this, EventArgs.Empty);
        }
        else if (hit == ClearSlot)
        {
            ClearRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Just under a chip, where its editor opens.</summary>
    public Point ChipAnchor(int index) =>
        index >= 0 && index < _chips.Count ? new Point(_chips[index].Chip.X, _chips[index].Chip.Bottom + Dpi.S(2)) : new Point(0, Height);

    /// <summary>Just under the add chip.</summary>
    public Point AddAnchor => new(_add.X, _add.Bottom + Dpi.S(2));

    // ── keyboard ───────────────────────────────────────────────────────
    // Focus walks chip, chip, …, + Add, Clear all. Enter acts, Delete removes.

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Left or Keys.Right or Keys.Enter or Keys.Space or Keys.Delete or Keys.Back || base.IsInputKey(keyData);

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        if (_focus < 0)
        {
            _focus = 0;
        }
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var last = _filters.Count + 1;
        switch (e.KeyCode)
        {
            case Keys.Left: _focus = Math.Max(0, _focus - 1); break;
            case Keys.Right: _focus = Math.Min(last, _focus + 1); break;
            case Keys.Enter:
            case Keys.Space:
                if (_focus >= 0 && _focus < _filters.Count)
                {
                    EditRequested?.Invoke(this, new IndexArgs(_focus, ChipAnchor(_focus)));
                }
                else if (_focus == _filters.Count)
                {
                    AddRequested?.Invoke(this, EventArgs.Empty);
                }
                else if (_focus == last)
                {
                    ClearRequested?.Invoke(this, EventArgs.Empty);
                }
                break;
            case Keys.Delete:
            case Keys.Back:
                if (_focus >= 0 && _focus < _filters.Count)
                {
                    RemoveRequested?.Invoke(this, new IndexArgs(_focus, ChipAnchor(_focus)));
                }
                break;
            default:
                return;
        }
        e.Handled = true;
        Invalidate();
    }
}
