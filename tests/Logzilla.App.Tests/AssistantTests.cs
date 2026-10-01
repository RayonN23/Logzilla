using System.Collections.Generic;
using System.Linq;
using Logzilla.AI.Providers;
using Logzilla.AI.Tools;
using Logzilla.App.Controls;
using Logzilla.App.Infrastructure;
using NUnit.Framework;

namespace Logzilla.App.Tests;

/// <summary>The Ask tab's pure logic: reading answers, organisation policy, stored keys, layout.</summary>
[TestFixture]
public sealed class AskMarkupTests
{
    private static readonly int[] TwelveThenThirtyFour = [12, 34];
    private static readonly int[] TwoCitations = [1204, 4211];
    private static readonly RunKind[] MixedRuns = [RunKind.Plain, RunKind.Bold, RunKind.Plain, RunKind.Code, RunKind.Plain];

    [Test]
    public void CitationsBecomeTheirOwnRuns()
    {
        var p = AskMarkup.Parse("Two resets [#1204] and [#4211].").Single();

        Assert.That(p.Runs.Where(r => r.Kind == RunKind.Cite).Select(r => r.Line), Is.EqualTo(TwoCitations));
        Assert.That(p.Runs.First(r => r.Kind == RunKind.Cite).Text, Is.EqualTo("#1,204"));
    }

    [Test]
    public void AGroupOfCitationsSplitsIntoOnePerLine()
    {
        Assert.That(AskMarkup.Citations("See [#12, #34]."), Is.EqualTo(TwelveThenThirtyFour));
    }

    [Test]
    public void BoldAndCodeAreRecognised()
    {
        var runs = AskMarkup.Parse("The **pool** ran out: `maxSize=10`.").Single().Runs;

        Assert.That(runs.Select(r => r.Kind), Is.EqualTo(MixedRuns));
        Assert.That(runs[1].Text, Is.EqualTo("pool"));
        Assert.That(runs[3].Text, Is.EqualTo("maxSize=10"));
    }

    [Test]
    public void BulletsNumbersAndHeadingsAreParagraphKinds()
    {
        var ps = AskMarkup.Parse("# Cause\n- first\n2. second\nplain");

        Assert.That(ps[0].Heading, Is.True);
        Assert.That(ps[1].Bullet, Is.True);
        Assert.That(ps[1].Marker, Is.EqualTo("•"));
        Assert.That(ps[2].Marker, Is.EqualTo("2."));
        Assert.That(ps[3].Bullet || ps[3].Heading, Is.False);
    }

    [Test]
    public void FencedCodeKeepsItsLinesAsTheyAre()
    {
        var ps = AskMarkup.Parse("Look:\n```\n  at Repo.load()\n**not bold**\n```\nDone.");

        Assert.That(ps.Count(p => p.Code), Is.EqualTo(2));
        Assert.That(ps[2].Runs[0].Text, Is.EqualTo("**not bold**"), "Inside a code block nothing is formatting.");
    }

    [Test]
    public void UnfinishedMarkupIsJustText()
    {
        var runs = AskMarkup.Parse("half **open and `tick").Single().Runs;

        Assert.That(string.Concat(runs.Select(r => r.Text)), Is.EqualTo("half **open and `tick"));
    }

    [Test]
    public void EmptyTextHasNoParagraphs()
    {
        Assert.That(AskMarkup.Parse(""), Is.Empty);
        Assert.That(AskMarkup.Parse(null), Is.Empty);
    }
}

[TestFixture]
public sealed class AIPolicyTests
{
    private static AIPolicy From(Dictionary<string, object> values) =>
        AIPolicy.From(name => values.TryGetValue(name, out var v) ? v : null);

    [Test]
    public void NothingSetMeansNoRestrictions()
    {
        var p = From(new Dictionary<string, object>());

        Assert.That(p.Managed, Is.False);
        Assert.That(p.Allows("copilot"), Is.True);
        Assert.That(p.Allows("anthropic"), Is.True);
    }

    [Test]
    public void DisabledAllowsNothing()
    {
        var p = From(new() { ["AIDisabled"] = 1 });

        Assert.That(p.Disabled, Is.True);
        Assert.That(p.Allows("copilot"), Is.False);
    }

    [Test]
    public void AnAllowListKeepsOnlyThoseProviders()
    {
        var p = From(new() { ["AIAllowedProviders"] = "Copilot; anthropic" });

        Assert.That(p.Allows("copilot"), Is.True, "Names ignore case and separators.");
        Assert.That(p.Allows("anthropic"), Is.True);
        Assert.That(p.Allows("openai"), Is.False);
    }

    [Test]
    public void RequirementsAndEndpointsAreRead()
    {
        var p = From(new()
        {
            ["AIRequireRedaction"] = 1,
            ["AIRequireRecord"] = "1",
            ["AIOpenAIEndpoint"] = "https://contoso.openai.azure.com/",
        });

        Assert.That(p.RequireRedaction, Is.True);
        Assert.That(p.RequireRecord, Is.True);
        Assert.That(p.OpenAIEndpoint, Is.EqualTo("https://contoso.openai.azure.com/"));
        Assert.That(p.Managed, Is.True);
    }

