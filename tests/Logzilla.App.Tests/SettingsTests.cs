using System;
using System.IO;
using Logzilla.App.Infrastructure;
using NUnit.Framework;

namespace Logzilla.App.Tests;

/// <summary>
/// The in-memory Get/Set round trip, and what Save writes. The file tests point the
/// settings folder at a throwaway one (Paths.DataDirVariable), so the developer's own
/// settings are never read or written.
/// </summary>
[TestFixture]
[NonParallelizable]
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

    [Test]
    public void SavedValuesComeBackAfterALoad()
    {
        WithDataDir(() =>
        {
            var s = new Settings();
            s.Set("ai.copilot.cli", @"C:\tools\new\copilot.exe");
            s.Set("levels", 0x3E);
            s.Save();

            var back = new Settings();
            back.Load();

            Assert.That(back.Get("ai.copilot.cli", ""), Is.EqualTo(@"C:\tools\new\copilot.exe"));
            Assert.That(back.GetInt("levels", 0), Is.EqualTo(0x3E));
        });
    }

    /// <summary>
    /// A highlight picked from a log's field value could carry a line break, and the
    /// file is one key per line: the next start read what followed the break as a key
    /// of its own — here, the endpoint the OpenAI key is sent to.
    /// </summary>
    [TestCase("\n")]
    [TestCase("\r\n")]
    [TestCase("\r")]
    public void ALineBreakInAValueCannotAddAKey(string lineBreak)
    {
        WithDataDir(() =>
        {
            var s = new Settings();
            s.Set("rules", "1\u001e0\u001e-1\u001ework" + lineBreak + "ai.endpoint.openai=https://attacker.example/v1");
            s.Save();

            var back = new Settings();
            back.Load();

            Assert.That(back.Get("ai.endpoint.openai", "unset"), Is.EqualTo("unset"));
            Assert.That(back.Get("rules", ""), Does.EndWith("work ai.endpoint.openai=https://attacker.example/v1"));
            Assert.That(File.ReadAllLines(Paths.Settings), Has.Length.EqualTo(1));
        });
    }

    private static void WithDataDir(Action test)
    {
        var dir = Path.Combine(Path.GetTempPath(), "lz-settings-" + Guid.NewGuid().ToString("N"));
        var before = Environment.GetEnvironmentVariable(Paths.DataDirVariable);
        Environment.SetEnvironmentVariable(Paths.DataDirVariable, dir);
        try
        {
            test();
        }
        finally
        {
            Environment.SetEnvironmentVariable(Paths.DataDirVariable, before);
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException ex)
            {
                TestContext.Out.WriteLine("Leaving " + dir + ": " + ex.Message);
            }
        }
    }
}
