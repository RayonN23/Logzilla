using Logzilla.AI.Providers;
using NUnit.Framework;

namespace Logzilla.AI.Tests;

/// <summary>Adding up what a question and a conversation cost.</summary>
[TestFixture]
public sealed class UsageTests
{
    [Test]
    public void EachCallIsCountedAndItsTokensAddUp()
    {
        var t = new UsageTally();
        t.Add(new UsageReport(1200, 80, 1000));
        t.Add(new UsageReport(1500, 300));

        Assert.That(t.Calls, Is.EqualTo(2));
        Assert.That(t.InputTokens, Is.EqualTo(2700));
        Assert.That(t.OutputTokens, Is.EqualTo(380));
        Assert.That(t.CachedTokens, Is.EqualTo(1000));
        Assert.That(t.Line(), Is.EqualTo("2,700 in · 380 out tokens · 2 calls"));
    }

    [Test]
    public void AContextReportIsNotACall()
    {
        var t = new UsageTally();
        t.Add(new UsageReport(ContextTokens: 18_000, ContextLimit: 128_000));

        Assert.That(t.Calls, Is.Zero);
        Assert.That(t.Line(), Is.Empty, "Nothing to show under an answer until tokens are reported.");
        Assert.That(t.Details(), Does.Contain("18,000 of 128,000 (14% full)"));
    }

    [Test]
    public void ThePremiumMultiplierIsShownNotSummed()
    {
        var t = new UsageTally();
        t.Add(new UsageReport(Multiplier: 1));
        t.Add(new UsageReport(100, 10));
        t.Add(new UsageReport(100, 10));

        Assert.That(t.Multiplier, Is.EqualTo(1));
        Assert.That(t.Line(), Does.EndWith("· 1× premium"));
    }

    [Test]
    public void ConversationTotalsAreCompact()
    {
        var t = new UsageTally();
        t.Add(new UsageReport(12_000, 480));

        Assert.That(t.Brief(), Is.EqualTo("12k tokens"));
        Assert.That(UsageTally.Compact(999), Is.EqualTo("999"));
        Assert.That(UsageTally.Compact(1_250), Is.EqualTo("1.3k"));
        Assert.That(UsageTally.Compact(2_400_000), Is.EqualTo("2.4M"));
    }

    [Test]
    public void TalliesCombine()
    {
        var answer = new UsageTally();
        answer.Add(new UsageReport(10, 5));
        var chat = new UsageTally();
        chat.Add(new UsageReport(20, 5));
        chat.Add(answer);

        Assert.That(chat.Calls, Is.EqualTo(2));
        Assert.That(chat.TotalTokens, Is.EqualTo(40));
    }
}
