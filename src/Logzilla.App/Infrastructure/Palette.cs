using System.Drawing;

namespace Logzilla.App.Infrastructure;

internal static class Palette
{
    private static readonly Color[] SourceTints = [
        Color.FromArgb(143, 122, 230), Color.FromArgb(63, 191, 178), Color.FromArgb(224, 169, 46),
        Color.FromArgb(239, 120, 160), Color.FromArgb(120, 180, 240), Color.FromArgb(160, 200, 110),
        Color.FromArgb(230, 140, 90),  Color.FromArgb(190, 150, 220), Color.FromArgb(110, 200, 210),
        Color.FromArgb(214, 196, 120), Color.FromArgb(180, 140, 120), Color.FromArgb(150, 170, 200)
    ];

    /// <summary>Colour for a merged source, wrapping when there are more sources than tints.</summary>
    public static Color Pick(int index) =>
        SourceTints[((index % SourceTints.Length) + SourceTints.Length) % SourceTints.Length];
}
