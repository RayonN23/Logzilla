using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using Logzilla.App.Controls;
using Logzilla.Core.Logs;
using Logzilla.Core.Query;
using NUnit.Framework;

namespace Logzilla.App.Tests;

/// <summary>
/// Driving the grid with the mouse: press, sweep, release.
///
/// Which row the pointer is over is hit-testing against the layout the last paint
/// built, so unlike the rest of the selection tests these need a document attached
/// and a real paint to have happened. Neither needs a file on disk — the grid
/// reaches line text through <see cref="ILineReader"/>, so a fake one is enough,
/// and the document's parallel arrays are public.
/// </summary>
[TestFixture]
public sealed class LogViewDragTests
{
    private const int Rows = 8;
    private const int ViewWidth = 900;
    private const int ViewHeight = 600;

    private static readonly int[] RowTwo = [2];
    private static readonly int[] RowThree = [3];
    private static readonly int[] RowSix = [6];
    private static readonly int[] RowsTwoAndThree = [2, 3];
    private static readonly int[] RowsTwoToFour = [2, 3, 4];
    private static readonly int[] RowsThreeToFive = [3, 4, 5];
    private static readonly int[] RowsOneToFour = [1, 2, 3, 4];

    /// <summary>Hands back "line 0", "line 1", ... so the grid has something to draw.</summary>
    private sealed class FakeReader : ILineReader
    {
        public int Locate(int index, out byte[] buffer)
        {
            buffer = Encoding.UTF8.GetBytes(Text(index));
            return 0;
        }

        public string Text(int index) => "line " + index;

        public void Dispose() { }
    }

    private sealed class FakeIndexer : ILogIndexer
    {
        public LogSource Prepare(string path) => throw new NotSupportedException();

        public void Run(LogDocument document, int sourceIndex, Action<long, long> progress, Func<bool> cancelled) =>
            throw new NotSupportedException();

        public LogDocument Merge(IReadOnlyList<LogDocument> documents) => throw new NotSupportedException();

        public void Reorder(LogDocument document) => throw new NotSupportedException();

        public ILineReader OpenReader(LogDocument document, int bufferSize) => new FakeReader();
    }

    /// <summary>A plain-text document of <see cref="Rows"/> single-line INFO entries,
    /// every one of them showing.</summary>
    private static LogView Attached()
    {
        var doc = new LogDocument { Name = "test.log" };
        doc.Srcs.Add(new LogSource { Path = "test.log", Name = "test.log", Label = "test" });
        for (var i = 0; i < Rows; i++)
        {
            doc.Sev[i] = 3;
            doc.Len[i] = 8;
            doc.Ts[i] = double.NaN;
        }
        doc.N = Rows;

        var res = new FilterResult { SrcCounts = new int[1] };
        for (var i = 0; i < Rows; i++)
        {
            res.View.Add(i);
        }

        var view = new LogView { Width = ViewWidth, Height = ViewHeight, Indexer = new FakeIndexer() };
        view.Attach(doc, res);
        Paint(view);
        return view;
    }

    /// <summary>Runs a paint so the row layout the mouse hit-tests against exists.</summary>
    private static void Paint(LogView view)
    {
        using var bitmap = new Bitmap(ViewWidth, ViewHeight);
        using var g = Graphics.FromImage(bitmap);
        typeof(LogView).GetMethod("OnPaint", BindingFlags.NonPublic | BindingFlags.Instance)
                       .Invoke(view, [new PaintEventArgs(g, new Rectangle(0, 0, ViewWidth, ViewHeight))]);
    }

    /// <summary>Vertical centre of the given row, in client coordinates.</summary>
    private static int RowY(LogView view, int row) => row * view.RowHeight + view.RowHeight / 2;

    private static void Mouse(LogView view, string handler, int y, MouseButtons button = MouseButtons.Left)
    {
        typeof(LogView).GetMethod(handler, BindingFlags.NonPublic | BindingFlags.Instance)
                       .Invoke(view, [new MouseEventArgs(button, 1, ViewWidth / 2, y, 0)]);
    }

    private static void Press(LogView view, int row) => Mouse(view, "OnMouseDown", RowY(view, row));

    private static void MoveTo(LogView view, int row) => Mouse(view, "OnMouseMove", RowY(view, row));

    private static void Release(LogView view, int row) => Mouse(view, "OnMouseUp", RowY(view, row));

    // ── the fixture itself ─────────────────────────────────────────────

    [Test]
    public void TheGridLaysOutOneRowPerEntry()
    {
        using var view = Attached();

        Assert.That(view.RowHeight, Is.GreaterThan(0));
        Assert.That(view.VisibleRows, Is.GreaterThanOrEqualTo(Rows),
            "Every row has to be on screen, or the hit-test coordinates mean nothing.");
    }

