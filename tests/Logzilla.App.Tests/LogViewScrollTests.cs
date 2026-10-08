using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using Logzilla.App.Controls;
using Logzilla.Core.Logs;
using Logzilla.Core.Query;
using NUnit.Framework;

namespace Logzilla.App.Tests;

/// <summary>
/// How far the grid scrolls. The range used to be "rows minus a page", which only
/// holds while every row is one line high. With Wrap on (or a row expanded) rows are
/// taller, so a short result list — what a search leaves — ran off the bottom while
/// the scroll bar thought it all fitted: the bar vanished and the wheel stopped
/// before the last results.
/// </summary>
[TestFixture]
public sealed class LogViewScrollTests
{
    private const int ViewWidth = 600;
    private const int ViewHeight = 300;

    /// <summary>Every line is long enough to wrap onto several display lines.</summary>
    private static readonly string LongLine = string.Join(" ", new string[40]).Replace(" ", "lorem ipsum ");

    private sealed class LongReader(string line) : ILineReader
    {
        public int Locate(int index, out byte[] buffer)
        {
            buffer = Encoding.UTF8.GetBytes(Text(index));
            return 0;
        }

        public string Text(int index) => index + " " + line;

        public void Dispose() { }
    }

    private sealed class FakeIndexer(string line) : ILogIndexer
    {
        public LogSource Prepare(string path) => throw new NotSupportedException();

        public void Run(LogDocument document, int sourceIndex, Action<long, long> progress, Func<bool> cancelled) =>
            throw new NotSupportedException();

        public LogDocument Merge(IReadOnlyList<LogDocument> documents) => throw new NotSupportedException();

        public void Reorder(LogDocument document) => throw new NotSupportedException();

        public ILineReader OpenReader(LogDocument document, int bufferSize) => new LongReader(line);
    }

    /// <summary>A document of <paramref name="rows"/> long lines, all of them showing.</summary>
    private static LogView Attached(int rows, string line = null)
    {
        line ??= LongLine;
        var doc = new LogDocument { Name = "test.log" };
        doc.Srcs.Add(new LogSource { Path = "test.log", Name = "test.log", Label = "test" });
        for (var i = 0; i < rows; i++)
        {
            doc.Sev[i] = 3;
            doc.Len[i] = line.Length;
            doc.Ts[i] = double.NaN;
        }
        doc.N = rows;

        var res = new FilterResult { SrcCounts = new int[1] };
        for (var i = 0; i < rows; i++)
        {
            res.View.Add(i);
        }

        var view = new LogView { Width = ViewWidth, Height = ViewHeight, Indexer = new FakeIndexer(line) };
        view.Attach(doc, res);
        Paint(view);
        return view;
    }

    private static void Paint(LogView view)
    {
        using var bitmap = new Bitmap(ViewWidth, ViewHeight);
        using var g = Graphics.FromImage(bitmap);
        typeof(LogView).GetMethod("OnPaint", BindingFlags.NonPublic | BindingFlags.Instance)
                       .Invoke(view, [new PaintEventArgs(g, new Rectangle(0, 0, ViewWidth, ViewHeight))]);
    }

    private static int Rows(LogView view) => view.VisibleRows / 2;

    [Test]
    public void FewerRowsThanAPageDoNotScrollWhenTheyFit()
    {
        using var view = Attached(Rows(view: new LogView { Width = ViewWidth, Height = ViewHeight }));

        view.ScrollBy(100);

        Assert.That(view.TopRow, Is.EqualTo(0), "Unwrapped, every row is one line and they all fit.");
    }

    [Test]
    public void WrappedRowsThatRunOffTheBottomCanBeScrolledTo()
    {
        var rows = Rows(new LogView { Width = ViewWidth, Height = ViewHeight });
        using var view = Attached(rows);
        view.Wrap = true;
        Paint(view);

        view.ScrollBy(1000);

        Assert.That(view.TopRow, Is.GreaterThan(0), "Wrapped, the same rows no longer fit, so the grid scrolls.");
        Assert.That(view.TopRow, Is.LessThan(rows), "And it stops with the last row still on screen.");
    }

