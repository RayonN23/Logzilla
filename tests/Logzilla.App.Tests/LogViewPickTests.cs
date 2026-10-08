using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using Logzilla.App.Controls;
using Logzilla.Core.Implementation;
using Logzilla.Core.Logs;
using Logzilla.Core.Query;
using NUnit.Framework;

namespace Logzilla.App.Tests;

/// <summary>
/// What a click lands on. A coworker selected a CorrelationId and pressed Ctrl+C and got
/// something else: the grid took the word from the row's raw text at the pointer's
/// column, which for a JSON record is not what is drawn, and ignored which line of an
/// expanded row — or which cell of its field list — was clicked. It now reads the text
/// as it was drawn, and a value in the field list copies with one click.
/// </summary>
[TestFixture]
public sealed class LogViewPickTests
{
    private const int ViewWidth = 900;
    private const int ViewHeight = 400;

    private const string Record =
        "{\"@t\":\"2026-06-18T16:33:46Z\",\"@mt\":\"Device {DeviceId} checked in\",\"DeviceId\":\"ABC-42\"," +
        "\"Name\":\"CheckIn\",\"SourceContext\":\"Soti.Service\",\"Feature\":\"DeviceAction\",\"Tenant\":\"acme\"," +
        "\"Region\":\"eu\",\"CorrelationId\":\"136ea017-3946\",\"ThreadId\":7," +
        "\"RequestPath\":\"/DeviceManagement.svc/WindowsDesktop/Windows10RS/Enrollment/DeviceEnrollmentService/Policies/CheckIn?attempt=3&retry=true\"}";

    private sealed class JsonReader : ILineReader
    {
        public int Locate(int index, out byte[] buffer)
        {
            buffer = Encoding.UTF8.GetBytes(Record);
            return 0;
        }

        public string Text(int index) => Record;

        public void Dispose() { }
    }

    private sealed class FakeIndexer : ILogIndexer
    {
        public LogSource Prepare(string path) => throw new NotSupportedException();

        public void Run(LogDocument document, int sourceIndex, Action<long, long> progress, Func<bool> cancelled) =>
            throw new NotSupportedException();

        public LogDocument Merge(IReadOnlyList<LogDocument> documents) => throw new NotSupportedException();

        public void Reorder(LogDocument document) => throw new NotSupportedException();

        public ILineReader OpenReader(LogDocument document, int bufferSize) => new JsonReader();
    }

