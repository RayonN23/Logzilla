using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.Windows.Forms;

namespace Logzilla.App.Infrastructure;

/// <summary>
/// Single source of truth for every colour, font and metric in the Logzilla shell.
/// Nothing outside this file should construct a Color, a Font, or a layout constant.
///
/// Metrics are expressed at 96 DPI; scale them with Dpi.S(...) at layout/paint time.
/// Fonts are built once, from Dpi.Factor, by InitFonts() — call it right after
/// Dpi.Init(form) and before the UI is built.
/// </summary>
internal static class Theme
{
    // ─────────────────────────────────────────────────────────────────────
    //  Surfaces. The log list is the DARKEST plane on purpose: data recedes,
    //  chrome lifts.
    // ─────────────────────────────────────────────────────────────────────
    public static Color BgData      { get; private set; } = Hex(0x0E1114);
    public static Color BgChrome    { get; private set; } = Hex(0x12161B);
    public static Color BgChromeLow { get; private set; } = Hex(0x10141A);
    public static Color BgRaised    { get; private set; } = Hex(0x1A212B);
    public static Color BgControl   { get; private set; } = Hex(0x232B36);
    public static Color BgChipOn    { get; private set; } = Hex(0x1D2530);
    public static Color BgRowHover  { get; private set; } = Hex(0x161B22);

    // ─────────────────────────────────────────────────────────────────────
    //  Lines
    // ─────────────────────────────────────────────────────────────────────
    public static Color Line              { get; private set; } = Hex(0x1C222B);
    public static Color LineSoft          { get; private set; } = Hex(0x1A1F27);
    public static Color LineControl       { get; private set; } = Hex(0x222932);
    public static Color LineControlStrong { get; private set; } = Hex(0x2E3744);
    public static Color LineLeader        { get; private set; } = Hex(0x1B212A);

    // ─────────────────────────────────────────────────────────────────────
    //  Text. TextDim (#7A8390, 4.9:1 on BgData) is the contrast floor — nothing
    //  dimmer is used for text a user needs to read.
    // ─────────────────────────────────────────────────────────────────────
    public static Color Text       { get; private set; } = Hex(0xE4E8EE);
    public static Color Text2      { get; private set; } = Hex(0xA3ABB7);
    public static Color Text3      { get; private set; } = Hex(0x8D96A3);
    public static Color TextDim    { get; private set; } = Hex(0x7A8390);
    public static Color TextPrefix { get; private set; } = Hex(0x79828F);
    public static Color TextBright { get; private set; } = Hex(0xB6BFCB);

    // ─────────────────────────────────────────────────────────────────────
    //  Severity. Indexed like Logzilla.Core.Common.Levels: 0=none 1=TRACE
    //  2=DEBUG 3=INFO 4=WARN 5=ERROR 6=FATAL. INFO/none are neutral on
    //  purpose — they are ~97% of a file and must not shout.
    // ─────────────────────────────────────────────────────────────────────
    public static readonly Color[] SevColor =
    [
        Hex(0x7A8390), // 0 none    (dim, same family as trace)
        Hex(0x7E8899), // 1 trace
        Hex(0xA99BF5), // 2 debug
        Hex(0x8B95A3), // 3 info    (neutral)
        Hex(0xF0B341), // 4 warn
        Hex(0xFF6F6B), // 5 error
        Hex(0xFF5C8A), // 6 fatal
    ];

    public static readonly Color[] MsgColor =
    [
        Hex(0xC8CFD9), // 0 none
        Hex(0xB2BAC6), // 1 trace
        Hex(0xCFC7FA), // 2 debug
        Hex(0xC8CFD9), // 3 info
        Hex(0xF5C778), // 4 warn
        Hex(0xFFB3AF), // 5 error
        Hex(0xFFB3C6), // 6 fatal
    ];

    /// <summary>Histogram fill for INFO/none. A fill, not text — contrast rules do not apply.</summary>
    public static Color HistInfo { get; private set; } = Hex(0x333C49);

    /// <summary>INFO (3) and none (0) draw no coloured spine and no coloured level tag.</summary>
    public static bool IsNeutral(int sev) => sev is 0 or 3;

