using System.Drawing;
using System.Reflection;
using Logzilla.App.Infrastructure;
using NUnit.Framework;

namespace Logzilla.App.Tests;

/// <summary>
/// Palette.Pick assigns a stable tint per merged source, wrapping once sources
/// outnumber the tint list. The tint count is read via reflection rather than
/// hardcoded, so this keeps passing if the palette is resized later.
/// </summary>
[TestFixture]
public sealed class PaletteTests
{
    private static Color[] Tints()
    {
        var field = typeof(Palette).GetField("SourceTints", BindingFlags.NonPublic | BindingFlags.Static);
        return (Color[])field.GetValue(null);
    }

    [Test]
    public void PicksTheMatchingTintForEachIndexInRange()
    {
        var tints = Tints();
        for (var i = 0; i < tints.Length; i++)
        {
            Assert.That(Palette.Pick(i), Is.EqualTo(tints[i]));
        }
    }

    [Test]
    public void WrapsAroundOnceTheIndexReachesTheTintCount()
    {
        var tints = Tints();
        Assert.That(Palette.Pick(tints.Length), Is.EqualTo(tints[0]));
        Assert.That(Palette.Pick(tints.Length + 1), Is.EqualTo(tints[1]));
    }

    [Test]
    public void WrapsBackwardForNegativeIndices()
    {
        var tints = Tints();
        Assert.That(Palette.Pick(-1), Is.EqualTo(tints[tints.Length - 1]));
    }
}
