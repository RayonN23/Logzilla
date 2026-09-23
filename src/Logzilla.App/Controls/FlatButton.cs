using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using Logzilla.App.Infrastructure;

namespace Logzilla.App.Controls;

/// <summary>
/// Owner-drawn push button. A WinForms Button ignores FlatAppearance.BorderColor
/// once the app runs in dark colour mode and falls back to a bright system
/// border, so every button in the shell is drawn here instead.
/// </summary>
internal sealed class FlatButton : Control
{
    public enum Look { Outline, Ghost, Dashed, Tab }

    private Look _look = Look.Outline;
    private Color _border = Theme.LineControl;
    private bool _selected;
    private bool _hot, _down;

    public FlatButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor, true);
        Font = Theme.UiBody;
        ForeColor = Theme.Text2;
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleRole = AccessibleRole.PushButton;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Look Style { get => _look; set { _look = value; Invalidate(); } }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color BorderColor { get => _border; set { _border = value; Invalidate(); } }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Selected { get => _selected; set { _selected = value; Invalidate(); } }

    /// <summary>Sizes the button to its label with the given horizontal padding and fixed height.</summary>
    public void FitTo(int minWidth, int height, int padX = 12)
    {
        var t = TextRenderer.MeasureText(Text ?? "", Font, new Size(short.MaxValue, short.MaxValue), Theme.TextFlags);
        Width = Math.Max(Dpi.S(minWidth), t.Width + Dpi.S(padX) * 2);
        Height = Dpi.S(height);
    }

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
    protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hot = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Enter or Keys.Space || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Enter or Keys.Space)
        {
            OnClick(EventArgs.Empty);
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.BgChrome);
        var r = new Rectangle(0, 0, Width - 1, Height - 1);

        if (_look == Look.Tab)
        {
            PaintTab(g, r);
            return;
        }

        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var path = TextUtil.RoundedRect(r, Dpi.S(Theme.RadiusChip)))
        {
            if (_hot || _down)
            {
                using var b = new SolidBrush(_down ? Theme.BgControl : Theme.BgRaised);
                g.FillPath(b, path);
            }
            if (_look != Look.Ghost || Focused)
            {
                using var pen = new Pen(Focused ? Theme.AccentDim : _border, 1f);
                if (_look == Look.Dashed && !Focused)
                {
                    pen.DashStyle = System.Drawing.Drawing2D.DashStyle.Dash;
                }
                g.DrawPath(pen, path);
            }
        }
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;

        var fore = Enabled ? (_hot ? Theme.Text : ForeColor) : Theme.TextDim;
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, fore, Theme.TextFlagsCenter);
    }

    private void PaintTab(Graphics g, Rectangle r)
    {
        var fore = _selected ? Theme.Text : (_hot ? Theme.Text2 : Theme.TextDim);
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, fore, Theme.TextFlagsCenter);
        if (_selected)
        {
            using var b = new SolidBrush(Theme.Accent);
            g.FillRectangle(b, Dpi.S(6), r.Bottom - Dpi.S(1), Width - Dpi.S(12), Dpi.S(2));
        }
        if (Focused)
        {
            using var p = new Pen(Theme.AccentDim);
            g.DrawRectangle(p, r);
        }
    }
}
