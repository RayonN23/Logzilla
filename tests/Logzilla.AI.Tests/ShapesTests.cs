using Logzilla.AI.Tools;
using NUnit.Framework;

namespace Logzilla.AI.Tests;

[TestFixture]
public sealed class ShapesTests
{
    [Test]
    public void NumbersIdsAndAddressesBecomePlaceholders()
    {
        Assert.That(Shapes.Of("latency=812ms from 10.1.2.3:8080 id 3f2504e0-4f89-11d3-9a0c-0305e82c3301"),
            Is.EqualTo("latency=<n>ms from <ip> id <id>"));
    }

    [Test]
    public void TwoMessagesThatDifferOnlyInValuesShareAShape()
    {
        Assert.That(Shapes.Of("Retry 1 for host=db-02 after 41ms"), Is.EqualTo(Shapes.Of("Retry 7 for host=db-05 after 812ms")));
    }

    [Test]
    public void QuotedValuesAndRedactionPlaceholdersAreVariableToo()
    {
        Assert.That(Shapes.Of("user \"bob\" from «IP_3»"), Is.EqualTo("user <str> from <*>"));
    }

    [Test]
    public void AVeryLongMessageIsCut()
    {
        Assert.That(Shapes.Of(new string('a', 5000)).Length, Is.LessThanOrEqualTo(Shapes.MaxLength + 1));
    }

    [Test]
    public void NothingInNothingOut()
    {
        Assert.That(Shapes.Of(null), Is.Empty);
    }
}
