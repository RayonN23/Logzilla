using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Text;
using Logzilla.App.Controls;
using Logzilla.App.Infrastructure;
using Logzilla.Core.Implementation;
using Logzilla.Core.Logs;
using Logzilla.Core.Query;
using NUnit.Framework;

namespace Logzilla.App.Tests;

/// <summary>
/// The Highlighting tab's rules: they must keep showing once a line is clicked open,
/// and the rules older versions added by themselves are taken out.
/// </summary>
[TestFixture]
public sealed class HighlightingTests
{
    private const int ViewWidth = 900;
    private const int ViewHeight = 300;
    private const string Line = "2026-03-01 08:00:00.593 WARN  [auth] Slow query detected latency=984ms";

    private sealed class OneLine : ILineReader
    {
        public int Locate(int index, out byte[] buffer)
        {
            buffer = Encoding.UTF8.GetBytes(Line);
            return 0;
        }

        public string Text(int index) => Line;

        public void Dispose() { }
    }

    private sealed class FakeIndexer : ILogIndexer
    {
        public LogSource Prepare(string path) => throw new System.NotSupportedException();

        public void Run(LogDocument document, int sourceIndex, System.Action<long, long> progress, System.Func<bool> cancelled) =>
            throw new System.NotSupportedException();

        public LogDocument Merge(IReadOnlyList<LogDocument> documents) => throw new System.NotSupportedException();

        public void Reorder(LogDocument document) => throw new System.NotSupportedException();

        public ILineReader OpenReader(LogDocument document, int bufferSize) => new OneLine();
    }

    private static readonly string[] KeptRules = ["Slow query", "exception"];

    private static readonly Color Rule = Color.FromArgb(255, 0, 255);   // a colour nothing else uses

    private static LogView Viewing()
    {
        var doc = new LogDocument { Name = "t.log" };
        doc.Srcs.Add(new LogSource { Path = "t.log", Name = "t.log", Label = "t", Encoding = Encoding.UTF8 });
        doc.Sev[0] = 4;
        doc.Len[0] = Line.Length;
        doc.Ts[0] = double.NaN;
        doc.Skip[0] = 30;
        doc.N = 1;
        var res = new FilterResult { SrcCounts = new int[1] };
        res.View.Add(0);
        var view = new LogView
        {
            Width = ViewWidth, Height = ViewHeight, Indexer = new FakeIndexer(), JsonReader = CoreServices.CreateJsonLogReader(),
            Rules = [new HighlightRule { Pattern = "Slow query", C = Rule, On = true }],
        };
        view.Attach(doc, res);
        return view;
    }

    /// <summary>How many pixels the rule's underline put on screen.</summary>
    private static int RulePixels(LogView view)
    {
        using var bitmap = new Bitmap(ViewWidth, ViewHeight);
        using (var g = Graphics.FromImage(bitmap))
        {
            typeof(LogView).GetMethod("OnPaint", BindingFlags.NonPublic | BindingFlags.Instance)
                           .Invoke(view, [new System.Windows.Forms.PaintEventArgs(g, new Rectangle(0, 0, ViewWidth, ViewHeight))]);
        }
        var n = 0;
        for (var y = 0; y < ViewHeight; y++)
        {
            for (var x = 0; x < ViewWidth; x++)
            {
                if (bitmap.GetPixel(x, y).ToArgb() == Rule.ToArgb())
                {
                    n++;
                }
            }
        }
        return n;
    }

    [Test]
    public void AHighlightStillShowsOnceItsLineIsClickedOpen()
    {
        // The team's report: click a highlighted line and the highlight went away.
        using var view = Viewing();
        Assume.That(RulePixels(view), Is.GreaterThan(0), "Highlighted while the line is collapsed.");

        view.Select(0, false);

        Assert.That(view.IsExpanded, Is.True);
        Assert.That(RulePixels(view), Is.GreaterThan(0), "And still highlighted with the line open.");
    }

    private static List<HighlightRule> Remove(params HighlightRule[] rules)
    {
        var list = new List<HighlightRule>(rules);
        typeof(Dpi).Assembly.GetType("Logzilla.App.Views.MainForm")
                   .GetMethod("RemoveOldDefaultRules", BindingFlags.NonPublic | BindingFlags.Static)
                   .Invoke(null, [list]);
        return list;
    }

    [Test]
    public void TheRulesOldVersionsAddedByThemselvesAreTakenOut()
    {
        var left = Remove(
            new HighlightRule { Pattern = "exception|traceback|stack trace", IsRegex = true },
            new HighlightRule { Pattern = "timeout|timed out|refused|unreachable", IsRegex = true },
            new HighlightRule { Pattern = "\\b\\d{1,3}(?:\\.\\d{1,3}){3}\\b", IsRegex = true });

        Assert.That(left, Is.Empty);
    }

    [Test]
    public void TheUsersOwnRulesStay()
    {
        var left = Remove(
            new HighlightRule { Pattern = "exception|traceback|stack trace", IsRegex = true },
            new HighlightRule { Pattern = "Slow query", IsRegex = false },
            new HighlightRule { Pattern = "exception", IsRegex = true });

        Assert.That(left.ConvertAll(r => r.Pattern), Is.EqualTo(KeptRules));
    }
}
