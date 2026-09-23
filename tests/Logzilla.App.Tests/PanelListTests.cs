using System.Collections.Generic;
using System.Reflection;
using Logzilla.App.Controls;
using NUnit.Framework;

namespace Logzilla.App.Tests;

/// <summary>
/// PanelList keeps its own ThinScrollBar in sync with the item count and the
/// panel's height (Sync(), called from SetItems/OnResize/OnFontChanged). The
/// scrollbar is a private field, so it's read through reflection; Sync() itself
/// is invoked directly for the resize scenario so the test doesn't depend on
/// whether a handle exists to raise WinForms' own Resize event.
/// </summary>
[TestFixture]
public sealed class PanelListTests
{
    private static ThinScrollBar GetScrollBar(PanelList list)
    {
        var field = typeof(PanelList).GetField("_sb", BindingFlags.NonPublic | BindingFlags.Instance);
        return (ThinScrollBar)field.GetValue(list);
    }

    private static void InvokeSync(PanelList list)
    {
        var method = typeof(PanelList).GetMethod("Sync", BindingFlags.NonPublic | BindingFlags.Instance);
        method.Invoke(list, null);
    }

    private static List<PanelRow> Rows(int count)
    {
        var rows = new List<PanelRow>();
        for (var i = 0; i < count; i++)
        {
            rows.Add(new PanelRow("label " + i, i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        return rows;
    }

    [Test]
    public void FewItemsInATallPanelHideTheScrollbar()
    {
        var list = new PanelList { Height = 1000 };
        list.SetItems(Rows(3));
        var sb = GetScrollBar(list);
        Assert.That(sb.Visible, Is.False);
        Assert.That(sb.Maximum, Is.EqualTo(2));
    }

    [Test]
    public void ManyItemsInAShortPanelShowTheScrollbar()
    {
        var list = new PanelList { Height = 40 };
        list.SetItems(Rows(100));
        var sb = GetScrollBar(list);
        Assert.That(sb.Visible, Is.True);
        Assert.That(sb.Maximum, Is.EqualTo(99));
        Assert.That(sb.Value, Is.EqualTo(0));
    }

    [Test]
    public void GrowingTallerReclampsAScrolledPosition()
    {
        var list = new PanelList { Height = 40 };
        list.SetItems(Rows(100));
        var sb = GetScrollBar(list);
        sb.Value = sb.Maximum; // scroll all the way down first

        list.Height = 10_000; // comfortably tall enough that every item fits
        InvokeSync(list);

        Assert.That(sb.Visible, Is.False);
        Assert.That(sb.Value, Is.EqualTo(0));
    }

    [Test]
    public void SettingItemsAlwaysResetsScrollPositionToTheTop()
    {
        var list = new PanelList { Height = 40 };
        list.SetItems(Rows(100));
        var sb = GetScrollBar(list);
        sb.Value = 50;

        list.SetItems(Rows(100));
        Assert.That(sb.Value, Is.EqualTo(0));
    }
}
