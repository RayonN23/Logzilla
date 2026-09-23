using System.Collections.Generic;
using System.Reflection;
using Logzilla.App.Controls;
using NUnit.Framework;

namespace Logzilla.App.Tests;

/// <summary>
/// MiniMap.BinarySearch locates a bookmark's position within the current filtered
/// view so it can be drawn as a tick. A wrong result here silently hides or
/// misplaces bookmarks on the map, with nothing else to catch it.
/// </summary>
[TestFixture]
public sealed class MiniMapTests
{
    private static int Search(List<int> view, int target)
    {
        var method = typeof(MiniMap).GetMethod("BinarySearch", BindingFlags.NonPublic | BindingFlags.Static);
        return (int)method.Invoke(null, [view, target]);
    }

    [Test]
    public void FindsAValueInTheMiddle()
    {
        Assert.That(Search([10, 20, 30, 40, 50], 30), Is.EqualTo(2));
    }

    [Test]
    public void FindsTheFirstAndLastElements()
    {
        var view = new List<int> { 10, 20, 30, 40, 50 };
        Assert.That(Search(view, 10), Is.EqualTo(0));
        Assert.That(Search(view, 50), Is.EqualTo(4));
    }

    [Test]
    public void ReturnsMinusOneWhenTheValueIsAbsent()
    {
        Assert.That(Search([10, 20, 30, 40, 50], 25), Is.EqualTo(-1));
    }

    [Test]
    public void ReturnsMinusOneForAnEmptyView()
    {
        Assert.That(Search([], 5), Is.EqualTo(-1));
    }

    [Test]
    public void ReturnsMinusOneWhenTheValueIsOutsideTheRange()
    {
        var view = new List<int> { 10, 20, 30 };
        Assert.That(Search(view, 0), Is.EqualTo(-1));
        Assert.That(Search(view, 100), Is.EqualTo(-1));
    }
}
