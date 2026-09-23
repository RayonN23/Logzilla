using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Windows.Forms;

namespace Logzilla.App.Infrastructure;

/// <summary>
/// The application icon, shared by the window and the dialogs, so every place the
/// product identifies itself shows the same artwork. <see cref="Mark"/> renders it
/// as a bitmap at an exact size; nothing in the chrome draws it today (the title bar
/// is the wordmark alone), but the dialogs and any future mark can ask for one.
/// </summary>
internal static class AppImages
{
    /// <summary>The frame sizes inside assets/Logzilla.ico.</summary>
    private static readonly int[] Frames = [16, 32, 48, 64, 128, 256];

    private static readonly Dictionary<int, Bitmap> MarkCache = new();

    /// <summary>
    /// The embedded icon. Preferred over the shell's copy: it does not depend on the
    /// .exe still sitting where it was launched from.
    /// </summary>
    public static Icon Load()
    {
        try
        {
            using var st = Assembly.GetExecutingAssembly().GetManifestResourceStream("appicon");
            if (st != null)
            {
                return new Icon(st);
            }
        }
        catch (Exception ex) { Debug.WriteLine("Embedded icon load: " + ex.Message); }
        try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
        catch (Exception ex) { Debug.WriteLine("Shell icon extraction: " + ex.Message); }
        return null;
    }

    /// <summary>
    /// The icon rendered as a bitmap at an exact pixel size, cached per size.
    /// Returns null only when the icon cannot be read at all, in which case the
    /// caller is expected to draw nothing rather than a placeholder.
    /// </summary>
    public static Bitmap Mark(int px)
    {
        px = Math.Max(1, px);
        if (MarkCache.TryGetValue(px, out var cached))
        {
            return cached;
        }
        var bmp = Render(px);
        if (bmp != null)
        {
            MarkCache[px] = bmp;
        }
        return bmp;
    }

    /// <summary>
    /// The smallest frame at least as large as the target, so the icon is always
    /// downscaled rather than blown up — a 16px frame stretched to 30px at 150%
    /// display scaling is what makes a title-bar icon unreadable.
    /// </summary>
    public static int BestFrame(int px)
    {
        foreach (var f in Frames)
        {
            if (f >= px)
            {
                return f;
            }
        }
        return Frames[^1];
    }

    private static Bitmap Render(int px)
    {
        try
        {
            using var st = Assembly.GetExecutingAssembly().GetManifestResourceStream("appicon");
            if (st == null)
            {
                return null;
            }
            var frame = BestFrame(px);
            using var icon = new Icon(st, new Size(frame, frame));
            using var src = icon.ToBitmap();
            var bmp = new Bitmap(px, px);
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.DrawImage(src, new Rectangle(0, 0, px, px));
            }
            return bmp;
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Brand mark render: " + ex.Message);
            return null;
        }
    }
}
