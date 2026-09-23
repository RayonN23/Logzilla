using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using Logzilla.App.Controls;
using Logzilla.App.Infrastructure;
using NUnit.Framework;

namespace Logzilla.App.Tests;

/// <summary>
/// The inspector used to cut long values off with an ellipsis, which hid the
/// loggers, namespaces and paths the panel exists to show. WrapLabel is the
/// replacement; it is a private static, called here through reflection.
/// </summary>
[TestFixture]
public sealed class PanelListWrapTests
{
    private static List<string> WrapLabel(string text, Font font, int width, int maxLines)
    {
        var m = typeof(PanelList).GetMethod("WrapLabel", BindingFlags.NonPublic | BindingFlags.Static);
        return (List<string>)m.Invoke(null, [text, font, width, maxLines]);
    }

    private static Font Mono => Theme.MonoBody;

    [Test]
    public void AShortValueStaysOnOneLine()
    {
        var lines = WrapLabel("INFO", Mono, 300, 4);
        Assert.That(lines, Has.Count.EqualTo(1));
        Assert.That(lines[0], Is.EqualTo("INFO"));
    }

    [Test]
    public void ALongValueWrapsInsteadOfBeingCutOff()
    {
        const string logger = "Soti.MobiControl.WindowsModern.Service.Implementation.MaintenanceCommandManager";
        var lines = WrapLabel(logger, Mono, 160, 4);
        Assert.That(lines.Count, Is.GreaterThan(1), "A value far wider than the column should wrap.");
        Assert.That(string.Concat(lines), Does.Not.Contain("…"));
    }

    [Test]
    public void WrappedLinesReconstructTheOriginalValue()
    {
        const string text = "logger=Soti.MobiControl.Service deviceId=abc-123 thread=worker-6";
        var lines = WrapLabel(text, Mono, 140, 8);
        // Word-boundary breaks trim the trailing space, so compare without spaces.
        var rebuilt = string.Join(" ", lines).Replace(" ", "");
        Assert.That(rebuilt, Is.EqualTo(text.Replace(" ", "")));
    }

    [Test]
    public void EachLineFitsInsideTheColumn()
    {
        const int width = 150;
        var lines = WrapLabel(new string('a', 40) + " " + new string('b', 40), Mono, width, 8);
        foreach (var line in lines)
        {
            Assert.That(Dpi.Text(line, Mono).Width, Is.LessThanOrEqualTo(width + 2),
                $"Line '{line}' overflows the {width}px column.");
        }
    }

    [Test]
    public void LineCountIsCappedSoOneValueCannotFillThePanel()
    {
        var lines = WrapLabel(new string('x', 4000), Mono, 100, 4);
        Assert.That(lines, Has.Count.LessThanOrEqualTo(4));
    }

    [Test]
    public void AWordLongerThanTheColumnIsBrokenRatherThanDropped()
    {
        var lines = WrapLabel(new string('z', 200), Mono, 80, 8);
        Assert.That(lines.Count, Is.GreaterThan(1));
        Assert.That(lines[0], Is.Not.Empty);
    }

    [Test]
    public void EmptyAndNullValuesProduceASingleEmptyLine()
    {
        Assert.That(WrapLabel("", Mono, 200, 4), Has.Count.EqualTo(1));
        Assert.That(WrapLabel(null, Mono, 200, 4), Has.Count.EqualTo(1));
    }

    [Test]
    public void AZeroWidthColumnDoesNotHang()
    {
        Assert.DoesNotThrow(() => WrapLabel("some value", Mono, 0, 4));
    }

    [Test]
    public void PrefersBreakingAtAWordBoundary()
    {
        var lines = WrapLabel("alpha bravo charlie delta echo foxtrot", Mono, 120, 8);
        Assert.That(lines.Count, Is.GreaterThan(1));
        // No line should start mid-word when a boundary was available.
        for (var k = 1; k < lines.Count; k++)
        {
            Assert.That(lines[k], Does.Not.StartWith(" "));
        }
    }
}