    private static LogView Selected()
    {
        var doc = new LogDocument { Name = "clef.log" };
        doc.Srcs.Add(new LogSource { Path = "clef.log", Name = "clef.log", Label = "clef", IsJson = true, Encoding = Encoding.UTF8 });
        doc.Sev[0] = 3;
        doc.Len[0] = Record.Length;
        doc.Ts[0] = double.NaN;
        doc.N = 1;
        var res = new FilterResult { SrcCounts = new int[1] };
        res.View.Add(0);

        var view = new LogView
        {
            Width = ViewWidth, Height = ViewHeight, Indexer = new FakeIndexer(),
            JsonReader = CoreServices.CreateJsonLogReader(),
        };
        view.Attach(doc, res);
        view.Select(0, false);
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

    /// <summary>The text runs the last paint recorded: (bounds, text, origin x, field key).</summary>
    private static List<(Rectangle Bounds, string Text, int OriginX, string Key)> Runs(LogView view)
    {
        var list = (IEnumerable)typeof(LogView).GetField("_runs", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(view);
        var result = new List<(Rectangle, string, int, string)>();
        foreach (var run in list)
        {
            var t = run.GetType();
            result.Add(((Rectangle)t.GetProperty("Bounds").GetValue(run), (string)t.GetProperty("Text").GetValue(run),
                (int)t.GetProperty("OriginX").GetValue(run), (string)t.GetProperty("FieldKey").GetValue(run)));
        }
        return result;
    }

    private static Point Centre(Rectangle r) => new(r.X + Math.Min(r.Width / 2, 40), r.Y + r.Height / 2);

    [Test]
    public void TheExpandedRowListsTheIdsPeopleFollowEvenOnABusyRecord()
    {
        using var view = Selected();
        var keys = Runs(view).Where(r => r.Key != null).Select(r => r.Key).ToList();

        Assert.That(keys, Does.Contain("CorrelationId"));
        Assert.That(keys, Does.Contain("ThreadId"), "Six fields used to be shown, and the thread was the one left off.");
        Assert.That(keys[0], Is.EqualTo("CorrelationId"), "The id a request is followed by comes first.");
    }

    [Test]
    public void PickingAFieldValueGivesTheWholeValueAndItsKey()
    {
        using var view = Selected();
        var cell = Runs(view).Single(r => r.Key == "CorrelationId");

        var word = view.PickAt(Centre(cell.Bounds), out var key);

        Assert.That(word, Is.EqualTo("136ea017-3946"));
        Assert.That(key, Is.EqualTo("CorrelationId"));
    }

    [Test]
    public void ClickingAFieldValueCopiesExactlyThatValue()
    {
        using var view = Selected();
        var cell = Runs(view).Single(r => r.Key == "CorrelationId");
        LogView.FieldCopyEventArgs copied = null;
        view.CopyFieldRequested += (_, a) => copied = a;

        typeof(LogView).GetMethod("OnMouseDown", BindingFlags.NonPublic | BindingFlags.Instance)
                       .Invoke(view, [new MouseEventArgs(MouseButtons.Left, 1, Centre(cell.Bounds).X, Centre(cell.Bounds).Y, 0)]);

        Assert.That(copied, Is.Not.Null);
        Assert.That(copied.Key, Is.EqualTo("CorrelationId"));
        Assert.That(copied.Value, Is.EqualTo("136ea017-3946"));
    }

    [Test]
    public void PickingInTheMessageGivesTheWordDrawnThereNotOneFromTheRawRecord()
    {
        using var view = Selected();
        var message = Runs(view).First(r => r.Key == null && r.Text.Contains("checked", StringComparison.Ordinal));
        var col = message.Text.IndexOf("checked", StringComparison.Ordinal) + 2;
        var cw = (float)typeof(LogView).GetField("_cw", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(view);
        var at = new Point(message.OriginX + (int)(col * cw + cw / 2), message.Bounds.Y + message.Bounds.Height / 2);

        var word = view.PickAt(at, out var key);

        Assert.That(word, Is.EqualTo("checked"));
        Assert.That(key, Is.Null);
    }

    private static void Click(LogView view, Point at, int clicks = 1) =>
        typeof(LogView).GetMethod("OnMouseDown", BindingFlags.NonPublic | BindingFlags.Instance)
                       .Invoke(view, [new MouseEventArgs(MouseButtons.Left, clicks, at.X, at.Y, 0)]);

    /// <summary>A point on the selected row's first line, clear of the field list.</summary>
    private static Point OnTheHeaderLine(LogView view) => new(ViewWidth / 3, view.RowHeight / 2);

    [Test]
    public void ClickingTheExpandedRowAgainFoldsItAndOnceMoreOpensIt()
    {
        // The team's report: clicking an expanded line again did nothing; it stayed open.
        using var view = Selected();
        Assert.That(view.IsExpanded, Is.True);

        Click(view, OnTheHeaderLine(view));
        Paint(view);

        Assert.That(view.IsExpanded, Is.False, "The second click folds it back to one line.");
        Assert.That(view.Selected, Is.EqualTo(0), "It stays selected.");
        Assert.That(Runs(view).Any(r => r.Key != null), Is.False, "The field list is gone.");

        Click(view, OnTheHeaderLine(view));

        Assert.That(view.IsExpanded, Is.True, "And a third opens it again.");
    }

    [Test]
    public void EscapeFoldsTheExpandedRow()
    {
        using var view = Selected();

        Assert.That(view.Collapse(), Is.True);
        Assert.That(view.IsExpanded, Is.False);
        Assert.That(view.Collapse(), Is.False, "Nothing left to fold.");
    }

    [Test]
    public void AQuickSelectThenDeselectFoldsTheRowHoweverFastTheClicks()
    {
        // The team's report: selecting and deselecting quickly was flaky. Two quick clicks
        // are a double-click to Windows, and the second click used to be taken as "pick a
        // word into Find": the row stayed open and the view re-filtered on that word.
        using var view = Selected();
        view.ClearSelection();
        Paint(view);
        var at = OnTheHeaderLine(view);
        var up = typeof(LogView).GetMethod("OnMouseUp", BindingFlags.NonPublic | BindingFlags.Instance);

        Click(view, at);                                    // select: it opens
        up.Invoke(view, [new MouseEventArgs(MouseButtons.Left, 1, at.X, at.Y, 0)]);
        Assert.That(view.IsExpanded, Is.True);
        Paint(view);
        Click(view, at, clicks: 2);                         // the quick second click: it folds
        up.Invoke(view, [new MouseEventArgs(MouseButtons.Left, 2, at.X, at.Y, 0)]);

        Assert.That(view.Selected, Is.EqualTo(0));
        Assert.That(view.IsExpanded, Is.False);
        Assert.That(typeof(LogView).GetEvent("WordPicked"), Is.Null, "Double-click no longer searches for the word under it.");
    }

    [Test]
    public void ALongFieldValueWrapsInsteadOfBeingCutOff()
    {
        // "Soti.MobiControl.WindowsModern.Ser..." — a value wider than its column used to
        // end in "...". It now takes the lines it needs, and copies whole.
        using var view = Selected();
        var path = Runs(view).Single(r => r.Key == "RequestPath");
        var corr = Runs(view).Single(r => r.Key == "CorrelationId");

        Assert.That(path.Bounds.Height, Is.GreaterThan(corr.Bounds.Height), "The long value's row grew to fit it.");
        Assert.That(path.Text, Does.EndWith("retry=true"), "And the value is all there.");
    }
}
