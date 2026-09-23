using System;
using System.Reflection;
using System.Windows.Forms;
using Logzilla.App.Controls;
using Logzilla.Core.Query;
using NUnit.Framework;

namespace Logzilla.App.Tests;

/// <summary>
/// Selecting a run of entries, so Ctrl+C can take more than one.
///
/// The run is held as two doc line indices — where the selection started and
/// where it now ends — and resolved through the current filter's view. The tests
/// use a view whose line numbers are deliberately not contiguous, because that is
/// what a filtered log looks like and it is where off-by-one errors show up.
/// </summary>
[TestFixture]
public sealed class LogViewSelectionTests
{
    /// <summary>Lines 10, 20, 30, 40, 50 survived the filter, in that order.</summary>
    private static readonly int[] Shown = [10, 20, 30, 40, 50];

    private static readonly int[] JustThirty = [30];
    private static readonly int[] TenAndTwenty = [10, 20];
    private static readonly int[] TenToThirty = [10, 20, 30];
    private static readonly int[] TwentyToForty = [20, 30, 40];
    private static readonly int[] SevenAndNine = [7, 9];
    private static readonly int[] JustTwenty = [20];
    private static readonly int[] TwentyToThirty = [20, 30];
    private static readonly int[] FortyAndFifty = [40, 50];

    private static LogView WithRows(params int[] lines)
    {
        var res = new FilterResult();
        res.View.AddRange(lines);
        return new LogView { Width = 800, Height = 400, Res = res };
    }

    private static LogView Filtered() => WithRows(Shown);

    // ── nothing selected ───────────────────────────────────────────────

    [Test]
    public void AFreshViewHasNothingSelected()
    {
        using var view = Filtered();

        Assert.That(view.SelectionCount, Is.EqualTo(0));
        Assert.That(view.SelectedLines(), Is.Empty);
        Assert.That(view.HasRange, Is.False);
    }

    [Test]
    public void SelectedLinesIsEmptyWithNoResultAtAll()
    {
        using var view = new LogView();

        Assert.That(view.SelectedLines(), Is.Empty);
        Assert.That(view.SelectionCount, Is.EqualTo(0));
    }

    // ── one row ────────────────────────────────────────────────────────

    [Test]
    public void SelectingOneRowSelectsExactlyThatRow()
    {
        using var view = Filtered();
        view.Select(30, false);

        Assert.That(view.SelectionCount, Is.EqualTo(1));
        Assert.That(view.SelectedLines(), Is.EqualTo(JustThirty));
        Assert.That(view.HasRange, Is.False, "One row is not a range; the row still expands.");
    }

    [Test]
    public void OnlyTheSelectedRowReportsItself()
    {
        using var view = Filtered();
        view.Select(30, false);

        Assert.That(view.IsSelected(30), Is.True);
        Assert.That(view.IsSelected(20), Is.False);
        Assert.That(view.IsSelected(40), Is.False);
    }

    // ── extending ──────────────────────────────────────────────────────

    [Test]
    public void ExtendingDownwardsTakesEverythingInBetween()
    {
        using var view = Filtered();
        view.Select(20, false);
        view.SelectTo(40, false);

        Assert.That(view.SelectedLines(), Is.EqualTo(TwentyToForty));
        Assert.That(view.HasRange, Is.True);
    }

    [Test]
    public void ExtendingUpwardsStillReadsInViewOrder()
    {
        using var view = Filtered();
        view.Select(40, false);
        view.SelectTo(20, false);

        Assert.That(view.SelectedLines(), Is.EqualTo(TwentyToForty),
            "The clipboard wants the entries in the order they are shown, not the order they were picked.");
    }

    [Test]
    public void TheAnchorStaysPutSoTheRunCanShrinkAgain()
    {
        using var view = Filtered();
        view.Select(10, false);
        view.SelectTo(50, false);
        view.SelectTo(20, false);

        Assert.That(view.SelectedLines(), Is.EqualTo(TenAndTwenty),
            "Shift+Up after Shift+Down shrinks the run rather than starting a new one.");
    }

