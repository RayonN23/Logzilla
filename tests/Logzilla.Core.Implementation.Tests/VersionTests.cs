using System.Reflection;
using Logzilla.Core.Common;
using Logzilla.Setup;
using NUnit.Framework;

namespace Logzilla.Core.Implementation.Tests;

/// <summary>
/// The version is written in three places — `Version` in Directory.Build.props,
/// which becomes every assembly's version resource; `AppInfo.Version`, which the
/// About box and the help text read; and `AppMetadata.Version`, which the
/// installer writes into the Add/Remove Programs entry. Nothing derives one from
/// another, so a bump that misses one leaves Windows and the app disagreeing about
/// what is installed. These tests fail the build when that happens.
/// </summary>
[TestFixture]
public class VersionTests
{
    /// <summary>What Directory.Build.props stamped on the assembly.</summary>
    private static string AssemblyVersion()
    {
        var informational = typeof(AppInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(informational))
        {
            return typeof(AppInfo).Assembly.GetName().Version?.ToString();
        }
        // The SDK appends "+<commit sha>" when the build knows one.
        var plus = informational.IndexOf('+');
        return plus < 0 ? informational : informational[..plus];
    }

    [Test]
    public void TheAppAndTheInstallerAgreeOnTheVersion()
    {
        Assert.That(AppMetadata.Version, Is.EqualTo(AppInfo.Version),
            "Add/Remove Programs would otherwise report a different version from the app itself.");
    }

    [Test]
    public void TheVersionResourceMatchesTheConstant()
    {
        Assert.That(AssemblyVersion(), Is.EqualTo(AppInfo.Version),
            "Directory.Build.props and AppInfo.Version have to be bumped together.");
    }

    [Test]
    public void TheVersionIsThreeNumberedParts()
    {
        var parts = AppInfo.Version.Split('.');

        Assert.That(parts, Has.Length.EqualTo(3), "Semantic versioning: major.minor.patch.");
        foreach (var part in parts)
        {
            Assert.That(int.TryParse(part, out var n) && n >= 0, Is.True,
                "Every part of the version must be a number: " + AppInfo.Version);
        }
    }
}
