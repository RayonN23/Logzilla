using System;
using System.Linq;
using System.Reflection;
using Logzilla.App.Controls;
using Logzilla.App.Infrastructure;
using NUnit.Framework;

namespace Logzilla.App.Tests;

/// <summary>
/// The inspector's tab strip. Three panels — sources, detail and colors — used to
/// live behind a "⋯" overflow menu; they now have tabs of their own, which only
/// works if the strip still fits the inspector's width. Building a MainForm in a
/// test would open files and start threads, so the tab list is read off the type
/// and the widths are measured with the same controls the layout builds.
/// </summary>
[TestFixture]
public sealed class SideTabsTests
{
    private static Type MainForm =>
        typeof(Dpi).Assembly.GetType("Logzilla.App.Views.MainForm", throwOnError: true);

    private static string[] SideNames()
    {
        var f = MainForm.GetField("SideNames", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.That(f, Is.Not.Null, "MainForm.SideNames is gone; the tab strip is built from something else now.");
        return (string[])f.GetValue(null);
    }

    private static int SideTabPadX()
    {
        var f = MainForm.GetField("SideTabPadX", BindingFlags.NonPublic | BindingFlags.Static);
        return (int)f.GetRawConstantValue();
    }

    private static string Cap1(string s)
    {
        var m = MainForm.GetMethod("Cap1", BindingFlags.NonPublic | BindingFlags.Static);
        return (string)m.Invoke(null, [s]);
    }

    private static readonly string[] Expected =
        { "summary", "detail", "fields", "patterns", "marks", "sources", "colors" };

    [Test]
    public void EveryPanelHasATabOfItsOwn()
    {
        Assert.That(SideNames(), Is.EqualTo(Expected));
    }

    [Test]
    public void DetailIsTheSecondTab()
    {
        Assert.That(SideNames()[1], Is.EqualTo("detail"));
    }

    [Test]
    public void SourcesDetailAndColorsAreNoLongerHiddenBehindAnOverflowMenu()
    {
        var names = SideNames();
        Assert.Multiple(() =>
        {
            Assert.That(names, Does.Contain("sources"));
            Assert.That(names, Does.Contain("detail"));
            Assert.That(names, Does.Contain("colors"));
        });
        Assert.That(MainForm.GetField("OverflowSideNames", BindingFlags.NonPublic | BindingFlags.Static),
            Is.Null, "An overflow tab list is back; every panel is supposed to have its own tab.");
        Assert.That(MainForm.GetMethod("ShowSidePanelOverflow", BindingFlags.NonPublic | BindingFlags.Instance),
            Is.Null, "The overflow menu is back; every panel is supposed to have its own tab.");
    }

    [Test]
    public void NoPanelIsListedTwice()
    {
        var names = SideNames();
        Assert.That(names.Distinct().Count(), Is.EqualTo(names.Length));
    }

    [Test]
    public void EveryTabIsReachableByItsCtrlNumberShortcut()
    {
        // MainForm.Input maps Ctrl+1..Ctrl+9 onto this list by index.
        Assert.That(SideNames(), Has.Length.LessThanOrEqualTo(9));
    }

    [Test]
    public void TheWholeStripFitsOneRowAtTheInspectorsWidth()
    {
        var strip = SideNames().Sum(TabWidth) + Dpi.S(12 - 8); // the strip's own left inset
        Assert.That(strip, Is.LessThanOrEqualTo(Dpi.S(Theme.InspectorWidth)),
            "The seven tabs no longer fit one row; widen the inspector or tighten SideTabPadX.");
    }

    [Test]
    public void ATabIsWideEnoughToShowItsWholeLabel()
    {
        // The tabs are text, not icons: a truncated "Patterns" is the reason they are
        // sized from the measured label rather than a fixed column.
        foreach (var name in SideNames())
        {
            var label = Dpi.Text(Cap1(name), Theme.UiBody).Width;
            Assert.That(TabWidth(name), Is.GreaterThanOrEqualTo(label), name);
        }
    }

    private static int TabWidth(string name)
    {
        var b = new FlatButton { Text = Cap1(name), Style = FlatButton.Look.Tab };
        b.FitTo(40, Theme.TitleBarHeight, SideTabPadX());
        return b.Width;
    }
}