    [Test]
    public void ShrinkingAllTheWayBackLeavesOneRow()
    {
        using var view = Filtered();
        view.Select(30, false);
        view.SelectTo(50, false);
        view.SelectTo(30, false);

        Assert.That(view.SelectionCount, Is.EqualTo(1));
        Assert.That(view.HasRange, Is.False);
    }

    [Test]
    public void ExtendingPastTheAnchorFlipsTheRunOver()
    {
        using var view = Filtered();
        view.Select(30, false);
        view.SelectTo(10, false);

        Assert.That(view.SelectedLines(), Is.EqualTo(TenToThirty));
    }

    [Test]
    public void ExtendingWithNothingSelectedJustSelectsThatRow()
    {
        using var view = Filtered();
        view.SelectTo(30, false);

        Assert.That(view.SelectedLines(), Is.EqualTo(JustThirty));
    }

    [Test]
    public void EveryRowOfTheRunReportsItselfSelected()
    {
        using var view = Filtered();
        view.Select(20, false);
        view.SelectTo(40, false);

        Assert.That(view.IsSelected(20), Is.True);
        Assert.That(view.IsSelected(30), Is.True);
        Assert.That(view.IsSelected(40), Is.True);
        Assert.That(view.IsSelected(10), Is.False);
        Assert.That(view.IsSelected(50), Is.False);
    }

    // ── collapsing ─────────────────────────────────────────────────────

    [Test]
    public void APlainClickAfterARunCollapsesIt()
    {
        using var view = Filtered();
        view.Select(10, false);
        view.SelectTo(50, false);
        view.Select(30, false);

        Assert.That(view.SelectedLines(), Is.EqualTo(JustThirty));
        Assert.That(view.HasRange, Is.False);
    }

    [Test]
    public void ClearingDropsTheRunAndItsAnchor()
    {
        using var view = Filtered();
        view.Select(10, false);
        view.SelectTo(50, false);
        view.ClearSelection();

        Assert.That(view.Selected, Is.EqualTo(-1));
        Assert.That(view.SelectionCount, Is.EqualTo(0));

        // The stale anchor must not come back when the next row is picked.
        view.SelectTo(30, false);
        Assert.That(view.SelectedLines(), Is.EqualTo(JustThirty));
    }

    // ── select all ─────────────────────────────────────────────────────

    [Test]
    public void SelectAllTakesEveryShownRow()
    {
        using var view = Filtered();
        view.SelectAll();

        Assert.That(view.SelectedLines(), Is.EqualTo(Shown));
        Assert.That(view.SelectionCount, Is.EqualTo(Shown.Length));
    }

    [Test]
    public void SelectAllTakesOnlyWhatTheFilterLeft()
    {
        // The document has more lines than this; the view is what is on screen.
        using var view = WithRows(7, 9);
        view.SelectAll();

        Assert.That(view.SelectedLines(), Is.EqualTo(SevenAndNine));
        Assert.That(view.IsSelected(8), Is.True,
            "8 sits between them in line order, so it falls inside the run's bounds.");
    }

    [Test]
    public void SelectAllOnAnEmptyViewDoesNothing()
    {
        using var view = WithRows();

        Assert.DoesNotThrow(view.SelectAll);
        Assert.That(view.SelectionCount, Is.EqualTo(0));
    }

    // ── dragging ───────────────────────────────────────────────────────
    //
    // Pressing and sweeping the mouse down the rows is the other half of
    // Shift+click. Which row the pointer is over is hit-testing against the last
    // painted layout and needs a real file behind it; what is reachable here is
    // the part with rules of its own — the auto-scroll that feeds rows in while
    // the pointer is held past an edge, and the row expansion standing down so
    // the layout cannot shift under a drag in progress.

    private static void SetPrivate(LogView view, string name, object value) =>
        typeof(LogView).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(view, value);

    private static bool CanExpand(LogView view) =>
        (bool)typeof(LogView).GetProperty("CanExpand", BindingFlags.NonPublic | BindingFlags.Instance)
                             .GetValue(view);

