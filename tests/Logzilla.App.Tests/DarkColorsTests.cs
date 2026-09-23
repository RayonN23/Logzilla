using Logzilla.App.Controls;
using Logzilla.App.Infrastructure;
using NUnit.Framework;

namespace Logzilla.App.Tests;

/// <summary>Each override must read the token its name implies, not a look-alike one.</summary>
[TestFixture]
public sealed class DarkColorsTests
{
    private readonly DarkColors _t = new();

    [Test]
    public void MenuSurfacesUseChromeTokens()
    {
        Assert.That(_t.ToolStripDropDownBackground, Is.EqualTo(Theme.BgChrome));
        Assert.That(_t.ImageMarginGradientBegin, Is.EqualTo(Theme.BgChrome));
        Assert.That(_t.ImageMarginGradientMiddle, Is.EqualTo(Theme.BgChrome));
        Assert.That(_t.ImageMarginGradientEnd, Is.EqualTo(Theme.BgChrome));
        Assert.That(_t.MenuStripGradientBegin, Is.EqualTo(Theme.BgChrome));
        Assert.That(_t.MenuStripGradientEnd, Is.EqualTo(Theme.BgChrome));
        Assert.That(_t.MenuItemPressedGradientBegin, Is.EqualTo(Theme.BgChrome));
        Assert.That(_t.MenuItemPressedGradientEnd, Is.EqualTo(Theme.BgChrome));
    }

    [Test]
    public void SelectedMenuItemsUseTheRaisedToken()
    {
        Assert.That(_t.MenuItemSelected, Is.EqualTo(Theme.BgRaised));
        Assert.That(_t.MenuItemSelectedGradientBegin, Is.EqualTo(Theme.BgRaised));
        Assert.That(_t.MenuItemSelectedGradientEnd, Is.EqualTo(Theme.BgRaised));
    }

    [Test]
    public void BordersUseLineTokens()
    {
        Assert.That(_t.MenuItemBorder, Is.EqualTo(Theme.LineControlStrong));
        Assert.That(_t.MenuBorder, Is.EqualTo(Theme.LineControlStrong));
        Assert.That(_t.SeparatorLight, Is.EqualTo(Theme.LineControlStrong));
        Assert.That(_t.SeparatorDark, Is.EqualTo(Theme.Line));
        Assert.That(_t.ToolStripBorder, Is.EqualTo(Theme.Line));
    }
}
