using System;
using System.Drawing;
using System.Windows.Forms;
using Logzilla.App.Infrastructure;

namespace Logzilla.App.Controls;

/// <summary>
/// One control drawing both segments (e.g. Highlight | Filter to matches), not two
/// adjacent buttons — so the active segment reads as a single selected state.
/// Each segment is sized to its own label so the pill never overhangs its text.
/// </summary>
internal sealed class SegmentedControl : Control
{
    private const int SegPad = 10;
    private const int Inset = 3;

    public string LeftLabel = "", RightLabel = "";
    /// <summary>true = right segment active.</summary>
    public bool RightActive;
    public event EventHandler Changed;

    private int _split;
    private int _hot = -1;

    public SegmentedControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.BgChrome;
        Font = Theme.UiBody;
        Height = Dpi.S(Theme.ControlHeight);
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleRole = AccessibleRole.PageTabList;
    }

    public void Fit()
    {
        var lw = Dpi.Text(LeftLabel, Font).Width + Dpi.S(SegPad) * 2;
        var rw = Dpi.Text(RightLabel, Font).Width + Dpi.S(SegPad) * 2;
        _split = Dpi.S(Inset) + lw;
        Width = _split + rw + Dpi.S(Inset);
    }

    private Rectangle Segment(bool right)
    {
        var i = Dpi.S(Inset);
        return right
            ? new Rectangle(_split, i, Width - i - _split, Height - i * 2)
            : new Rectangle(i, i, _split - i, Height - i * 2);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var h = e.X >= _split ? 1 : 0;
        if (h != _hot) { _hot = h; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e) { _hot = -1; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        SetRight(e.X >= _split);
    }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Left or Keys.Right)
        {
            SetRight(e.KeyCode == Keys.Right);
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override AccessibleObject CreateAccessibilityInstance() => new SwitchAccessible(this);

    /// <summary>Reads out which side is chosen, and switches to the other side when a
    /// screen reader or UI automation presses it — a plain Control offers neither.</summary>
    private sealed class SwitchAccessible(SegmentedControl owner) : ControlAccessibleObject(owner)
    {
        public override string Name => owner.AccessibleName ?? owner.LeftLabel + " or " + owner.RightLabel;

        public override string Value => owner.RightActive ? owner.RightLabel : owner.LeftLabel;

        public override string DefaultAction => "Switch to " + (owner.RightActive ? owner.LeftLabel : owner.RightLabel);

        public override void DoDefaultAction() => owner.SetRight(!owner.RightActive);
    }

    private void SetRight(bool right)
    {
        if (right == RightActive)
        {
            return;
        }
        RightActive = right;
        Invalidate();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.BgChrome);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = TextUtil.RoundedRect(r, Dpi.S(Theme.RadiusChip)))
        using (var pen = new Pen(Focused ? Theme.AccentDim : Theme.LineControl))
        {
            g.DrawPath(pen, path);
        }

        var active = Segment(RightActive);
        using (var path = TextUtil.RoundedRect(active, Dpi.S(Theme.RadiusSmall)))
        using (var b = new SolidBrush(Theme.BgControl))
        {
            g.FillPath(b, path);
        }
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;

        var left = Segment(false);
        var right = Segment(true);
        TextRenderer.DrawText(g, LeftLabel, Font, left, LabelColor(false), Theme.TextFlagsCenter);
        TextRenderer.DrawText(g, RightLabel, Font, right, LabelColor(true), Theme.TextFlagsCenter);
    }

    private Color LabelColor(bool right)
    {
        if (right == RightActive)
        {
            return Theme.Text;
        }
        return _hot == (right ? 1 : 0) ? Theme.Text2 : Theme.TextDim;
    }
}
