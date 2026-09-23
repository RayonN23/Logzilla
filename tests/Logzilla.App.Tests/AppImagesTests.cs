using System.Drawing;
using Logzilla.App.Infrastructure;
using NUnit.Framework;

namespace Logzilla.App.Tests;

/// <summary>
/// The window and dialog icon is the product's own artwork, and <c>Mark</c> renders
/// it as a bitmap for anything that wants it inline. These guard the two things that
/// made the old hand-drawn stand-in look wrong: the artwork has to actually load, and
/// it has to come from a frame big enough that it isn't a blur.
/// </summary>
[TestFixture]
public sealed class AppImagesTests
{
    [Test]
    public void EmbeddedIconLoads()
    {
        using var icon = AppImages.Load();
        Assert.That(icon, Is.Not.Null, "The 'appicon' embedded resource did not load.");
    }

    [TestCase(1, 16)]
    [TestCase(16, 16)]
    [TestCase(17, 32)]
    [TestCase(24, 32)]
    [TestCase(32, 32)]
    [TestCase(33, 48)]
    [TestCase(64, 64)]
    [TestCase(100, 128)]
    public void BestFrameIsTheSmallestFrameThatIsNotUpscaled(int target, int expected)
    {
        Assert.That(AppImages.BestFrame(target), Is.EqualTo(expected));
    }

    [Test]
    public void BestFrameFallsBackToTheLargestFrameBeyondTheIconsRange()
    {
        Assert.That(AppImages.BestFrame(4096), Is.EqualTo(256));
    }

    [TestCase(20)]
    [TestCase(24)]
    [TestCase(36)]
    public void MarkRendersAtTheRequestedSize(int px)
    {
        var mark = AppImages.Mark(px);
        Assert.That(mark, Is.Not.Null);
        Assert.That(mark.Width, Is.EqualTo(px));
        Assert.That(mark.Height, Is.EqualTo(px));
    }

    [Test]
    public void MarkIsNotBlank()
    {
        // A mark that loaded but rendered empty would look identical to a missing
        // icon in the title bar, so "not null" on its own proves nothing.
        var mark = AppImages.Mark(24);
        var opaque = 0;
        for (var x = 0; x < mark.Width; x++)
        {
            for (var y = 0; y < mark.Height; y++)
            {
                if (mark.GetPixel(x, y).A > 8)
                {
                    opaque++;
                }
            }
        }
        Assert.That(opaque, Is.GreaterThan(mark.Width * mark.Height / 10),
            "Fewer than 10% of the rendered icon's pixels carry any ink.");
    }

    [Test]
    public void MarkIsCachedPerSize()
    {
        // Re-rendering the icon on every title-bar paint would be wasteful.
        var first = AppImages.Mark(28);
        var second = AppImages.Mark(28);
        Assert.That(second, Is.SameAs(first));
    }

    [Test]
    public void MarkNeverReturnsAZeroSizedBitmap()
    {
        var mark = AppImages.Mark(0);
        Assert.That(mark, Is.Not.Null);
        Assert.That(mark.Size, Is.EqualTo(new Size(1, 1)));
    }
}