    // ── a plain click ──────────────────────────────────────────────────

    [Test]
    public void PressingARowSelectsThatRowAlone()
    {
        using var view = Attached();
        Press(view, 2);

        Assert.That(view.Selected, Is.EqualTo(2));
        Assert.That(view.SelectionCount, Is.EqualTo(1));
    }

    [Test]
    public void AClickWithoutMovingStillSelectsOneRow()
    {
        using var view = Attached();
        Press(view, 3);
        Release(view, 3);

        Assert.That(view.SelectedLines(), Is.EqualTo(RowThree));
        Assert.That(view.HasRange, Is.False);
    }

    [Test]
    public void MovingTheMouseWithNoButtonDownSelectsNothing()
    {
        using var view = Attached();
        MoveTo(view, 5);

        Assert.That(view.SelectionCount, Is.EqualTo(0), "Hovering is not selecting.");
    }

    // ── sweeping ───────────────────────────────────────────────────────

    [Test]
    public void SweepingDownTakesEveryRowPassedOver()
    {
        using var view = Attached();
        Press(view, 1);
        MoveTo(view, 2);
        MoveTo(view, 4);
        Release(view, 4);

        Assert.That(view.SelectedLines(), Is.EqualTo(RowsOneToFour));
    }

    [Test]
    public void SweepingUpTakesEveryRowPassedOver()
    {
        using var view = Attached();
        Press(view, 5);
        MoveTo(view, 3);
        Release(view, 3);

        Assert.That(view.SelectedLines(), Is.EqualTo(RowsThreeToFive),
            "Sweeping upward still reads in view order.");
    }

    [Test]
    public void ReversingMidSweepShrinksTheRunBackToTheAnchor()
    {
        using var view = Attached();
        Press(view, 2);
        MoveTo(view, 6);
        MoveTo(view, 3);
        Release(view, 3);

        Assert.That(view.SelectedLines(), Is.EqualTo(RowsTwoAndThree),
            "The row the press landed on stays the anchor for the whole sweep.");
    }

    [Test]
    public void SweepingPastTheAnchorFlipsTheRunOver()
    {
        using var view = Attached();
        Press(view, 4);
        MoveTo(view, 6);
        MoveTo(view, 2);
        Release(view, 2);

        Assert.That(view.SelectedLines(), Is.EqualTo(RowsTwoToFour));
    }

    [Test]
    public void MovingWithinTheSameRowChangesNothing()
    {
        using var view = Attached();
        Press(view, 3);
        Mouse(view, "OnMouseMove", 3 * view.RowHeight + 1);
        Mouse(view, "OnMouseMove", 4 * view.RowHeight - 1);

        Assert.That(view.SelectionCount, Is.EqualTo(1),
            "A press and a wiggle is a click, not a selection of two rows.");
    }

    [Test]
    public void ThePressedRowDoesNotExpandUntilTheButtonComesUp()
    {
        using var view = Attached();
        Press(view, 3);

        var canExpand = typeof(LogView).GetProperty("CanExpand", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.That((bool)canExpand.GetValue(view), Is.False,
            "An expanding row is several rows tall; the layout must not move mid-sweep.");

        Release(view, 3);
        Assert.That((bool)canExpand.GetValue(view), Is.True);
    }

    [Test]
    public void MovingAfterTheButtonIsReleasedNoLongerSelects()
    {
        using var view = Attached();
        Press(view, 2);
        Release(view, 2);
        MoveTo(view, 6);

        Assert.That(view.SelectedLines(), Is.EqualTo(RowTwo),
            "The sweep ends with the button, not with the pointer leaving the row.");
    }

    // ── the right button ───────────────────────────────────────────────

    [Test]
    public void RightClickingInsideARunKeepsIt()
    {
        using var view = Attached();
        Press(view, 1);
        MoveTo(view, 4);
        Release(view, 4);

        Mouse(view, "OnMouseDown", RowY(view, 3), MouseButtons.Right);

        Assert.That(view.SelectedLines(), Is.EqualTo(RowsOneToFour),
            "The context menu acts on the selection, so opening it must not destroy it.");
    }

    [Test]
    public void RightClickingOutsideARunSelectsWhatWasClicked()
    {
        using var view = Attached();
        Press(view, 1);
        MoveTo(view, 3);
        Release(view, 3);

        Mouse(view, "OnMouseDown", RowY(view, 6), MouseButtons.Right);

        Assert.That(view.SelectedLines(), Is.EqualTo(RowSix));
    }

    [Test]
    public void TheRightButtonDoesNotStartASweep()
    {
        using var view = Attached();
        Mouse(view, "OnMouseDown", RowY(view, 2), MouseButtons.Right);
        MoveTo(view, 5);

        Assert.That(view.SelectedLines(), Is.EqualTo(RowTwo));
    }
}
