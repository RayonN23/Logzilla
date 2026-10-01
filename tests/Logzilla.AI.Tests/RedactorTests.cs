using Logzilla.AI.Privacy;
using NUnit.Framework;

namespace Logzilla.AI.Tests;

/// <summary>Personal and secret values never leave the machine, and come back on screen.</summary>
[TestFixture]
public sealed class RedactorTests
{
    [TestCase("user=alice@example.com logged in", "alice@example.com", "«EMAIL_1»")]
    [TestCase("from 10.1.2.3 port 443", "10.1.2.3", "«IP_1»")]
    [TestCase("device 3f2504e0-4f89-11d3-9a0c-0305e82c3301 online", "3f2504e0-4f89-11d3-9a0c-0305e82c3301", "«ID_1»")]
    [TestCase("key AKIAIOSFODNN7EXAMPLE used", "AKIAIOSFODNN7EXAMPLE", "«SECRET_1»")]
    [TestCase("token ghp_abcdefghijklmnopqrstuvwxyz0123456789AB", "ghp_abcdefghijklmnopqrstuvwxyz0123456789AB", "«SECRET_1»")]
    public void HidesTheValue(string text, string value, string placeholder)
    {
        var r = new Redactor();
        var sent = r.Redact(text);

        Assert.That(sent, Does.Not.Contain(value));
        Assert.That(sent, Does.Contain(placeholder));
    }

    [TestCase("password=hunter22 ok", "hunter22")]
    [TestCase("\"apiKey\": \"sk-live-1234567890\"", "sk-live-1234567890")]
    [TestCase("Server=db;User Id=sa;Password=P@ssw0rd!;", "P@ssw0rd!")]
    [TestCase("client_secret: abcDEF123456", "abcDEF123456")]
    public void HidesTheValueOfAKeyedSecretButKeepsTheKey(string text, string secret)
    {
        var sent = new Redactor().Redact(text);

        Assert.That(sent, Does.Not.Contain(secret));
        Assert.That(sent, Does.Contain("«SECRET_1»"));
    }

    [Test]
    public void HidesBearerTokensAndJwts()
    {
        var jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U";
        var sent = new Redactor().Redact("Authorization: Bearer " + jwt);

        Assert.That(sent, Does.Not.Contain("eyJ"));
        Assert.That(sent, Does.StartWith("Authorization: Bearer «TOKEN_"));
    }

    [Test]
    public void TheSameValueAlwaysGetsTheSamePlaceholder()
    {
        var r = new Redactor();
        var first = r.Redact("from 10.1.2.3");
        var second = r.Redact("again from 10.1.2.3, then 10.9.9.9");

        Assert.That(first, Is.EqualTo("from «IP_1»"));
        Assert.That(second, Is.EqualTo("again from «IP_1», then «IP_2»"), "The model can still tell one host from another.");
    }

    [Test]
    public void RestorePutsTheRealValuesBackForDisplay()
    {
        var r = new Redactor();
        r.Redact("alice@example.com at 10.1.2.3");

        Assert.That(r.Restore("«EMAIL_1» connected from «IP_1»."), Is.EqualTo("alice@example.com connected from 10.1.2.3."));
    }

    [Test]
    public void AnUnknownPlaceholderIsLeftAlone()
    {
        Assert.That(new Redactor().Restore("see «IP_7»"), Is.EqualTo("see «IP_7»"));
    }

    [Test]
    public void LoopbackAddressesAreNotPersonal()
    {
        Assert.That(new Redactor().Redact("listening on 127.0.0.1 and 0.0.0.0"), Is.EqualTo("listening on 127.0.0.1 and 0.0.0.0"));
    }

    [Test]
    public void TurnedOffItPassesEverythingThrough()
    {
        var r = new Redactor { Enabled = false };

        Assert.That(r.Redact("alice@example.com password=x123"), Is.EqualTo("alice@example.com password=x123"));
    }

    [Test]
    public void ClearingForgetsEveryValue()
    {
        var r = new Redactor();
        r.Redact("10.1.2.3");
        r.Clear();

        Assert.That(r.Hidden, Is.Zero);
        Assert.That(r.Restore("«IP_1»"), Is.EqualTo("«IP_1»"));
    }

    [Test]
    public void OrdinaryTextIsUntouched()
    {
        const string text = "GET /orders/42 status=200 latency=41ms took 3.5s";
        Assert.That(new Redactor().Redact(text), Is.EqualTo(text));
    }
}
