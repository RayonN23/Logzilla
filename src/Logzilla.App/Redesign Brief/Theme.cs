using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.Windows.Forms;

namespace Logzilla.UI;

/// <summary>
/// Single source of truth for every colour, font and metric in the Logzilla shell.
/// Nothing outside this file should construct a Color, a Font, or a layout constant.
///
/// All metrics are expressed at 96 DPI. Scale them at paint time with Scale(value, DeviceDpi).
/// Never bake a scaled value into a field.
/// </summary>
internal static class Theme
{
    // ─────────────────────────────────────────────────────────────────────
    //  Surfaces
    //  The log list is the DARKEST plane on purpose: data recedes, chrome lifts.
    // ─────────────────────────────────────────────────────────────────────
    public static readonly Color BgData      = Hex(0x0E1114); // log list
    public static readonly Color BgChrome    = Hex(0x12161B); // title bar, command bar, inspector
    public static readonly Color BgChromeLow = Hex(0x10141A); // filter bar, timeline, status bar, minimap
    public static readonly Color BgRaised    = Hex(0x1A212B); // selected row, tooltips, popovers
    public static readonly Color BgControl   = Hex(0x232B36); // active segment in a segmented control
    public static readonly Color BgChipOn    = Hex(0x1D2530); // enabled filter chip
    public static readonly Color BgRowHover  = Hex(0x161B22); // row hover

    // ─────────────────────────────────────────────────────────────────────
    //  Lines
    // ─────────────────────────────────────────────────────────────────────
    public static readonly Color Line              = Hex(0x1C222B); // between major regions
    public static readonly Color LineSoft          = Hex(0x1A1F27); // row separators, inside a pane
    public static readonly Color LineControl       = Hex(0x222932); // default control border
    public static readonly Color LineControlStrong = Hex(0x2E3744); // active / raised control border
    public static readonly Color LineLeader        = Hex(0x1B212A); // dot-leader hairline in the inspector

    // ─────────────────────────────────────────────────────────────────────
    //  Text
    //  TextDim is the contrast floor (4.9:1 on BgData). Nothing dimmer, ever.
    // ─────────────────────────────────────────────────────────────────────
    public static readonly Color Text       = Hex(0xE4E8EE); // primary values, active labels
    public static readonly Color Text2      = Hex(0xA3ABB7); // control labels
    public static readonly Color Text3      = Hex(0x8D96A3); // timestamps, secondary values
    public static readonly Color TextDim    = Hex(0x7A8390); // line numbers, INFO tag, section heads
    public static readonly Color TextPrefix = Hex(0x79828F); // dimmed logger prefix inside a message
    public static readonly Color TextBright = Hex(0xB6BFCB); // line number / timestamp on a non-INFO row

    // ─────────────────────────────────────────────────────────────────────
    //  Severity
    //  Info is deliberately neutral. It is ~97% of a file; it must not shout.
    // ─────────────────────────────────────────────────────────────────────
    public static readonly Color SevFatal = Hex(0xFF5C8A);
    public static readonly Color SevError = Hex(0xFF6F6B);
    public static readonly Color SevWarn  = Hex(0xF0B341);
    public static readonly Color SevInfo  = Hex(0x8B95A3);
    public static readonly Color SevDebug = Hex(0xA99BF5);
    public static readonly Color SevTrace = Hex(0x7E8899);

    public static readonly Color MsgFatal = Hex(0xFFB3C6);
    public static readonly Color MsgError = Hex(0xFFB3AF);
    public static readonly Color MsgWarn  = Hex(0xF5C778);
    public static readonly Color MsgInfo  = Hex(0xC8CFD9);
    public static readonly Color MsgDebug = Hex(0xCFC7FA);
    public static readonly Color MsgTrace = Hex(0xB2BAC6);

    /// <summary>Histogram fill for INFO. A fill, not text — contrast rules do not apply.</summary>
    public static readonly Color HistInfo = Hex(0x333C49);

    // ─────────────────────────────────────────────────────────────────────
    //  Accent — selection, focus, viewport position, brand. NEVER severity.
    // ─────────────────────────────────────────────────────────────────────
    public static readonly Color Accent      = Hex(0x56C8DE);
    public static readonly Color AccentGlow  = Alpha(Hex(0x56C8DE), 0.10); // focus ring
    public static readonly Color AccentFaint = Alpha(Hex(0x56C8DE), 0.12); // viewport band fill, badges
    public static readonly Color AccentDim   = Alpha(Hex(0x56C8DE), 0.42); // focused field border
    public static readonly Color AccentEdge  = Alpha(Hex(0x56C8DE), 0.65); // selected row edge, crosshair

    // ─────────────────────────────────────────────────────────────────────
    //  Search match
    // ─────────────────────────────────────────────────────────────────────
    public static readonly Color MatchBg       = Alpha(Hex(0xF0B341), 0.22);
    public static readonly Color MatchText     = Hex(0xF6D48F);
    public static readonly Color MatchCurBg    = Alpha(Hex(0xF0B341), 0.85);
    public static readonly Color MatchCurText  = Hex(0x11151A);

    public static readonly Color Ok = Hex(0x8FD694); // live-tail indicator