    [Test]
    public void AnOrganisationCanUseItsOwnGitHubApp()
    {
        var p = From(new() { ["AIGitHubClientId"] = " Iv1.contoso " });

        Assert.That(p.GitHubClientId, Is.EqualTo("Iv1.contoso"));
        Assert.That(p.Managed, Is.True);
    }

    [Test]
    public void AZeroFlagIsOff()
    {
        Assert.That(From(new() { ["AIDisabled"] = 0 }).Disabled, Is.False);
    }
}

[TestFixture]
public sealed class SecretStoreTests
{
    [Test]
    public void AKeyRoundTripsButIsNeverStoredAsIs()
    {
        const string key = "sk-ant-test-0123456789";
        var stored = SecretStore.Protect(key);

        Assert.That(stored, Does.StartWith("dpapi:"));
        Assert.That(stored, Does.Not.Contain(key));
        Assert.That(SecretStore.Unprotect(stored), Is.EqualTo(key));
    }

    [Test]
    public void NothingStoredReadsAsNothing()
    {
        Assert.That(SecretStore.Protect(""), Is.Empty);
        Assert.That(SecretStore.Unprotect(""), Is.Null);
        Assert.That(SecretStore.Unprotect("plain-text-key"), Is.Null, "A value that was never encrypted is not trusted.");
        Assert.That(SecretStore.Unprotect("dpapi:not-base64!"), Is.Null);
    }
}

[TestFixture]
public sealed class AskTranscriptTests
{
    private static AskTranscript Transcript(int width)
    {
        var t = new AskTranscript();
        t.Width = width;
        t.Height = 400;
        return t;
    }

    [Test]
    public void BlocksAreLaidOutOneBelowAnother()
    {
        using var t = Transcript(360);
        var q = t.Add(AskBlockKind.User, "Why did it fail?");
        var a = t.Add(AskBlockKind.Assistant, "Because the pool ran out [#42].");

        Assert.That(q.Height, Is.GreaterThan(0));
        Assert.That(a.Top, Is.GreaterThan(q.Top + q.Height - 1));
        Assert.That(a.Pieces.Any(p => p.Cite == 42), Is.True, "The citation is a chip that can be clicked.");
    }

    [Test]
    public void ALongAnswerWrapsWithinTheWidth()
    {
        using var t = Transcript(240);
        var a = t.Add(AskBlockKind.Assistant, string.Join(" ", Enumerable.Repeat("word", 80)));

        Assert.That(a.Pieces.Max(p => p.Rect.Right), Is.LessThanOrEqualTo(240));
        Assert.That(a.Pieces.Select(p => p.Rect.Top).Distinct().Count(), Is.GreaterThan(3));
    }

    [Test]
    public void AToolCallGoesAboveTheAnswerStillBeingWritten()
    {
        using var t = Transcript(360);
        var answer = t.Add(AskBlockKind.Assistant, "");
        answer.Streaming = true;
        t.AddBefore(answer, AskBlockKind.Tool, "Searched \"reset\" — 2 entries", new SearchRequest("reset", false, false, "", "", "", "", ""));

        Assert.That(t.Blocks[0].Kind, Is.EqualTo(AskBlockKind.Tool));
        Assert.That(t.Blocks[0].Pieces.Any(p => p.Apply), Is.True, "A search can be applied as a filter.");
        Assert.That(t.Blocks[1], Is.SameAs(answer));
    }

    [Test]
    public void AnAnswerShowsWhatItCostOnceTokensAreReported()
    {
        using var t = Transcript(360);
        var a = t.Add(AskBlockKind.Assistant, "Two resets.");
        a.Usage = new UsageTally();
        t.Changed(a);
        Assert.That(a.Pieces.Any(p => p.Text.Contains("tokens", System.StringComparison.Ordinal)), Is.False, "Nothing reported yet, nothing shown.");

        a.Usage.Add(new UsageReport(1200, 80));
        t.Changed(a);

        Assert.That(a.Pieces.Last().Text, Is.EqualTo("1,200 in · 80 out tokens"));
    }

    [Test]
    public void StreamedTextAppendsToTheSameBlock()
    {
        using var t = Transcript(360);
        var a = t.Add(AskBlockKind.Assistant, "Two ");
        t.Append(a, "resets.");

        Assert.That(a.Text, Is.EqualTo("Two resets."));
        Assert.That(t.Blocks, Has.Count.EqualTo(1));
    }

    [Test]
    public void TheRealValuesAreShownNotThePlaceholders()
    {
        using var t = Transcript(360);
        t.Restore = s => s.Replace("«IP_1»", "10.1.2.3", System.StringComparison.Ordinal);
        var a = t.Add(AskBlockKind.Assistant, "From «IP_1».");

        Assert.That(string.Concat(a.Pieces.Select(p => p.Text)), Does.Contain("10.1.2.3"));
        Assert.That(a.Text, Does.Contain("«IP_1»"), "What the model said is kept as it said it.");
    }
}