    [Test]
    public void SelectingTheLastRowLeavesItWhollyOnScreen()
    {
        // The selected row expands, so the range grows the moment it is selected. The
        // scroll bar used to clamp the new top row to its old, smaller maximum and leave
        // the last row hanging off the bottom. Found by the end-to-end tests (End key).
        var rows = new LogView { Width = ViewWidth, Height = ViewHeight }.VisibleRows * 2;
        // One-line rows, so the expanded row (message, then its action row) is clearly
        // taller than it was collapsed and the range has to grow to fit it.
        using var view = Attached(rows, "short line");
        Paint(view);

        view.Select(rows - 1, false);
        Paint(view);

        Assert.That(view.AccessibilityObject.Value, Does.EndWith(" to " + rows + " of " + rows));
    }

    [TestCase(Keys.Down)]
    [TestCase(Keys.Up)]
    [TestCase(Keys.Down | Keys.Shift)]
    [TestCase(Keys.PageDown)]
    [TestCase(Keys.Home)]
    public void MovementKeysBelongToTheGrid(Keys key)
    {
        // Otherwise Windows takes the arrows to mean "next control", and after clicking a
        // row the first Down moved focus to the minimap. Found by the end-to-end run.
        using var view = new LogView();
        var isInput = (bool)typeof(LogView).GetMethod("IsInputKey", BindingFlags.NonPublic | BindingFlags.Instance)
                                           .Invoke(view, [key]);

        Assert.That(isInput, Is.True);
    }

    private static FilterResult ResultOf(IEnumerable<int> lines)
    {
        var res = new FilterResult { SrcCounts = new int[1] };
        res.View.AddRange(lines);
        return res;
    }

    [Test]
    public void SwitchingSeveritiesBackOnKeepsThePickedRowWhereItWas()
    {
        // The team's report: errors only, pick one, switch the other severities back on —
        // and the view jumped somewhere else. Here every tenth line is an "error".
        using var view = Attached(400, "short line");
        var doc = (LogDocument)typeof(LogView).GetField("_file", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(view);
        var errorsOnly = ResultOf(Enumerable.Range(0, 40).Select(k => k * 10));
        view.Attach(doc, errorsOnly);
        view.ScrollTo(20, false);
        view.Select(250, false);                   // the 26th error, 5 rows down the window
        Paint(view);
        var rowsAbove = view.ViewPosOf(250) - view.TopRow;
        Assume.That(rowsAbove, Is.EqualTo(5));

        view.Attach(doc, ResultOf(Enumerable.Range(0, 400)));   // every severity back on

        Assert.That(view.Selected, Is.EqualTo(250), "Still selected.");
        Assert.That(view.ViewPosOf(250) - view.TopRow, Is.EqualTo(rowsAbove), "And still 5 rows down the window.");
    }

    [Test]
    public void APickedRowTheNewFilterHidesKeepsThePlaceNearIt()
    {
        using var view = Attached(400, "short line");
        var doc = (LogDocument)typeof(LogView).GetField("_file", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(view);
        view.ScrollTo(240, false);
        view.Select(245, false);
        Paint(view);

        view.Attach(doc, ResultOf(Enumerable.Range(0, 40).Select(k => k * 10)));   // 245 is filtered out

        Assert.That(doc.N, Is.EqualTo(400));
        Assert.That(view.TopRow, Is.InRange(view.ViewPosOf(240) - 6, view.ViewPosOf(240)),
            "The view stays around line 240, the nearest one still shown, instead of jumping.");
    }

    [Test]
    public void TurningWrapOffBringsTheRangeBack()
    {
        var rows = Rows(new LogView { Width = ViewWidth, Height = ViewHeight });
        using var view = Attached(rows);
        view.Wrap = true;
        view.ScrollBy(1000);

        view.Wrap = false;

        Assert.That(view.TopRow, Is.EqualTo(0));
    }
}