    private static bool AutoScrollRunning(LogView view) =>
        ((Timer)typeof(LogView).GetField("_autoScroll", BindingFlags.NonPublic | BindingFlags.Instance)
                               .GetValue(view)).Enabled;

    private static void Tick(LogView view) =>
        typeof(LogView).GetMethod("AutoScrollTick", BindingFlags.NonPublic | BindingFlags.Instance)
                       .Invoke(view, [null, EventArgs.Empty]);

    /// <summary>A view mid-drag, with the pointer held past the given edge.</summary>
    private static LogView Dragging(int direction)
    {
        var view = Filtered();
        SetPrivate(view, "_dragging", true);
        SetPrivate(view, "_autoScrollDir", direction);
        return view;
    }

    [Test]
    public void HoldingPastTheBottomEdgeTakesTheNextRowEachTick()
    {
        using var view = Dragging(1);
        view.Select(20, false);

        Tick(view);
        Assert.That(view.SelectedLines(), Is.EqualTo(TwentyToThirty));

        Tick(view);
        Assert.That(view.SelectedLines(), Is.EqualTo(TwentyToForty));
    }

    [Test]
    public void HoldingPastTheTopEdgeWorksTheOtherWay()
    {
        using var view = Dragging(-1);
        view.Select(30, false);

        Tick(view);
        Tick(view);

        Assert.That(view.SelectedLines(), Is.EqualTo(TenToThirty));
    }

    [Test]
    public void AutoScrollStopsAtTheEndOfTheView()
    {
        using var view = Dragging(1);
        view.Select(40, false);

        Tick(view);
        Tick(view);
        Tick(view);

        Assert.That(view.SelectedLines(), Is.EqualTo(FortyAndFifty),
            "There is nothing past the last row to select.");
    }

    [Test]
    public void AutoScrollShutsItselfOffOnceTheDragEnds()
    {
        using var view = Dragging(1);
        view.Select(20, false);
        SetPrivate(view, "_dragging", false);

        Tick(view);

        Assert.That(view.SelectedLines(), Is.EqualTo(JustTwenty), "A finished drag selects nothing more.");
        Assert.That(AutoScrollRunning(view), Is.False);
    }

    [Test]
    public void AutoScrollDoesNothingWhileThePointerIsInsideTheGrid()
    {
        using var view = Dragging(0);
        view.Select(20, false);

        Tick(view);

        Assert.That(view.SelectedLines(), Is.EqualTo(JustTwenty));
    }

    [Test]
    public void TheRowDoesNotExpandWhileTheButtonIsDown()
    {
        using var view = Filtered();
        view.Select(30, false);
        Assert.That(CanExpand(view), Is.True, "A settled single selection expands as it always did.");

        SetPrivate(view, "_dragging", true);
        Assert.That(CanExpand(view), Is.False,
            "An expanding row is several rows tall; the layout must not move under a drag.");
    }

    [Test]
    public void ReleasingTheButtonEndsTheDragAndLetsTheRowExpand()
    {
        using var view = Dragging(1);
        view.Select(30, false);

        typeof(LogView).GetMethod("OnMouseUp", BindingFlags.NonPublic | BindingFlags.Instance)
                       .Invoke(view, [new MouseEventArgs(MouseButtons.Left, 1, 10, 10, 0)]);

        Assert.That(CanExpand(view), Is.True);
        Assert.That(AutoScrollRunning(view), Is.False);
    }

    [Test]
    public void ARunSelectedByDraggingStillDoesNotExpand()
    {
        using var view = Filtered();
        view.Select(20, false);
        view.SelectTo(40, false);

        Assert.That(CanExpand(view), Is.False, "Expanding one row of three says nothing useful.");
    }

    // ── it tells the form ──────────────────────────────────────────────

    [Test]
    public void EveryWayOfSelectingRaisesSelectionChanged()
    {
        using var view = Filtered();
        var fired = 0;
        view.SelectionChanged += delegate { fired++; };

        view.Select(20, false);
        view.SelectTo(40, false);
        view.SelectAll();

        Assert.That(fired, Is.EqualTo(3), "The status bar and the inspector follow this event.");
    }
}
