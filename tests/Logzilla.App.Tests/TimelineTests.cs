using System.Reflection;
using System.Windows.Forms;
using Logzilla.App.Controls;
using Logzilla.Core.Query;
using NUnit.Framework;

namespace Logzilla.App.Tests;

/// <summary>
/// XToT/ToX map a pixel column to a timestamp and back; every drag-to-select,
/// click-to-seek and hover-card feature in the timeline depends on this arithmetic
/// being correct. ShortAxisLabel is the "HH:mm:ss" truncation rule for axis labels.
/// All three are private, so they are called through reflection.
/// </summary>
[TestFixture]
public sealed class TimelineTests
{
    private static double XToT(Timeline t, int x)
    {
        var m = typeof(Timeline).GetMethod("XToT", BindingFlags.NonPublic | BindingFlags.Instance);
        return (double)m.Invoke(t, [x]);
    }

    private static int ToX(Timeline t, double time)
    {
        var m = typeof(Timeline).GetMethod("ToX", BindingFlags.NonPublic | BindingFlags.Instance);
        return (int)m.Invoke(t, [time]);
    }

    private static string ShortAxisLabel(double ms)
    {
        var m = typeof(Timeline).GetMethod("ShortAxisLabel", BindingFlags.NonPublic | BindingFlags.Static);
        return (string)m.Invoke(null, [ms]);
    }

    private static Timeline WithRange(int width, double lo, double hi)
    {
        var t = new Timeline { Width = width, Res = new FilterResult { Lo = lo, Hi = hi } };
        return t;
    }

    [Test]
    public void LeftEdgeMapsToTheLowBound()
    {
        var t = WithRange(1000, 1_000, 5_000);
        Assert.That(XToT(t, 0), Is.EqualTo(1_000));
    }

    [Test]
    public void RightEdgeMapsToTheHighBound()
    {
        var t = WithRange(1000, 1_000, 5_000);
        Assert.That(XToT(t, 1000), Is.EqualTo(5_000));
    }

    [Test]
    public void ToXInvertsXToTAtTheBounds()
    {
        var t = WithRange(1000, 1_000, 5_000);
        Assert.That(ToX(t, 1_000), Is.EqualTo(0));
        Assert.That(ToX(t, 5_000), Is.EqualTo(1000));
    }

    [Test]
    public void RoundTripThroughXToTAndToXStaysWithinARoundingPixel()
    {
        var t = WithRange(1000, 1_000, 5_000);
        var time = XToT(t, 257);
        Assert.That(ToX(t, time), Is.EqualTo(257).Within(1));
    }

    [Test]
    public void XToTReturnsNaNWhenThereIsNoResult()
    {
        var t = new Timeline { Width = 1000 };
        Assert.That(double.IsNaN(XToT(t, 100)), Is.True);
    }

    [Test]
    public void XToTReturnsNaNForADegenerateRange()
    {
        var t = WithRange(1000, 5_000, 5_000); // Hi == Lo
        Assert.That(double.IsNaN(XToT(t, 100)), Is.True);
    }

    [Test]
    public void ToXReturnsZeroWhenThereIsNoResult()
    {
        var t = new Timeline { Width = 1000 };
        Assert.That(ToX(t, 2_000), Is.EqualTo(0));
    }

    [Test]
    public void ShortAxisLabelKeepsOnlyTheTimeOfDay()
    {
        // 2026-03-01 08:14:02.113 UTC, expressed as ms since the Unix epoch.
        var epoch = new System.DateTime(1970, 1, 1, 0, 0, 0, System.DateTimeKind.Utc);
        var stamp = new System.DateTime(2026, 3, 1, 8, 14, 2, 113, System.DateTimeKind.Utc);
        var ms = (stamp - epoch).TotalMilliseconds;
        Assert.That(ShortAxisLabel(ms), Is.EqualTo("08:14:02"));
    }

    [Test]
    public void ShortAxisLabelOfNaNIsEmpty()
    {
        Assert.That(ShortAxisLabel(double.NaN), Is.EqualTo(""));
    }

    // ── double-click to clear the range ────────────────────────────────
    //
    // Dragging the timeline narrows the time range; double-clicking it puts the
    // range back. The "Reset zoom" link does the same thing, but it is a small
    // target in the corner and it is not what anyone's hands reach for.

    private static void Raise(Timeline t, string method, MouseEventArgs e)
    {
        typeof(Timeline).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)
                        .Invoke(t, [e]);
    }

    private static MouseEventArgs Click(int x, int clicks = 1) =>
        new(MouseButtons.Left, clicks, x, 40, 0);

    [Test]
    public void DoubleClickingClearsTheTimeRange()
    {
        var t = WithRange(1000, 1_000, 5_000);
        t.T0 = 2_000;
        t.T1 = 3_000;
        Timeline.RangeArgs got = null;
        t.RangeSelected += (_, a) => got = a;

        Raise(t, "OnMouseDoubleClick", Click(500, clicks: 2));

        Assert.That(got, Is.Not.Null, "Double-click has to ask for a new range.");
        Assert.That(double.IsNaN(got.A), Is.True, "A NaN range means no time filter.");
        Assert.That(double.IsNaN(got.B), Is.True);
    }

    [Test]
    public void TheMouseUpCompletingADoubleClickDoesNotAlsoSeek()
    {
        var t = WithRange(1000, 1_000, 5_000);
        var seeks = 0;
        var ranges = 0;
        t.Seek += delegate { seeks++; };
        t.RangeSelected += delegate { ranges++; };

        // The sequence Windows delivers for the second half of a double-click.
        Raise(t, "OnMouseDown", Click(500, clicks: 2));
        Raise(t, "OnMouseDoubleClick", Click(500, clicks: 2));
        Raise(t, "OnMouseUp", Click(500));

        Assert.That(ranges, Is.EqualTo(1));
        Assert.That(seeks, Is.EqualTo(0), "Resetting the range must not also scroll the grid.");
    }

    [Test]
    public void APlainClickStillSeeks()
    {
        var t = WithRange(1000, 1_000, 5_000);
        double? sought = null;
        t.Seek += (_, a) => sought = a.T;

        Raise(t, "OnMouseDown", Click(500));
        Raise(t, "OnMouseUp", Click(500));

        Assert.That(sought, Is.EqualTo(3_000).Within(10));
    }

    [Test]
    public void ADragStillSelectsARange()
    {
        var t = WithRange(1000, 1_000, 5_000);
        Timeline.RangeArgs got = null;
        t.RangeSelected += (_, a) => got = a;

        Raise(t, "OnMouseDown", Click(200));
        Raise(t, "OnMouseUp", Click(600));

        Assert.That(got, Is.Not.Null);
        Assert.That(got.A, Is.EqualTo(1_800).Within(10));
        Assert.That(got.B, Is.EqualTo(3_400).Within(10));
    }

    [Test]
    public void AClickAfterADoubleClickSeeksAgain()
    {
        var t = WithRange(1000, 1_000, 5_000);
        var seeks = 0;
        t.Seek += delegate { seeks++; };

        Raise(t, "OnMouseDown", Click(500, clicks: 2));
        Raise(t, "OnMouseDoubleClick", Click(500, clicks: 2));
        Raise(t, "OnMouseUp", Click(500));

        Raise(t, "OnMouseDown", Click(300));
        Raise(t, "OnMouseUp", Click(300));

        Assert.That(seeks, Is.EqualTo(1), "The seek suppression lasts one mouse-up, not forever.");
    }
}
