using Logzilla.App.Controls;
using Logzilla.App.Infrastructure;
using NUnit.Framework;

namespace Logzilla.App.Tests;

[TestFixture]
public sealed class FlatButtonTests
{
    [Test]
    public void HeightIsTheRequestedHeightScaledByDpi()
    {
        var btn = new FlatButton { Text = "OK" };
        btn.FitTo(50, 30);
        Assert.That(btn.Height, Is.EqualTo(Dpi.S(30)));
    }

    [Test]
    public void MinWidthFloorWinsWhenTextIsNarrow()
    {
        var btn = new FlatButton { Text = "." };
        btn.FitTo(500, 30);
        Assert.That(btn.Width, Is.EqualTo(Dpi.S(500)));
    }

    [Test]
    public void LongerTextProducesAWiderButton()
    {
        var narrow = new FlatButton { Text = "OK" };
        narrow.FitTo(0, 30);
        var wide = new FlatButton { Text = "Only this logger" };
        wide.FitTo(0, 30);
        Assert.That(wide.Width, Is.GreaterThan(narrow.Width));
    }

    [Test]
    public void ExtraHorizontalPaddingWidensTheButton()
    {
        var tight = new FlatButton { Text = "Reset" };
        tight.FitTo(0, 30, padX: 4);
        var padded = new FlatButton { Text = "Reset" };
        padded.FitTo(0, 30, padX: 40);
        Assert.That(padded.Width, Is.GreaterThan(tight.Width));
    }
}
