using System.Drawing;
using Logzilla.App.Controls;
using Logzilla.App.Dialogs;
using Logzilla.App.Infrastructure;
using NUnit.Framework;

namespace Logzilla.App.Tests;

/// <summary>
/// Where the field filter dialog opens. It used to be a drop-down hanging off the
/// "+ Field filter" button at the window's right edge, and with the window maximised
/// it opened half on the next monitor.
/// </summary>
[TestFixture]
public sealed class FieldFilterDialogTests
{
    // Two 1920×1080 monitors side by side; the main one on the right, taskbar 40px.
    private static readonly Rectangle RightMonitor = new(1920, 0, 1920, 1040);
    private static readonly Size Dialog = new(460, 470);

    [Test]
    public void ItOpensCentredOverTheMainWindow()
    {
        var owner = new Rectangle(2200, 100, 1200, 800);
        var at = FieldFilterDialog.Place(owner, RightMonitor, Dialog, null);
        Assert.That(at.X, Is.EqualTo(owner.X + (owner.Width - Dialog.Width) / 2));
        Assert.That(new Rectangle(at, Dialog).IntersectsWith(owner), Is.True);
    }

    [Test]
    public void AMaximisedWindowKeepsItOnItsOwnMonitor()
    {
        // Windows gives a maximised window bounds a few pixels past the monitor's edge.
        var maximised = new Rectangle(1912, -8, 1936, 1056);
        var at = FieldFilterDialog.Place(maximised, RightMonitor, Dialog, null);
        Assert.That(RightMonitor.Contains(new Rectangle(at, Dialog)), Is.True);
    }

    [Test]
    public void AWindowHangingOffTheScreenStillGetsADialogThatIsFullyOnIt()
    {
        var offEdge = new Rectangle(3500, 900, 800, 600);
        var at = FieldFilterDialog.Place(offEdge, RightMonitor, Dialog, null);
        Assert.That(RightMonitor.Contains(new Rectangle(at, Dialog)), Is.True);
    }

    [Test]
    public void ItReopensWhereItWasLeft()
    {
        var owner = new Rectangle(2000, 50, 1600, 900);
        var left = new Point(40, 60);
        Assert.That(FieldFilterDialog.Place(owner, RightMonitor, Dialog, left), Is.EqualTo(new Point(2040, 110)));
    }

    [Test]
    public void AWherePlaceThatWouldLeaveTheMonitorIsForgotten()
    {
        // Left there while the window was elsewhere; now it would land on the other screen.
        var owner = new Rectangle(1920, 0, 1920, 1040);
        var at = FieldFilterDialog.Place(owner, RightMonitor, Dialog, new Point(-600, 20));
        Assert.That(RightMonitor.Contains(new Rectangle(at, Dialog)), Is.True);
    }

    [Test]
    public void DraggingTheHeaderMovesTheDialogAndTheCrossClosesIt()
    {
        using var editor = new FieldFilterEditor(new FieldCatalog());
        using var dialog = new FieldFilterDialog(editor, null) { Location = new Point(100, 100) };
        Assert.That(dialog.ClientSize, Is.EqualTo(editor.Size));
        Assert.That(editor.InHeader(new Point(20, 10)), Is.True, "the title bar area drags");
        Assert.That(editor.InHeader(new Point(editor.Width - 12, 10)), Is.False, "except the × at its right");
        Assert.That(editor.InHeader(new Point(20, 80)), Is.False, "the body does not");
    }
}