    // ─────────────────────────────────────────────────────────────────────
    //  Fonts
    //  GraphicsUnit.Pixel so the px values in the design brief transfer 1:1.
    //  IBM Plex (used in the browser mockup) is not installed on Windows and
    //  PrivateFontCollection does not resolve reliably under GDI/TextRenderer.
    //  These families ship with Windows. Sizes and weights are the real spec.
    // ─────────────────────────────────────────────────────────────────────
    private static readonly string MonoFamily = FirstAvailable("Cascadia Mono", "Consolas", "Courier New");
    private static readonly string SansFamily = FirstAvailable("Segoe UI Variable Text", "Segoe UI", "Tahoma");

    /// <summary>Log body, panel values, line numbers. 11.5px on a 20px row.</summary>
    public static readonly Font MonoBody    = new(MonoFamily, 11.5f, FontStyle.Regular, GraphicsUnit.Pixel);
    /// <summary>Line numbers, status bar, axis labels.</summary>
    public static readonly Font MonoSmall   = new(MonoFamily, 10.5f, FontStyle.Regular, GraphicsUnit.Pixel);
    public static readonly Font MonoTiny    = new(MonoFamily,  9.5f, FontStyle.Regular, GraphicsUnit.Pixel);

    /// <summary>Buttons, chips, control labels.</summary>
    public static readonly Font UiBody      = new(SansFamily, 11.5f, FontStyle.Regular, GraphicsUnit.Pixel);
    /// <summary>Active segment, wordmark.</summary>
    public static readonly Font UiBodyBold  = new(SansFamily, 11.5f, FontStyle.Bold,    GraphicsUnit.Pixel);
    /// <summary>Section heads. Draw UPPERCASE with ~0.12em tracking (see TrackedText).</summary>
    public static readonly Font UiHead      = new(SansFamily, 10f,   FontStyle.Bold,    GraphicsUnit.Pixel);
    /// <summary>Column heads and the severity tag in a row. UPPERCASE, ~0.06–0.1em tracking.</summary>
    public static readonly Font UiTag       = new(SansFamily,  9.5f, FontStyle.Bold,    GraphicsUnit.Pixel);
    public static readonly Font Wordmark    = new(SansFamily, 12.5f, FontStyle.Bold,    GraphicsUnit.Pixel);

    /// <summary>
    /// Use GDI (TextRenderer), not GDI+ (Graphics.DrawString). GDI+ ClearType on a dark
    /// ground reads muddy and is far slower across thousands of short runs.
    /// </summary>
    public const TextFormatFlags TextFlags =
        TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix |
        TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter;

    public const TextFormatFlags TextFlagsEllipsis = TextFlags | TextFormatFlags.EndEllipsis;
    public const TextFormatFlags TextFlagsRight    = TextFlags | TextFormatFlags.Right;

    // ─────────────────────────────────────────────────────────────────────
    //  Metrics — all at 96 DPI
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

    // Log row columns. Fixed, never Auto — the whole point is that they do not shift
    // when a level tag is blank.
    public const int ColGutter   = 46;  // line number, right-aligned, 10px right padding
    public const int ColSpine    = 3;   // severity spine, transparent for INFO
    public const int ColTime     = 100; // 10px left padding
    public const int ColLevel    = 46;
    public const int ColMessageRightPad = 16;
    public const int MinimapWidth = 11;

    public const int TimelineChartHeight = 48;
    public const int TimelineLabelHeight = 16;
    public const int TimelineAxisHeight  = 16;

    public const int RadiusSmall  = 4;  // inner controls
    public const int RadiusChip   = 6;  // chips, buttons
    public const int RadiusField  = 7;  // input fields
    public const int RadiusWindow = 10;

    public const int ChipHeight    = 26;
    public const int ControlHeight = 30;
    public const int FieldHeight   = 34;

    // ─────────────────────────────────────────────────────────────────────
    //  Helpers
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Scale a 96-DPI metric for the control currently being painted.</summary>
    public static int Scale(int value, int deviceDpi) =>
        (int)Math.Round(value * deviceDpi / 96.0);

    public static float Scale(float value, int deviceDpi) =>
        value * deviceDpi / 96f;

    /// <summary>Palette for one severity: spine/tag colour and message colour.</summary>
    public static (Color Tag, Color Message) Palette(Severity level) => level switch
    {
        Severity.Fatal => (SevFatal, MsgFatal),
        Severity.Error => (SevError, MsgError),
        Severity.Warn  => (SevWarn,  MsgWarn),
        Severity.Debug => (SevDebug, MsgDebug),
        Severity.Trace => (SevTrace, MsgTrace),
        _              => (TextDim,  MsgInfo),   // Info: neutral tag, no spine
    };

    /// <summary>INFO draws no spine. Everything else does.</summary>
    public static bool HasSpine(Severity level) => level != Severity.Info;

    private static Color Hex(int rgb) =>
        Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);

    private static Color Alpha(Color c, double a) =>
        Color.FromArgb((int)Math.Round(a * 255), c.R, c.G, c.B);

    private static string FirstAvailable(params string[] families)
    {
        var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var collection = new InstalledFontCollection())
        {
            foreach (var family in collection.Families)
                installed.Add(family.Name);
        }

        foreach (var name in families)
            if (installed.Contains(name))
                return name;

        return FontFamily.GenericMonospace.Name;
    }
}

internal enum Severity
{
    Trace,
    Debug,
    Info,
    Warn,
    Error,
    Fatal,
}
