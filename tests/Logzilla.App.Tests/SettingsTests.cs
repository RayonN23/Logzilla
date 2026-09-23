using Logzilla.App.Infrastructure;
using NUnit.Framework;

namespace Logzilla.App.Tests;

/// <summary>
/// Only the in-memory Get/Set round trip is covered here. Load/Save/LoadRecent/
/// PushRecent read and write the user's real %AppData%\Logzilla files (see
/// Paths.cs) with no injectable location, so exercising them from a test would
/// mean overwriting the developer's own saved settings and recent-files list —
/// out of scope for this pass.
/// </summary>
[TestFixture]
public sealed class SettingsTests
{
    [Test]
    public void GetReturnsTheDefaultWhenTheKeyIsMissing()
    {
        var s = new Settings();
        Assert.That(s.Get("missing", "fallback"), Is.EqualTo("fallback"));
    }

    [Test]
    public void SetThenGetReturnsTheStoredValue()
    {
        var s = new Settings();
        s.Set("k", "value");
        Assert.That(s.Get("k", "fallback"), Is.EqualTo("value"));
    }

    [Test]
    public void GetIntRoundTripsAStoredInteger()
    {
        var s = new Settings();
        s.Set("k", 7);
        Assert.That(s.GetInt("k", 0), Is.EqualTo(7));
    }

    [Test]
    public void GetIntFallsBackToTheDefaultOnUnparsableText()
    {
        var s = new Settings();
        s.Set("k", "not-a-number");
        Assert.That(s.GetInt("k", 42), Is.EqualTo(42));
    }

    [Test]
    public void GetIntFallsBackToTheDefaultWhenMissing()
    {
        var s = new Settings();
        Assert.That(s.GetInt("missing", 42), Is.EqualTo(42));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void GetBoolRoundTripsAStoredBoolean(bool value)
    {
        var s = new Settings();
        s.Set("k", value);
        Assert.That(s.GetBool("k", !value), Is.EqualTo(value));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void GetBoolFallsBackToTheDefaultWhenMissing(bool @default)
    {
        var s = new Settings();
        Assert.That(s.GetBool("missing", @default), Is.EqualTo(@default));
    }
}
