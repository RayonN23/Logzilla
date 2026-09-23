using System.IO;
using System.Reflection;
using NUnit.Framework;

namespace Logzilla.App.Tests;

/// <summary>
/// ExistingPaths decides which command-line arguments are files to open versus
/// switches like /register. It touches only Path.GetTempFileName()'s own temp
/// files, never the real filesystem the app would otherwise open.
/// </summary>
[TestFixture]
public sealed class ProgramTests
{
    private static System.Collections.Generic.List<string> ExistingPaths(string[] args)
    {
        var programType = typeof(Logzilla.App.Infrastructure.Dpi).Assembly.GetType("Logzilla.App.Program");
        var method = programType.GetMethod("ExistingPaths", BindingFlags.NonPublic | BindingFlags.Static);
        return (System.Collections.Generic.List<string>)method.Invoke(null, [args]);
    }

    [Test]
    public void KeepsOnlyArgumentsThatAreRealFiles()
    {
        var real1 = Path.GetTempFileName();
        var real2 = Path.GetTempFileName();
        try
        {
            var result = ExistingPaths([real1, "/register", real2, @"Z:\definitely\missing\file.log"]);
            Assert.That(result, Is.EqualTo(new[] { real1, real2 }));
        }
        finally
        {
            File.Delete(real1);
            File.Delete(real2);
        }
    }

    [Test]
    public void ReturnsAnEmptyListWhenNoArgumentIsARealFile()
    {
        var result = ExistingPaths(["/register", "/silent"]);
        Assert.That(result, Is.Empty);
    }
}
