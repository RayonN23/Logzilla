using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Windows.Forms;
using Logzilla.App.Infrastructure;
using Logzilla.Core.Logs;
using Logzilla.Core.Query;

namespace Logzilla.App.Controls;


internal sealed class MiniMap : Control
{
    public FilterResult Res;
    public LogView View;
    public LogDocument File;
    public event EventHandler<JumpFractionEventArgs> Jump;
    public sealed class JumpFractionEventArgs : EventArgs { public float F; public JumpFractionEventArgs(float f) { F = f; } }

    public MiniMap()
    {
        AccessibleRole = AccessibleRole.Diagram;
        AccessibleName = "Severity map";
        AccessibleDescription = "Error density across the whole file. Click to jump.";
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Width = Dpi.S(Theme.MinimapWidth);
        BackColor = Theme.BgData;
        Cursor = Cursors.Hand;
        // A mouse-only strip: it has nothing to do with the keyboard, so it must not take
        // focus away from the grid beside it.
        TabStop = false;
        SetStyle(ControlStyles.Selectable, false);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (Jump != null)
        {
            Jump(this, new JumpFractionEventArgs((float)e.Y / Math.Max(1, ClientSize.Height)));
        }
    }

    [SuppressMessage("Major Code Smell", "S3776", Justification = "Render loop is intentionally branchy for speed.")]
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.BgData);
        using (var p = new Pen(Theme.Line))
        {
            g.DrawLine(p, 0, 0, 0, ClientSize.Height);
        }
        if (Res == null || Res.Density == null || Res.View.Count == 0)
        {
            return;
        }
        int h = ClientSize.Height, w = ClientSize.Width;
        // Discrete 2px ticks on a 3px pitch: a continuous stripe of red reads as a
        // solid wall on any file where errors are spread out, and hides clusters.
        var pitch = Math.Max(2, Dpi.S(3));
        var tick = Math.Max(1, pitch - 1);
        var inner = w - Dpi.S(3);
        for (var y = 0; y < h; y += pitch)
        {
            var a = (int)((long)y * FilterResult.Slots / Math.Max(1, h));
            var b = (int)((long)(y + pitch) * FilterResult.Slots / Math.Max(1, h));
            if (b <= a)
            {
                b = a + 1;
            }
            var best = 0;
            for (var k = a; k < b && k < FilterResult.Slots; k++)
            {
                if (Res.Density[k] > best)
                {
                    best = Res.Density[k];
                }
            }
            Color c; int wdt;
            if (best == 3) { c = Theme.SevColor[6]; wdt = inner; }
            else if (best == 2) { c = Theme.SevColor[5]; wdt = inner * 2 / 3; }
            else if (best == 1) { c = Theme.SevColor[4]; wdt = inner / 2; }
            else { c = Theme.HistInfo; wdt = inner / 3; }
            using (var br = new SolidBrush(c))
            {
                g.FillRectangle(br, Dpi.S(2), y, Math.Max(1, wdt), tick);
            }
        }
        DrawHits(g, w, h);
        if (View != null && Res.View.Count > 0)
        {
            var f0 = (float)View.TopRow / Res.View.Count;
            var f1 = (float)(View.TopRow + View.VisibleRows) / Res.View.Count;
            int y0 = (int)(f0 * h), y1 = Math.Max(y0 + 3, (int)(f1 * h));
            using (var b = new SolidBrush(Theme.AccentFaint))
            {
                g.FillRectangle(b, 0, y0, w, y1 - y0);
            }
            using (var p = new Pen(Theme.AccentEdge))
            {
                g.DrawLine(p, 0, y0, w, y0); g.DrawLine(p, 0, y1, w, y1);
            }
        }

        if (File != null && File.Marks.Count > 0 && Res != null && Res.View.Count > 0)
        {
            using (var b = new SolidBrush(Theme.MatchText))
            {
                var viewCount = Res.View.Count;
                foreach (var markLine in File.Marks)
                {
                    var pos = BinarySearch(Res.View, markLine);
                    if (pos < 0)
                    {
                        continue;
                    }
                    var y = (int)((float)pos / viewCount * h);
                    g.FillRectangle(b, 0, Math.Clamp(y, 0, h - 2), w, 2);
                }
            }
        }
    }

    /// <summary>
    /// Search hits as ticks down the right edge, apart from the severity bars on the
    /// left, so a handful of matches in a long log can be seen and clicked to at a
    /// glance — the way errors already are.
    /// </summary>
    private void DrawHits(Graphics g, int w, int h)
    {
        var hits = Res.HitSlots;
        if (hits == null)
        {
            return;
        }
        var tickW = Math.Max(2, Dpi.S(3));
        var tickH = Math.Max(2, Dpi.S(2));
        using var b = new SolidBrush(Theme.MatchText);
        var lastY = int.MinValue;
        for (var k = 0; k < hits.Length; k++)
        {
            if (!hits[k])
            {
                continue;
            }
            var y = Math.Clamp((int)((long)k * h / FilterResult.Slots), 0, h - tickH);
            if (y == lastY)
            {
                continue;
            }
            lastY = y;
            g.FillRectangle(b, w - tickW, y, tickW, tickH);
        }
    }

    private static int BinarySearch(List<int> view, int target)
    {
        int lo = 0, hi = view.Count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (view[mid] == target)
            {
                return mid;
            }
            if (view[mid] < target)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return -1;
    }
}
