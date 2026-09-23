using System.Drawing;
using Logzilla.App.Infrastructure;

namespace Logzilla.App.Controls;

internal sealed class PanelRow
{
    public string Left = "", Right = "", Unit = "";
    public Color Accent = Theme.Text;
    public bool Header;
    /// <summary>Left text is a UI label (sans, Text2) rather than log data (mono).</summary>
    public bool UiLabel;
    public int Indent;

    /// <summary>0..1 fraction, already scaled by the builder (e.g. log scale for
    /// severity counts). -1 = no bar/track drawn at all.</summary>
    public float Bar = -1;

    /// <summary>True renders the track outline only, no fill — the "excluded" state.</summary>
    public bool BarOutline;

    /// <summary>null = no dot. true = filled (included). false = hollow (excluded).</summary>
    public bool? Dot;

    /// <summary>Draws a trailing chevron affordance ("busiest error windows" rows).</summary>
    public bool Chevron;

    public object Tag;

    public PanelRow()
    {
    }

    public PanelRow(string l, string r)
    {
        Left = l;
        Right = r;
    }
}
