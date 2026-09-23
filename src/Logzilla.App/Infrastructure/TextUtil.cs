using System;
using System.Drawing;
using System.Windows.Forms;

namespace Logzilla.App.Infrastructure;

/// <summary>
/// Small drawing helpers shared by the owner-drawn controls. Kept out of Theme.cs
/// because Theme only holds tokens, not behaviour.
/// </summary>
internal static class TextUtil
{
    /// <summary>
    /// Draws upper-cased text with extra letter-spacing (section heads, column heads,
    /// the severity tag). TextRenderer has no tracking parameter, so each glyph is
    /// measured and placed by hand. Not for the log-row hot path.
    /// </summary>
    private const TextFormatFlags PointFlags = Theme.TextFlagsPoint;

    public static void DrawTracked(Graphics g, string text, Font font, Point pt, Color color, float trackingPx)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        var upper = text.ToUpperInvariant();
        var x = pt.X;
        foreach (var ch in upper)
        {
            var s = ch.ToString();
            TextRenderer.DrawText(g, s, font, new Point(x, pt.Y), color, PointFlags);
            x += TextRenderer.MeasureText(g, s, font, new Size(short.MaxValue, short.MaxValue), PointFlags).Width;
            x += (int)Math.Round(trackingPx);
        }
    }

    public static int MeasureTracked(Graphics g, string text, Font font, float trackingPx)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }
        var upper = text.ToUpperInvariant();
        var w = 0;
        foreach (var ch in upper)
        {
            w += TextRenderer.MeasureText(g, ch.ToString(), font, new Size(short.MaxValue, short.MaxValue), PointFlags).Width;
            w += (int)Math.Round(trackingPx);
        }
        return w;
    }

    /// <summary>Rounded rectangle path used by chips, buttons and the search field.</summary>
    public static System.Drawing.Drawing2D.GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        if (radius <= 0)
        {
            path.AddRectangle(r);
            return path;
        }
        var d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
