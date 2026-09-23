using System.Reflection;
using Logzilla.App.Controls;
using NUnit.Framework;

namespace Logzilla.App.Tests;

/// <summary>
/// The horizontal bar under the log grid.
///
/// It is sized from the widest message drawn this frame. ThinScrollBar derives its
/// own scrollable range as Maximum - LargeChange + 1, so handing it the *overflow*
/// as Maximum left nothing to scroll unless the text ran past twice the column
/// width. JSON messages usually do; a plain-text line that overflows by a word or
/// two does not, which is why the bar went missing on plain-text logs. Maximum is
/// the whole message width instead.
/// </summary>
[TestFixture]
public sealed class LogViewHorizontalScrollTests
{
    private const int ViewWidth = 900;

    private static ThinScrollBar Bar(LogView view) =>
        (ThinScrollBar)typeof(LogView).GetField("_h", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(view);

    private static float CharWidth(LogView view) =>
        (float)typeof(LogView).GetField("_cw", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(view);

    private static int MessageColumnWidth(LogView view) =>
        (int)typeof(LogView).GetProperty("MessageWidth", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(view);

    private static int VerticalBarWidth(LogView view) =>
        ((ThinScrollBar)typeof(LogView).GetField("_v", BindingFlags.NonPublic | BindingFlags.Instance)
                                       .GetValue(view)).Width;

    /// <summary>Lays the bar out for a longest-message of the given character count.
    /// OnPaint hands SyncHorizontal the width with the vertical bar already taken
    /// off, so this does the same.</summary>
    private static ThinScrollBar SyncFor(LogView view, int messageChars)
    {
        typeof(LogView).GetField("_maxMsgChars", BindingFlags.NonPublic | BindingFlags.Instance)
                       .SetValue(view, messageChars);
        typeof(LogView).GetMethod("SyncHorizontal", BindingFlags.NonPublic | BindingFlags.Instance)
                       .Invoke(view, [ViewWidth - VerticalBarWidth(view)]);
        return Bar(view);
    }

    private static LogView NewView()
    {
        var view = new LogView();
        view.Width = ViewWidth;
        view.Height = 600;
        return view;
    }

    /// <summary>Characters that just overflow the message column by the given factor.</summary>
    private static int CharsForOverflow(LogView view, double factor) =>
        (int)(MessageColumnWidth(view) * factor / CharWidth(view)) + 8;

    [Test]
    public void AMessageThatFitsLeavesNothingToScroll()
    {
        using var view = NewView();
        var bar = SyncFor(view, 4);

        Assert.That(bar.Enabled, Is.False);
        bar.Value = 10_000;
        Assert.That(bar.Value, Is.EqualTo(0));
    }

    [Test]
    public void AMessageThatOverflowsSlightlyCanStillBeScrolled()
    {
        // The reported bug: 1.2x the column is real overflow, and used to produce a
        // dead bar because the derived range came out negative.
        using var view = NewView();
        var bar = SyncFor(view, CharsForOverflow(view, 1.2));

        Assert.That(bar.Enabled, Is.True, "A line wider than its column must be scrollable.");
        bar.Value = 100_000;
        Assert.That(bar.Value, Is.GreaterThan(0),
            "The bar has to travel somewhere, not pin itself at zero.");
    }

    [Test]
    public void TheBarTravelsExactlyTheHiddenPixels()
    {
        using var view = NewView();
        var avail = MessageColumnWidth(view);
        var chars = CharsForOverflow(view, 2.5);
        var bar = SyncFor(view, chars);
        var need = (int)(chars * CharWidth(view));

        bar.Value = int.MaxValue;
        Assert.That(bar.Value, Is.EqualTo(need - avail).Within(2),
            "Scrolled fully right, the last character of the message should be at the right edge.");
    }

    [Test]
    public void AWiderMessageScrollsFurtherThanANarrowOne()
    {
        using var view = NewView();

        var small = SyncFor(view, CharsForOverflow(view, 1.5));
        small.Value = int.MaxValue;
        var smallTravel = small.Value;

        var large = SyncFor(view, CharsForOverflow(view, 6.0));
        large.Value = int.MaxValue;

        Assert.That(large.Value, Is.GreaterThan(smallTravel));
    }

    [Test]
    public void WrappingTurnsTheBarOff()
    {
        using var view = NewView();
        view.Wrap = true;
        var bar = SyncFor(view, CharsForOverflow(view, 6.0));

        Assert.That(bar.Enabled, Is.False,
            "There is nothing left to scroll to once every line is on screen.");
    }
}
