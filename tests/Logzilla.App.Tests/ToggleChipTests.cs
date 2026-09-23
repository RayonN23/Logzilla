using System.Drawing;
using Logzilla.App.Controls;
using Logzilla.App.Infrastructure;
using NUnit.Framework;

namespace Logzilla.App.Tests;

/// <summary>
/// Fit() sizes the chip so severity pills, the .*/Aa toggles, and Wrap/Live tail
/// never clip their own label or count — failure #1 in the redesign brief. These
/// assertions are deliberately differential (does adding a dot/count grow the
/// chip?) rather than pinned to ToggleChip's private padding constants, so they
/// keep protecting real regressions even if those pixel values are retuned later.
/// </summary>
[TestFixture]
public sealed class ToggleChipTests
{
    [Test]
    public void MinWidthFloorWinsWhenContentIsNarrow()
    {
        var chip = new ToggleChip { Label = "A" };
        chip.Fit(200);
        Assert.That(chip.Width, Is.EqualTo(Dpi.S(200)));
    }

    [Test]
    public void LongerLabelProducesAWiderChip()
    {
        var narrow = new ToggleChip { Label = "A" };
        narrow.Fit(0);
        var wide = new ToggleChip { Label = "A much longer chip label" };
        wide.Fit(0);
        Assert.That(wide.Width, Is.GreaterThan(narrow.Width));
    }

    [Test]
    public void AddingACountWidensTheChip()
    {
        var noCount = new ToggleChip { Label = "ERROR" };
        noCount.Fit(0);
        var withCount = new ToggleChip { Label = "ERROR", Count = "1,234" };
        withCount.Fit(0);
        Assert.That(withCount.Width, Is.GreaterThan(noCount.Width));
    }

    [Test]
    public void AddingADotWidensTheChip()
    {
        var noDot = new ToggleChip { Label = "WARN" };
        noDot.Fit(0);
        var withDot = new ToggleChip { Label = "WARN", Dot = Color.Red };
        withDot.Fit(0);
        Assert.That(withDot.Width, Is.GreaterThan(noDot.Width));
    }

    [Test]
    public void FitIsIdempotentForTheSameContent()
    {
        var chip = new ToggleChip { Label = "DEBUG", Count = "62" };
        chip.Fit(0);
        var first = chip.Width;
        chip.Fit(0);
        Assert.That(chip.Width, Is.EqualTo(first));
    }
}
