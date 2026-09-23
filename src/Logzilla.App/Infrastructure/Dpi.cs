using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace Logzilla.App.Infrastructure;


// Everything on screen is sized from measured text rather than fixed pixels.
// That is what keeps the toolbar readable at 150%% and 200%% display scaling,
// and it also makes Ctrl+plus resize the chrome along with the log.
internal static class Dpi
{
    public static float Factor { get; private set; } = 1f;      // spacing and box sizes
    public static float FontFactor { get; private set; } = 1f;  // point sizes, when the user overrides

    public static void Init(Control c)
    {
        try { using (var g = c.CreateGraphics()) 
{
    Factor = g.DpiX / 96f;
} }
        catch (Exception ex)
        {
            Debug.WriteLine("Read system DPI: " + ex.Message);
            Factor = 1f;
        }
        if (Factor < 0.5f || Factor > 6f) 
        {
            Factor = 1f;
        }

        // Escape hatch for machines that misreport their DPI, and for anyone
        // who simply wants bigger controls:  set LOGZILLA_SCALE=1.5
        try
        {
            var v = Environment.GetEnvironmentVariable("LOGZILLA_SCALE");
            float f;
            if (!string.IsNullOrEmpty(v) &&
                float.TryParse(v, NumberStyles.Float,
                               CultureInfo.InvariantCulture, out f) && f >= 0.5f && f <= 4f)
            { Factor = f; FontFactor = f; }
        }
        catch (Exception ex) { Debug.WriteLine("Parse LOGZILLA_SCALE: " + ex.Message); }
    }

    public static int S(int px) { return (int)Math.Round(px * Factor); }
    public static float S(float px) { return px * Factor; }

    public static Size Text(string t, Font f)
    {
        return TextRenderer.MeasureText(t ?? "", f, new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.NoPadding);
    }
}
