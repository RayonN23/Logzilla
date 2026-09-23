using System;
using System.Drawing;
using System.Windows.Forms;
using Logzilla.App.Infrastructure;

namespace Logzilla.App.Controls;

/// <summary>
/// An owner-drawn on/off pill: severity chips, the .* / Aa search toggles, Wrap
/// and Live tail. FlatAppearance.BorderColor on a Button is ignored once the app
/// is in dark colour mode, so anything that needs a visible on/off border has to
/// be owner-drawn instead.
/// </summary>
internal sealed class ToggleChip : Control
{
    public string Label = "";
    public string Count = "";
    public bool On;
    public Color? Dot;          // filled/hollow dot in front of the label, or null for none
    public Color AccentColor = Theme.Accent;
    public bool Enabled2 = true; // "Enabled" is already a Control property; this is the zero-count/disabled look
    /// <summary>No border while off — for toggles that sit inside another field (.* / Aa).</summary>
    public bool BorderlessOff;
    private bool _hot;

    public event EventHandler Click2;
    public event EventHandler RightClick;

    public ToggleChip()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.BgChromeLow;
        Font = Theme.UiBody;
        Height = Dpi.S(Theme.ChipHeight);
        Cursor = Cursors.Hand;
        TabStop = true;
    }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Enter or Keys.Space || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Enter or Keys.Space)
        {
            Click2?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (!Enabled2)
        {
            return;
        }
        if (e.Button == MouseButtons.Right)
        {
            RightClick?.Invoke(this, EventArgs.Empty);
        }
        else if (ClientRectangle.Contains(e.Location))
        {
            Click2?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnMouseEnter(EventArgs e) { _hot = true; base.OnMouseEnter(e); Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { _hot = false; base.OnMouseLeave(e); Invalidate(); }

    private const int PadX = 10;
    private const int DotSize = 7;
    private const int DotGap = 7;
    private const int CountGap = 8;

    public void Fit(int minWidth)
    {
        var lw = Dpi.Text(Label, Font).Width;
        var cw = Count.Length > 0 ? Dpi.Text(Count, Theme.MonoSmall).Width + Dpi.S(CountGap) : 0;
        var dotW = Dot.HasValue ? Dpi.S(DotSize + DotGap) : 0;
        Width = Math.Max(Dpi.S(minWidth), Dpi.S(PadX) * 2 + dotW + lw + cw);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.BgChromeLow);
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        Color fore;
        if (!Enabled2)
        {
            fore = Theme.TextDim;
        }
        else if (On || _hot)
        {
            fore = Theme.Text;
        }
        else
        {
            fore = Dot.HasValue ? Theme.TextDim : Theme.Text2;
        }
        var border = On ? Theme.LineControlStrong : Theme.LineControl;

        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var path = TextUtil.RoundedRect(r, Dpi.S(Theme.RadiusChip)))
        {
            if (On || (_hot && Enabled2))
            {
                using var b = new SolidBrush(On ? Theme.BgChipOn : Theme.BgRaised);
                g.FillPath(b, path);
            }
            if (Focused || On || !BorderlessOff)
            {
                using var pen = new Pen(Focused ? Theme.AccentDim : border, 1f);
                g.DrawPath(pen, path);
            }
        }
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;

        var hasCount = Count.Length > 0;
        if (!Dot.HasValue && !hasCount)
        {
            TextRenderer.DrawText(g, Label, Font, ClientRectangle, fore, Theme.TextFlagsCenter);
            return;
        }

        var x = Dpi.S(PadX);
        if (Dot.HasValue)
        {
            var d = Dpi.S(DotSize);
            var dy = (Height - d) / 2;
            if (On)
            {
                using var b = new SolidBrush(Dot.Value);
                g.FillRectangle(b, x, dy, d, d);
            }
            else
            {
                using var p = new Pen(Dot.Value, Dpi.Factor * 1.5f);
                g.DrawRectangle(p, x, dy, d - 1, d - 1);
            }
            x += d + Dpi.S(DotGap);
        }

        var countW = hasCount ? Dpi.Text(Count, Theme.MonoSmall).Width : 0;
        var right = Width - Dpi.S(PadX);
        var labelW = right - x - (hasCount ? countW + Dpi.S(CountGap) : 0);
        TextRenderer.DrawText(g, Label, Font, new Rectangle(x, 0, Math.Max(10, labelW), Height), fore, Theme.TextFlagsEllipsis);
        if (hasCount)
        {
            TextRenderer.DrawText(g, Count, Theme.MonoSmall, new Rectangle(right - countW, 0, countW + 1, Height),
                On && Enabled2 ? Theme.Text2 : Theme.TextDim, Theme.TextFlags);
        }
    }
}
