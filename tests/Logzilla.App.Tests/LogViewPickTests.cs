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
        "\"Region\":\"eu\",\"CorrelationId\":\"136ea017-3946\",\"ThreadId\":7}";

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
}
