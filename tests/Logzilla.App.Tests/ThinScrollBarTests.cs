using Logzilla.App.Controls;
using NUnit.Framework;

namespace Logzilla.App.Tests;

/// <summary>
/// A native scrollbar's thumb shrinks to sub-pixel on a 30,000-line file with 40
/// rows on screen; ThinScrollBar exists to enforce a minimum thumb and correct
/// clamping instead. All of this lives in plain public properties, no OnPaint
/// or reflection required.
/// </summary>
[TestFixture]
public sealed class ThinScrollBarTests
{
    [Test]
    public void ValueClampsToZeroWhenSetNegative()
    {
        var sb = new ThinScrollBar(true) { Maximum = 100, LargeChange = 10 };
        sb.Value = -50;
        Assert.That(sb.Value, Is.EqualTo(0));
    }

    [Test]
    public void ValueClampsToTheHighestPositionWhereAFullPageStillFits()
    {
        var sb = new ThinScrollBar(true) { Maximum = 100, LargeChange = 10 };
        sb.Value = 10_000;
        // Highest legal value = Maximum - LargeChange + 1 = 91.
        Assert.That(sb.Value, Is.EqualTo(91));
    }

    [Test]
    public void ReducingMaximumReclampsAnExistingValue()
    {
        var sb = new ThinScrollBar(true) { Maximum = 100, LargeChange = 10, Value = 50 };
        sb.Maximum = 20;
        // New highest legal value = 20 - 10 + 1 = 11.
        Assert.That(sb.Value, Is.EqualTo(11));
    }

    [Test]
    public void IncreasingLargeChangeReclampsAnExistingValue()
    {
        var sb = new ThinScrollBar(true) { Maximum = 100, LargeChange = 10, Value = 91 };
        sb.LargeChange = 50;
        // New highest legal value = 100 - 50 + 1 = 51.
        Assert.That(sb.Value, Is.EqualTo(51));
    }

    [Test]
    public void MaximumNeverGoesNegative()
    {
        var sb = new ThinScrollBar(true) { Maximum = -5 };
        Assert.That(sb.Maximum, Is.EqualTo(0));
    }

    [Test]
    public void LargeChangeIsAtLeastOne()
    {
        var sb = new ThinScrollBar(true) { LargeChange = -3 };
        Assert.That(sb.LargeChange, Is.EqualTo(1));
    }

    [Test]
    public void ValueChangedFiresOnlyWhenTheClampedValueActuallyChanges()
    {
        var sb = new ThinScrollBar(true) { Maximum = 100, LargeChange = 10 };
        var fired = 0;
        sb.ValueChanged += delegate { fired++; };

        sb.Value = 5;
        Assert.That(fired, Is.EqualTo(1));

        sb.Value = 5; // no-op: already at 5
        Assert.That(fired, Is.EqualTo(1));

        sb.Value = 500; // clamps to 91, a real change
        Assert.That(fired, Is.EqualTo(2));

        sb.Value = 1000; // clamps to 91 again: no real change
        Assert.That(fired, Is.EqualTo(2));
    }

    [Test]
    public void SettingMaximumBelowLargeChangePinsValueToZero()
    {
        var sb = new ThinScrollBar(true) { Maximum = 100, LargeChange = 10, Value = 50 };
        sb.Maximum = 3; // MaxValue = max(0, 3 - 10 + 1) = 0
        Assert.That(sb.Value, Is.EqualTo(0));
    }
}
