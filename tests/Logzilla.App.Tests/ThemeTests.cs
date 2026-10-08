using System.Drawing;
using Logzilla.App.Infrastructure;
using NUnit.Framework;

namespace Logzilla.App.Tests;

/// <summary>
/// Guards the redesign brief's hard requirements: which severities are "neutral"
/// (INFO must not shout), the exact row/column geometry the brief calls out as
/// fixed, and the handful of hex tokens the brief treats as spec rather than taste.
/// </summary>
[TestFixture]
public sealed class ThemeTests
{
    [TestCase(0, true)]  // none
    [TestCase(1, false)] // trace
    [TestCase(2, false)] // debug
    [TestCase(3, true)]  // info
    [TestCase(4, false)] // warn
    [TestCase(5, false)] // error
    [TestCase(6, false)] // fatal
    public void IsNeutralMatchesTheSeverityTable(int severity, bool expectedNeutral)
    {
        Assert.That(Theme.IsNeutral(severity), Is.EqualTo(expectedNeutral));
    }

    [Test]
    public void SeverityAndMessageColorArraysCoverAllSevenLevels()
    {
        Assert.That(Theme.SevColor, Has.Length.EqualTo(7));
        Assert.That(Theme.MsgColor, Has.Length.EqualTo(7));
    }

    // The brief's layout table gives these as exact, not fluid, and calls out
    // that a blank INFO level tag must never shift the message column.
    [TestCase(nameof(Theme.TitleBarHeight), 40)]
    [TestCase(nameof(Theme.CommandBarHeight), 52)]
    [TestCase(nameof(Theme.FilterBarHeight), 44)]
    [TestCase(nameof(Theme.TimelineHeight), 96)]
    [TestCase(nameof(Theme.StatusBarHeight), 28)]
    [TestCase(nameof(Theme.InspectorWidth), 440)]
    [TestCase(nameof(Theme.RowHeight), 20)]
    [TestCase(nameof(Theme.ColGutter), 46)]
    [TestCase(nameof(Theme.ColSpine), 3)]
    [TestCase(nameof(Theme.ColLevel), 46)]
    [TestCase(nameof(Theme.MinimapWidth), 11)]
    public void LayoutMetricMatchesTheBrief(string metricName, int expected)
    {
        var field = typeof(Theme).GetField(metricName);
        Assert.That(field, Is.Not.Null, $"Theme.{metricName} no longer exists.");
        Assert.That((int)field.GetValue(null), Is.EqualTo(expected));
    }

    [Test]
    public void BgDataIsTheDarkestSurfaceInTheBrief()
    {
        Assert.That(Theme.BgData, Is.EqualTo(FromHex(0x0E1114)));
    }

    [Test]
    public void AccentMatchesTheBrief()
    {
        Assert.That(Theme.Accent, Is.EqualTo(FromHex(0x56C8DE)));
    }

    [Test]
    public void TextDimIsTheContrastFloorFromTheBrief()
    {
        // "#7A8390 is the floor. Anything dimmer than that on bg-data fails
        // WCAG AA for small text."
        Assert.That(Theme.TextDim, Is.EqualTo(FromHex(0x7A8390)));
    }

    [Test]
    public void WarnSeverityColorMatchesTheBrief()
    {
        Assert.That(Theme.SevColor[4], Is.EqualTo(FromHex(0xF0B341)));
    }

    [Test]
    public void ApplySystemPreferencesIsANoOpOutsideHighContrast()
    {
        Assume.That(!System.Windows.Forms.SystemInformation.HighContrast,
            "This machine runs a high-contrast scheme; the no-op branch isn't exercised here.");
        var before = Theme.BgData;
        var beforeAccent = Theme.Accent;
        Theme.ApplySystemPreferences();
        Assert.That(Theme.BgData, Is.EqualTo(before));
        Assert.That(Theme.Accent, Is.EqualTo(beforeAccent));
    }

    private static Color FromHex(int rgb) =>
        Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
}
