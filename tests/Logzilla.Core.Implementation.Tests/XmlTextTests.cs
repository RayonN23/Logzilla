using Logzilla.Core.Common;
using NUnit.Framework;

namespace Logzilla.Core.Implementation.Tests;

/// <summary>
/// Indenting an XML payload that a service logged inside a sentence. The input is
/// never a clean document — it is quoted inside prose, it is a fragment, it has
/// been cut off mid-tag — so the rule that matters most is that anything not
/// recognised comes back untouched rather than mangled or thrown over.
/// </summary>
[TestFixture]
public class XmlTextTests
{
    private const string Plist =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
        "<plist version=\"1.0\"><dict>" +
        "<key>MessageType</key><string>UserEnrollmentReportStatus</string>" +
        "<key>PasscodeCompliant</key><true />" +
        "</dict></plist>";

    private static string[] Lines(string s) => s.Split('\n');

    // ── detection ──────────────────────────────────────────────────────

    [Test]
    public void RecognisesAnXmlPayload()
    {
        Assert.That(XmlText.LooksLikeXml(Plist), Is.True);
    }

    [Test]
    public void DoesNotTakeProseForMarkup()
    {
        Assert.That(XmlText.LooksLikeXml("latency 30ms < budget 50ms, so ok"), Is.False);
        Assert.That(XmlText.LooksLikeXml("GET /api/orders/4213 status=200"), Is.False);
        Assert.That(XmlText.LooksLikeXml(""), Is.False);
        Assert.That(XmlText.LooksLikeXml(null), Is.False);
    }

    [Test]
    public void OneOrTwoStrayTagsAreNotEnough()
    {
        // A generic type name in a .NET message must not turn the row into a document.
        Assert.That(XmlText.LooksLikeXml("resolved IHandler<Order> in 3ms"), Is.False);
    }

    // ── layout ─────────────────────────────────────────────────────────

    [Test]
    public void PutsEachElementOnItsOwnLine()
    {
        var lines = Lines(XmlText.Pretty(Plist));

        Assert.That(lines[0], Is.EqualTo("<?xml version=\"1.0\" encoding=\"utf-8\"?>"));
        Assert.That(lines[1], Is.EqualTo("<plist version=\"1.0\">"));
        Assert.That(lines[2], Is.EqualTo("  <dict>"));
    }

    [Test]
    public void KeepsATextOnlyElementOnOneLine()
    {
        Assert.That(XmlText.Pretty(Plist),
            Does.Contain("    <key>MessageType</key>\n    <string>UserEnrollmentReportStatus</string>"),
            "Splitting <key>X</key> over three lines is harder to read, not easier.");
    }

    [Test]
    public void ClosingTagsComeBackOutAgain()
    {
        var lines = Lines(XmlText.Pretty(Plist));

        Assert.That(lines[^2], Is.EqualTo("  </dict>"));
        Assert.That(lines[^1], Is.EqualTo("</plist>"));
    }

    [Test]
    public void SelfClosingElementsDoNotIndentWhatFollows()
    {
        var lines = Lines(XmlText.Pretty("<a><b /><c /></a>"));

        Assert.That(lines[1], Is.EqualTo("  <b />"));
        Assert.That(lines[2], Is.EqualTo("  <c />"));
    }

    [Test]
    public void DeclarationsAndDoctypesDoNotIndentWhatFollows()
    {
        var xml = "<?xml version=\"1.0\"?><!DOCTYPE plist PUBLIC \"-//Apple//DTD\"><a><b>1</b></a>";
        var lines = Lines(XmlText.Pretty(xml));

        Assert.That(lines[1], Does.StartWith("<!DOCTYPE"));
        Assert.That(lines[2], Is.EqualTo("<a>"));
    }

    [Test]
    public void AGreaterThanInsideAnAttributeDoesNotEndTheTag()
    {
        var lines = Lines(XmlText.Pretty("<rule expr=\"a > b\"><hit>1</hit></rule>"));

        Assert.That(lines[0], Is.EqualTo("<rule expr=\"a > b\">"));
    }

    [Test]
    public void CommentsAndCDataSurviveIntact()
    {
        var lines = Lines(XmlText.Pretty("<a><!-- keep > this --><b><![CDATA[x > y]]></b></a>"));

        Assert.That(lines[1], Is.EqualTo("  <!-- keep > this -->"));
        Assert.That(lines[2], Does.Contain("<![CDATA[x > y]]>"));
    }

    // ── embedded in a message ──────────────────────────────────────────

    [Test]
    public void LiftsThePayloadOutOfTheSentenceAroundIt()
    {
        var message = "Device Request [>>>]: type 'Report', content '" + Plist + "'";
        var lines = Lines(XmlText.PrettyWithin(message));

        Assert.That(lines[0], Is.EqualTo("Device Request [>>>]: type 'Report', content '"));
        Assert.That(lines[1], Is.EqualTo("<?xml version=\"1.0\" encoding=\"utf-8\"?>"));
        Assert.That(lines[^1], Is.EqualTo("'"), "The trailing quote keeps its own line, not lost.");
    }

    [Test]
    public void LeavesAMessageWithoutMarkupExactlyAsItWas()
    {
        const string message = "GET /api/orders/4213 status=200 latency=63ms client=10.3.87.136";

        Assert.That(XmlText.PrettyWithin(message), Is.EqualTo(message));
    }

    [Test]
    public void APayloadThatIsTheWholeMessageGainsNoBlankLines()
    {
        var formatted = XmlText.PrettyWithin(Plist);

        Assert.That(formatted, Does.StartWith("<?xml"));
        Assert.That(formatted, Does.EndWith("</plist>"));
    }

    // ── tolerance ──────────────────────────────────────────────────────

    [Test]
    public void SurvivesAPayloadThatWasCutOffMidTag()
    {
        Assert.DoesNotThrow(() => XmlText.Pretty("<a><b>1</b><c attr=\"unclo"));
        Assert.DoesNotThrow(() => XmlText.PrettyWithin("content '<a><b>1</b><c"));
    }

    [Test]
    public void SurvivesMoreClosingTagsThanOpeningOnes()
    {
        Assert.DoesNotThrow(() => XmlText.Pretty("</a></b></c>"));
    }

    [Test]
    public void KeepsEveryPieceOfContentItWasGiven()
    {
        var formatted = XmlText.Pretty(Plist);

        Assert.That(formatted, Does.Contain("UserEnrollmentReportStatus"));
        Assert.That(formatted, Does.Contain("<true />"));
        Assert.That(formatted.Replace("\n", "").Replace(" ", ""),
            Is.EqualTo(Plist.Replace(" ", "")),
            "Indenting must not drop or reorder anything.");
    }

    [Test]
    public void EmptyAndNullComeBackUnchanged()
    {
        Assert.That(XmlText.Pretty(""), Is.EqualTo(""));
        Assert.That(XmlText.Pretty(null), Is.Null);
        Assert.That(XmlText.PrettyWithin(null), Is.Null);
    }
}
