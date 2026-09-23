using System.Drawing;
using System.Windows.Forms;
using Logzilla.App.Infrastructure;

namespace Logzilla.App.Controls;

internal sealed class DarkColors : ProfessionalColorTable
{
    public override Color MenuItemSelected => Theme.BgRaised;
    public override Color MenuItemBorder => Theme.LineControlStrong;
    public override Color MenuBorder => Theme.LineControlStrong;
    public override Color ToolStripDropDownBackground => Theme.BgChrome;
    public override Color ImageMarginGradientBegin => Theme.BgChrome;
    public override Color ImageMarginGradientMiddle => Theme.BgChrome;
    public override Color ImageMarginGradientEnd => Theme.BgChrome;
    public override Color MenuItemSelectedGradientBegin => Theme.BgRaised;
    public override Color MenuItemSelectedGradientEnd => Theme.BgRaised;
    public override Color MenuItemPressedGradientBegin => Theme.BgChrome;
    public override Color MenuItemPressedGradientEnd => Theme.BgChrome;
    public override Color SeparatorDark => Theme.Line;
    public override Color SeparatorLight => Theme.LineControlStrong;
    public override Color MenuStripGradientBegin => Theme.BgChrome;
    public override Color MenuStripGradientEnd => Theme.BgChrome;
    public override Color ToolStripBorder => Theme.Line;
}
