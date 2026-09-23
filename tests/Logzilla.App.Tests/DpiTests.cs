using System;
using System.Windows.Forms;
using Logzilla.App.Infrastructure;
using NUnit.Framework;

namespace Logzilla.App.Tests;

/// <summary>
/// Dpi.Init reads the live monitor DPI, which this test machine's own display
/// scaling makes non-deterministic. Every test below either forces a value through
/// the LOGZILLA_SCALE escape hatch (deterministic regardless of the real monitor)
/// or asserts an invariant that holds no matter what the real DPI is.
/// </summary>
[TestFixture]
public sealed class DpiTests
{
    private Control _probe;

    [SetUp]
    public void SetUp()
    {
        _probe = new Control();
    }

    [TearDown]
    public void TearDown()
    {
        _probe.Dispose();
        // Restore the baseline the rest of the suite assumes (see AssemblySetup).
        Environment.SetEnvironmentVariable("LOGZILLA_SCALE", "1");
        using (var reset = new Control())
        {
            Dpi.Init(reset);
        }
        Environment.SetEnvironmentVariable("LOGZILLA_SCALE", null);
    }

    [Test]
    public void ValidScaleOverridesBothFactors()
    {
        Environment.SetEnvironmentVariable("LOGZILLA_SCALE", "2");
        Dpi.Init(_probe);
        Assert.That(Dpi.Factor, Is.EqualTo(2f));
        Assert.That(Dpi.FontFactor, Is.EqualTo(2f));
    }

    [TestCase("1")]
    [TestCase("1.5")]
    [TestCase("0.5")]
    [TestCase("4")]
    public void ScaleWithinAllowedRangeIsAppliedExactly(string scale)
    {
        Environment.SetEnvironmentVariable("LOGZILLA_SCALE", scale);
        Dpi.Init(_probe);
        Assert.That(Dpi.Factor, Is.EqualTo(float.Parse(scale, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Test]
    public void ScaleAboveAllowedRangeIsNotAppliedVerbatim()
    {
        // 10x is far outside [0.5, 4]; no real monitor reports 960 DPI, so the
        // override being skipped is the only way Factor can end up here.
        Environment.SetEnvironmentVariable("LOGZILLA_SCALE", "10");
        Dpi.Init(_probe);
        Assert.That(Dpi.Factor, Is.Not.EqualTo(10f));
    }

    [Test]
    public void ScaleBelowAllowedRangeIsNotAppliedVerbatim()
    {
        Environment.SetEnvironmentVariable("LOGZILLA_SCALE", "0.1");
        Dpi.Init(_probe);
        Assert.That(Dpi.Factor, Is.Not.EqualTo(0.1f));
    }

    [Test]
    public void NonNumericScaleDoesNotThrowAndLeavesFactorClamped()
    {
        Environment.SetEnvironmentVariable("LOGZILLA_SCALE", "not-a-number");
        Assert.DoesNotThrow(() => Dpi.Init(_probe));
        Assert.That(Dpi.Factor, Is.InRange(0.5f, 6f));
    }

    [Test]
    public void ScalesIntegerPixelsByFactorWithBankersRounding()
    {
        Environment.SetEnvironmentVariable("LOGZILLA_SCALE", "1.5");
        Dpi.Init(_probe);
        // 3 * 1.5 = 4.5 -> Math.Round's default banker's rounding gives 4.
        Assert.That(Dpi.S(3), Is.EqualTo(4));
        Assert.That(Dpi.S(10), Is.EqualTo(15));
    }

    [Test]
    public void ScalesFloatPixelsByFactor()
    {
        Environment.SetEnvironmentVariable("LOGZILLA_SCALE", "2");
        Dpi.Init(_probe);
        Assert.That(Dpi.S(10f), Is.EqualTo(20f));
    }

    [Test]
    public void MeasuresNonEmptyTextWiderThanEmptyText()
    {
        var empty = Dpi.Text("", Theme.UiBody).Width;
        var full = Dpi.Text("Hello, Logzilla", Theme.UiBody).Width;
        Assert.That(full, Is.GreaterThan(empty));
    }

    [Test]
    public void MeasuresNullTextAsEmptyWithoutThrowing()
    {
        int nullWidth = 0, emptyWidth = 0;
        Assert.DoesNotThrow(() => nullWidth = Dpi.Text(null, Theme.UiBody).Width);
        Assert.DoesNotThrow(() => emptyWidth = Dpi.Text("", Theme.UiBody).Width);
        Assert.That(nullWidth, Is.EqualTo(emptyWidth));
    }
}
