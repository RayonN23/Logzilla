using System.Drawing;
using Logzilla.App.Infrastructure;
using NUnit.Framework;

namespace Logzilla.App.Tests;

[TestFixture]
public sealed class TextUtilTests
{
    private Bitmap _bmp;
    private Graphics _g;

    [SetUp]
    public void SetUp()
    {
        _bmp = new Bitmap(300, 100);
        _g = Graphics.FromImage(_bmp);
    }

    [TearDown]
    public void TearDown()
    {
        _g.Dispose();
        _bmp.Dispose();
    }

    [Test]
    public void RoundedRectWithZeroRadiusMatchesInputBounds()
    {
        var r = new Rectangle(0, 0, 100, 40);
        using var path = TextUtil.RoundedRect(r, 0);
        var b = path.GetBounds();
        Assert.That(b.X, Is.EqualTo(r.X).Within(0.5f));
        Assert.That(b.Y, Is.EqualTo(r.Y).Within(0.5f));
        Assert.That(b.Width, Is.EqualTo(r.Width).Within(0.5f));
        Assert.That(b.Height, Is.EqualTo(r.Height).Within(0.5f));
    }

    [Test]
    public void RoundedRectWithPositiveRadiusStaysWithinInputBounds()
    {
        var r = new Rectangle(10, 10, 120, 50);
        using var path = TextUtil.RoundedRect(r, 8);
        var b = path.GetBounds();
        // The arcs are inscribed at the corners, so the path never exceeds the
        // rectangle it was built from, and its extremes reach every edge.
        Assert.That(b.Left, Is.GreaterThanOrEqualTo(r.Left - 0.5f));
        Assert.That(b.Top, Is.GreaterThanOrEqualTo(r.Top - 0.5f));
        Assert.That(b.Right, Is.LessThanOrEqualTo(r.Right + 0.5f));
        Assert.That(b.Bottom, Is.LessThanOrEqualTo(r.Bottom + 0.5f));
    }

    [Test]
    public void MeasureTrackedReturnsZeroForEmptyText()
    {
        Assert.That(TextUtil.MeasureTracked(_g, "", Theme.UiHead, 0.8f), Is.EqualTo(0));
        Assert.That(TextUtil.MeasureTracked(_g, null, Theme.UiHead, 0.8f), Is.EqualTo(0));
    }

    [Test]
    public void MeasureTrackedIsPositiveForNonEmptyText()
    {
        Assert.That(TextUtil.MeasureTracked(_g, "VOLUME", Theme.UiHead, 0.8f), Is.GreaterThan(0));
    }

    [Test]
    public void MoreTrackingProducesAWiderMeasurement()
    {
        var tight = TextUtil.MeasureTracked(_g, "VOLUME", Theme.UiHead, 0f);
        var tracked = TextUtil.MeasureTracked(_g, "VOLUME", Theme.UiHead, 5f);
        Assert.That(tracked, Is.GreaterThan(tight));
    }

    [Test]
    public void DrawTrackedDoesNotThrowForEmptyOrNormalText()
    {
        Assert.DoesNotThrow(() => TextUtil.DrawTracked(_g, "", Theme.UiHead, Point.Empty, Color.White, 0.8f));
        Assert.DoesNotThrow(() => TextUtil.DrawTracked(_g, null, Theme.UiHead, Point.Empty, Color.White, 0.8f));
        Assert.DoesNotThrow(() => TextUtil.DrawTracked(_g, "SEVERITY", Theme.UiHead, new Point(4, 4), Color.White, 0.8f));
    }
}
