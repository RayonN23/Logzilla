using Logzilla.Setup;
using NUnit.Framework;

namespace Logzilla.Core.Implementation.Tests;

/// <summary>
/// The installer's command line. Silent and uninstall are what CI and
/// Add/Remove Programs invoke, so they need to keep working.
/// </summary>
[TestFixture]
public class SetupOptionsTests
{
    [Test]
    public void DefaultsToAnInteractiveInstall()
    {
        var options = SetupOptions.Parse([]);

        Assert.That(options.Silent, Is.False);
        Assert.That(options.Uninstall, Is.False);
        Assert.That(options.TargetDirectory, Is.Null);
    }

    [Test]
    public void RecognisesSilent() => Assert.That(SetupOptions.Parse(["/silent"]).Silent, Is.True);

    [Test]
    public void SwitchesAreCaseInsensitive() => Assert.That(SetupOptions.Parse(["/UNINSTALL"]).Uninstall, Is.True);

    [Test]
    public void CapturesAnExplicitDirectory() =>
        Assert.That(SetupOptions.Parse([@"/dir=C:\Tools\CN"]).TargetDirectory, Is.EqualTo(@"C:\Tools\CN"));

    /// <summary>A path with spaces arrives quoted when it comes from a shortcut.</summary>
    [Test]
    public void StripsQuotesFromTheDirectory() =>
        Assert.That(SetupOptions.Parse(["/dir=\"C:\\Program Files\\CN\""]).TargetDirectory, Is.EqualTo(@"C:\Program Files\CN"));

    [Test]
    public void EmptyDirectoryFallsBackToTheDefault() =>
        Assert.That(SetupOptions.Parse(["/dir="]).TargetDirectory, Is.Null);

    [Test]
    public void SwitchesCombine()
    {
        var options = SetupOptions.Parse(["/uninstall", "/silent"]);

        Assert.That(options.Uninstall, Is.True);
        Assert.That(options.Silent, Is.True);
    }

    [Test]
    public void UnknownArgumentsAreIgnored()
    {
        var options = SetupOptions.Parse(["--nonsense", "file.log"]);

        Assert.That(options.Silent, Is.False);
        Assert.That(options.Uninstall, Is.False);
    }
}