    // ─────────────────────────────────────────────────────────────────────
    //  Accent — selection, focus, viewport position, brand. NEVER severity.
    // ─────────────────────────────────────────────────────────────────────
    public static Color Accent      { get; private set; } = Hex(0x56C8DE);
    public static Color AccentGlow  { get; private set; } = Alpha(Hex(0x56C8DE), 0.10);
    public static Color AccentFaint { get; private set; } = Alpha(Hex(0x56C8DE), 0.12);
    public static Color AccentDim   { get; private set; } = Alpha(Hex(0x56C8DE), 0.42);
    public static Color AccentEdge  { get; private set; } = Alpha(Hex(0x56C8DE), 0.65);

    // ─────────────────────────────────────────────────────────────────────
    //  Search match
    // ─────────────────────────────────────────────────────────────────────
    public static Color MatchBg      { get; private set; } = Alpha(Hex(0xF0B341), 0.22);
    public static Color MatchText    { get; private set; } = Hex(0xF6D48F);
    public static Color MatchCurBg   { get; private set; } = Alpha(Hex(0xF0B341), 0.85);
    public static Color MatchCurText { get; private set; } = Hex(0x11151A);

    public static Color Ok { get; private set; } = Hex(0x8FD694); // live-tail indicator

    /// <summary>When Windows is in a high-contrast scheme, the user chose those
    /// colours for a reason; system colours replace the dark palette so text
    /// stays legible and the UI respects the setting.</summary>
    public static void ApplySystemPreferences()
    {
        if (!SystemInformation.HighContrast)
        {
            return;
        }
        BgData = BgChrome = BgChromeLow = SystemColors.Window;
        BgRaised = BgControl = BgChipOn = BgRowHover = SystemColors.Control;
        Line = LineSoft = LineControl = LineControlStrong = LineLeader = SystemColors.ControlDark;
        Text = Text2 = Text3 = TextBright = SystemColors.WindowText;
        TextDim = TextPrefix = SystemColors.GrayText;
        Accent = AccentEdge = SystemColors.Highlight;
        AccentGlow = AccentFaint = AccentDim = Alpha(SystemColors.Highlight, 0.35);
        HistInfo = SystemColors.ControlDark;
        Ok = SystemColors.HotTrack;
        for (var i = 0; i < SevColor.Length; i++)
        {
            SevColor[i] = i >= 4 ? SystemColors.HotTrack : SystemColors.GrayText;
            MsgColor[i] = SystemColors.WindowText;
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Fonts. GraphicsUnit.Pixel so the px values in the design brief transfer
    //  1:1 once scaled by Dpi.Factor. IBM Plex (the browser mockup) is not
    //  installed on Windows; these families ship with it. Sizes/weights are
    //  the real spec, the family names are not.
    // ─────────────────────────────────────────────────────────────────────
    private static readonly string MonoFamily = FirstAvailable("Cascadia Mono", "Consolas", "Courier New");
    private static readonly string SansFamily = FirstAvailable("Segoe UI Variable Text", "Segoe UI", "Tahoma");

    /// <summary>Log body, panel values. 11.5px on a 20px row.</summary>
    public static Font MonoBody   { get; private set; }
    /// <summary>Line numbers, status bar, axis labels.</summary>
    public static Font MonoSmall  { get; private set; }
    public static Font MonoTiny   { get; private set; }
    /// <summary>Controls and buttons.</summary>
    public static Font UiBody     { get; private set; }
    /// <summary>Active segment, wordmark weight.</summary>
    public static Font UiBodyBold { get; private set; }
    /// <summary>Section heads. Draw UPPERCASE with tracking (see TextUtil.Tracked).</summary>
    public static Font UiHead     { get; private set; }
    /// <summary>Column heads and the severity tag in a row. UPPERCASE, tracked.</summary>
    public static Font UiTag      { get; private set; }
    public static Font Wordmark   { get; private set; }

    private static bool FontsReady;

    /// <summary>Builds every named font at the current Dpi.Factor. Call once, after
    /// Dpi.Init(form), before BuildUi(). Safe to call again after a scale change.</summary>
    public static void InitFonts()
    {
        DisposeFonts();
        var f = Math.Max(0.5f, Dpi.Factor);
        MonoBody   = new Font(MonoFamily, 11.5f * f, FontStyle.Regular, GraphicsUnit.Pixel);
        MonoSmall  = new Font(MonoFamily, 10.5f * f, FontStyle.Regular, GraphicsUnit.Pixel);
        MonoTiny   = new Font(MonoFamily,  9.5f * f, FontStyle.Regular, GraphicsUnit.Pixel);
        UiBody     = new Font(SansFamily, 11.5f * f, FontStyle.Regular, GraphicsUnit.Pixel);
        UiBodyBold = new Font(SansFamily, 11.5f * f, FontStyle.Bold,    GraphicsUnit.Pixel);
        UiHead     = new Font(SansFamily, 10f   * f, FontStyle.Bold,    GraphicsUnit.Pixel);
        UiTag      = new Font(SansFamily,  9.5f * f, FontStyle.Bold,    GraphicsUnit.Pixel);
        Wordmark   = new Font(SansFamily, 12.5f * f, FontStyle.Bold,    GraphicsUnit.Pixel);
        FontsReady = true;
    }

    private static void DisposeFonts()
    {
        if (!FontsReady)
        {
            return;
        }
        MonoBody?.Dispose(); MonoSmall?.Dispose(); MonoTiny?.Dispose();
        UiBody?.Dispose(); UiBodyBold?.Dispose(); UiHead?.Dispose(); UiTag?.Dispose(); Wordmark?.Dispose();
    }

    /// <summary>
    /// GDI (TextRenderer), not GDI+ (Graphics.DrawString). GDI+ ClearType on a dark
    /// ground reads muddy and is far slower across thousands of short runs.
    /// </summary>
    public const TextFormatFlags TextFlags =
        TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix |
        TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter;

    public const TextFormatFlags TextFlagsEllipsis = TextFlags | TextFormatFlags.EndEllipsis;
    public const TextFormatFlags TextFlagsRight    = TextFlags | TextFormatFlags.Right;
    public const TextFormatFlags TextFlagsCenter   = TextFlags | TextFormatFlags.HorizontalCenter;

    /// <summary>
    /// For the Point overload of TextRenderer.DrawText, where the caller has already
    /// computed the exact top-left Y itself. TextFlags' VerticalCenter fights that:
    /// with no explicit bounding rectangle, GDI still centers against an internal
    /// layout box, which can shift a whole line height off from the intended spot.
    /// </summary>
    public const TextFormatFlags TextFlagsPoint = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

    // ─────────────────────────────────────────────────────────────────────
    //  Metrics — all at 96 DPI. Scale with Dpi.S(...) at layout time.
    // ─────────────────────────────────────────────────────────────────────
    public const int TitleBarHeight   = 40;
    public const int CommandBarHeight = 52;
    public const int FilterBarHeight  = 44;
    public const int TimelineHeight   = 96;
    public const int StatusBarHeight  = 28;
    public const int InspectorWidth   = 380;

    public const int RowHeight        = 20;
    public const int ColumnHeadHeight = 26;
    public const int DateBandHeight   = 20;

    // Log row columns. Fixed, never auto — the point is that a blank INFO level
    // tag never shifts the message column.
    public const int ColGutter         = 46;  // line number, right-aligned
    public const int ColSpine          = 3;   // severity spine, transparent for INFO
    public const int ColTime           = 100;
    public const int ColLevel          = 46;
    public const int ColMessageRightPad = 16;
    public const int MinimapWidth      = 11;

    public const int TimelineLabelHeight = 16;
    public const int TimelineChartHeight = 48;
    public const int TimelineAxisHeight  = 16;

    public const int RadiusSmall  = 4;
    public const int RadiusChip   = 6;
    public const int RadiusField  = 7;
    public const int RadiusWindow = 10;

    public const int ChipHeight    = 26;
    public const int ControlHeight = 30;
    public const int FieldHeight   = 34;

    // ─────────────────────────────────────────────────────────────────────
    //  Helpers
    // ─────────────────────────────────────────────────────────────────────
    private static Color Hex(int rgb) =>
        Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);

    private static Color Alpha(Color c, double a) =>
        Color.FromArgb((int)Math.Round(a * 255), c.R, c.G, c.B);

    private static HashSet<string> InstalledFamiliesHashSet;

    /// <summary>Installed family names, read once. Enumerating every font on the
    /// machine is slow enough to notice at startup, and both families are looked up.</summary>
    private static HashSet<string> InstalledFamilies()
    {
        if (InstalledFamiliesHashSet != null)
        {
            return InstalledFamiliesHashSet;
        }
        var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var collection = new InstalledFontCollection())
        {
            foreach (var family in collection.Families)
            {
                installed.Add(family.Name);
                family.Dispose();
            }
        }
        InstalledFamiliesHashSet = installed;
        return installed;
    }

    private static string FirstAvailable(params string[] families)
    {
        var installed = InstalledFamilies();
        foreach (var name in families)
        {
            if (installed.Contains(name))
            {
                return name;
            }
        }
        return FontFamily.GenericMonospace.Name;
    }
}
