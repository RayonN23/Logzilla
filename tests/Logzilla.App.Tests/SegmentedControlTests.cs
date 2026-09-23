using System.Reflection;
using System.Windows.Forms;
using Logzilla.App.Controls;
using NUnit.Framework;

namespace Logzilla.App.Tests;

/// <summary>
/// Covers Fit()'s sizing (public) and the keyboard toggle (Left/Right arrows),
/// which the brief requires for "every control reachable by Tab" accessibility.
/// OnKeyDown is a protected override, so it is invoked through reflection rather
/// than by subclassing a sealed control.
/// </summary>
[TestFixture]
public sealed class SegmentedControlTests
{
    private static void SendKeyDown(SegmentedControl control, Keys key)
    {
        var method = typeof(SegmentedControl).GetMethod("OnKeyDown", BindingFlags.NonPublic | BindingFlags.Instance);
        method.Invoke(control, [new KeyEventArgs(key)]);
    }

    [Test]
    public void LongerLabelsProduceAWiderControl()
    {
        var narrow = new SegmentedControl { LeftLabel = "On", RightLabel = "Off" };
        narrow.Fit();
        var wide = new SegmentedControl { LeftLabel = "Highlight", RightLabel = "Filter to matches" };
        wide.Fit();
        Assert.That(wide.Width, Is.GreaterThan(narrow.Width));
    }

    [Test]
    public void RightArrowActivatesTheRightSegment()
    {
        var seg = new SegmentedControl { LeftLabel = "Highlight", RightLabel = "Filter to matches" };
        seg.Fit();
        SendKeyDown(seg, Keys.Right);
        Assert.That(seg.RightActive, Is.True);
    }

    [Test]
    public void LeftArrowActivatesTheLeftSegment()
    {
        var seg = new SegmentedControl { LeftLabel = "Highlight", RightLabel = "Filter to matches", RightActive = true };
        seg.Fit();
        SendKeyDown(seg, Keys.Left);
        Assert.That(seg.RightActive, Is.False);
    }

    [Test]
    public void ChangedFiresExactlyOnceWhenTheActiveSegmentFlips()
    {
        var seg = new SegmentedControl { LeftLabel = "A", RightLabel = "B" };
        var fired = 0;
        seg.Changed += delegate { fired++; };
        SendKeyDown(seg, Keys.Right);
        Assert.That(fired, Is.EqualTo(1));
    }

    [Test]
    public void ChangedDoesNotFireWhenTheRequestedSegmentIsAlreadyActive()
    {
        var seg = new SegmentedControl { LeftLabel = "A", RightLabel = "B", RightActive = true };
        var fired = 0;
        seg.Changed += delegate { fired++; };
        SendKeyDown(seg, Keys.Right);
        Assert.That(fired, Is.EqualTo(0));
    }
}
