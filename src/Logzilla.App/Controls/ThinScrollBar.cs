using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using Logzilla.App.Infrastructure;

namespace Logzilla.App.Controls;


// A WinForms scrollbar sizes its thumb as LargeChange / (Maximum + 1). With
// 30,000 lines and 40 on screen that is 0.13% of the track, so the thumb
// renders sub-pixel and the bar looks empty — worse the bigger the file, which
// is exactly backwards for a tool built for huge logs. This one enforces a
// minimum thumb and paints in the app's own colours.
internal sealed class ThinScrollBar : Control
{
    public bool Vertical = true;

    private int _value, _max, _large = 1;
    private bool _dragging, _hot;
    private int _grabOffset;

    public event EventHandler ValueChanged;

    public ThinScrollBar(bool vertical)
    {
        AccessibleRole = AccessibleRole.ScrollBar;
        AccessibleName = vertical ? "Vertical scroll bar" : "Horizontal scroll bar";
        Vertical = vertical;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.BgData;
        TabStop = false;
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Value
    {
        get { return _value; }
        set
        {
            var v = Clamp(value);
            if (v == _value)
            {
                return;
            }
            _value = v;
            Invalidate();
            if (ValueChanged != null)
            {
                ValueChanged(this, EventArgs.Empty);
            }
        }
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Maximum
    {
        get { return _max; }
        set { _max = Math.Max(0, value); _value = Clamp(_value); Invalidate(); }
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int LargeChange
    {
        get { return _large; }
        set { _large = Math.Max(1, value); _value = Clamp(_value); Invalidate(); }
    }

    // Highest legal Value: the last position where a full page still fits.
    private int MaxValue { get { return Math.Max(0, _max - _large + 1); } }
    private int Clamp(int v) { return Math.Max(0, Math.Min(MaxValue, v)); }

    private int Track { get { return Vertical ? Height : Width; } }
    private static int MinThumb { get { return Math.Max(Dpi.S(24), Dpi.S(10)); } }

    private bool Active { get { return Enabled && MaxValue > 0 && Track > MinThumb; } }

    private int ThumbLen()
    {
        var total = _max + 1;
        if (total <= 0)
        {
            return Track;
        }
        var len = (int)((long)Track * _large / total);
        if (len < MinThumb)
        {
            len = MinThumb;
        }
        if (len > Track)
        {
            len = Track;
        }
        return len;
    }

    private int ThumbPos()
    {
        var travel = Track - ThumbLen();
        if (travel <= 0 || MaxValue <= 0)
        {
            return 0;
        }
        return (int)((long)travel * _value / MaxValue);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (!Active)
        {
            return;
        }
        var p = Vertical ? e.Y : e.X;
        int tp = ThumbPos(), tl = ThumbLen();
        if (p >= tp && p < tp + tl)
        {
            _dragging = true;
            _grabOffset = p - tp;
        }
        else
        {
            // clicking the track pages toward the click, like a native bar
            Value = _value + (p < tp ? -_large : _large);
        }
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var wasHot = _hot;
        _hot = true;
        if (_dragging && Active)
        {
            var p = (Vertical ? e.Y : e.X) - _grabOffset;
            var travel = Track - ThumbLen();
            Value = travel <= 0 ? 0 : (int)((long)p * MaxValue / travel);
        }
        else if (!wasHot)
        {
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _dragging = false;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hot = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        using (var b = new SolidBrush(Theme.BgData))
        {
            g.FillRectangle(b, ClientRectangle);
        }
        if (!Active)
        {
            return;
        }

        int tp = ThumbPos(), tl = ThumbLen();
        var c = _dragging ? Theme.Accent : _hot ? Theme.Text3 : Theme.LineControlStrong;
        var r = Vertical
            ? new Rectangle(Dpi.S(3), tp + 1, Math.Max(3, Width - Dpi.S(5)), Math.Max(4, tl - 2))
            : new Rectangle(tp + 1, Dpi.S(3), Math.Max(4, tl - 2), Math.Max(3, Height - Dpi.S(5)));
        using (var b = new SolidBrush(c))
        {
            g.FillRectangle(b, r);
        }
    }
}
