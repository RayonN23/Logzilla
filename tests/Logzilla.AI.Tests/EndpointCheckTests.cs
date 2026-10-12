using Logzilla.AI.Providers;
using NUnit.Framework;

namespace Logzilla.AI.Tests;

/// <summary>
/// An endpoint typed into AI settings receives the API key on every request, so it has
/// to be encrypted — except on this machine, where a local gateway listens.
/// </summary>
[TestFixture]
public sealed class EndpointCheckTests
{
    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("https://gateway.example.com/v1")]
    [TestCase("https://myorg.openai.azure.com/")]
    [TestCase("http://localhost:4000")]
    [TestCase("http://127.0.0.1:8080/v1")]
    [TestCase("http://[::1]:8080")]
    public void IsAccepted(string endpoint)
    {
        Assert.That(EndpointCheck.Problem(endpoint), Is.Null);
    }

    [TestCase("http://gateway.example.com/v1")]
    [TestCase("http://10.0.0.5/v1")]
    [TestCase("ftp://example.com")]
    [TestCase("file:///C:/keys.txt")]
    public void SendingTheKeyUnencryptedIsRefused(string endpoint)
    {
        Assert.That(EndpointCheck.Problem(endpoint), Does.Contain("https://"));
    }

    [TestCase("gateway.example.com")]
    [TestCase("not a url")]
    public void SomethingThatIsNotAnAddressIsRefused(string endpoint)
    {
        Assert.That(EndpointCheck.Problem(endpoint), Does.Contain("not a web address"));
    }
}
