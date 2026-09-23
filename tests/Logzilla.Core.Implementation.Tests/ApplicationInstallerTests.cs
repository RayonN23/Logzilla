using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Versioning;
using Logzilla.Setup;
using NUnit.Framework;

namespace Logzilla.Core.Implementation.Tests;

/// <summary>
/// The uninstall's file removal. The uninstaller runs from inside the folder it is
/// deleting, so one file is always still locked; reporting that as a failure is what
/// made a finished uninstall end with
/// "ERROR: Unexpected failure: Access to the path 'Logzilla-Setup.exe' is denied."
///
/// PendingDelete's scheduling flag is set before these run, so nothing here starts a
/// cmd process that would delete a temporary folder out from under the test.
/// </summary>
[TestFixture]
[SupportedOSPlatform("windows")]
public sealed class ApplicationInstallerTests
{
    private bool _scheduledBefore;

    [OneTimeSetUp]
    public void SuppressPendingDeletes()
    {
        _scheduledBefore = (bool)ScheduledField().GetValue(null);
        ScheduledField().SetValue(null, true);
    }

    [OneTimeTearDown]
    public void RestorePendingDeletes() => ScheduledField().SetValue(null, _scheduledBefore);

    [Test]
    public void RemovesEveryFileAndTheFolderWhenNothingIsLocked()
    {
        using var folder = new TemporaryFolder();
        File.WriteAllText(Path.Combine(folder.DirectoryPath, "Logzilla.exe"), "x");
        Directory.CreateDirectory(Path.Combine(folder.DirectoryPath, "runtimes"));
        File.WriteAllText(Path.Combine(folder.DirectoryPath, "runtimes", "native.dll"), "x");

        Assert.That(DeleteDirectory(folder.DirectoryPath, @"C:\somewhere\else\Logzilla-Setup.exe"), Is.True);
        Assert.That(Directory.Exists(folder.DirectoryPath), Is.False);
    }

    [Test]
    public void LeavesTheRunningUninstallerBehindAndSaysSo()
    {
        using var folder = new TemporaryFolder();
        var self = Path.Combine(folder.DirectoryPath, "Logzilla-Setup.exe");
        File.WriteAllText(self, "x");
        File.WriteAllText(Path.Combine(folder.DirectoryPath, "Logzilla.exe"), "x");
        Directory.CreateDirectory(Path.Combine(folder.DirectoryPath, "runtimes"));
        File.WriteAllText(Path.Combine(folder.DirectoryPath, "runtimes", "native.dll"), "x");

        Assert.That(DeleteDirectory(folder.DirectoryPath, self), Is.False,
            "The caller has to know something was left for the scheduled cleanup.");
        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(self), Is.True, "A running image cannot be deleted.");
            Assert.That(File.Exists(Path.Combine(folder.DirectoryPath, "Logzilla.exe")), Is.False);
            Assert.That(File.Exists(Path.Combine(folder.DirectoryPath, "runtimes", "native.dll")), Is.False);
        });
    }

    [Test]
    public void RecognisesItsOwnFileWhateverSpellingItWasLaunchedWith()
    {
        // Add/Remove Programs invokes whatever path was recorded at install time; the
        // directory walk produces its own spelling of the same file.
        using var folder = new TemporaryFolder();
        var self = Path.Combine(folder.DirectoryPath, "Logzilla-Setup.exe");
        File.WriteAllText(self, "x");
        var roundabout = Path.Combine(folder.DirectoryPath, "runtimes", "..", "LOGZILLA-SETUP.EXE");
        Directory.CreateDirectory(Path.Combine(folder.DirectoryPath, "runtimes"));

        Assert.That(DeleteDirectory(folder.DirectoryPath, roundabout), Is.False);
        Assert.That(File.Exists(self), Is.True);
    }

    [Test]
    public void ALockedFileIsLeftForTheScheduledCleanupInsteadOfThrowing()
    {
        using var folder = new TemporaryFolder();
        var locked = Path.Combine(folder.DirectoryPath, "Logzilla.exe");
        File.WriteAllText(locked, "x");
        File.WriteAllText(Path.Combine(folder.DirectoryPath, "readme.txt"), "x");

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.That(DeleteDirectory(folder.DirectoryPath, null), Is.False);
        }

        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(locked), Is.True);
            Assert.That(File.Exists(Path.Combine(folder.DirectoryPath, "readme.txt")), Is.False,
                "One locked file must not stop the rest of the install from being removed.");
        });
    }

    [Test]
    public void ARunningExecutableInTheFolderDoesNotFailTheUninstall()
    {
        // The reported bug: deleting a running image raises UnauthorizedAccessException,
        // not IOException, and it escaped as "Unexpected failure: Access to the path
        // 'Logzilla-Setup.exe' is denied" after the uninstall had done its work. A copy of
        // cmd.exe stands in for the uninstaller — any running image is locked the same way.
        using var folder = new TemporaryFolder();
        var running = Path.Combine(folder.DirectoryPath, "Logzilla-Setup.exe");
        File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), running);
        File.WriteAllText(Path.Combine(folder.DirectoryPath, "Logzilla.exe"), "x");

        var process = Process.Start(new ProcessStartInfo(running, "/c ping 127.0.0.1 -n 30 > nul")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        });
        try
        {
            // Not passed as "self": this is the locked-file path, which is what threw.
            Assert.That(DeleteDirectory(folder.DirectoryPath, null), Is.False);
            Assert.That(File.Exists(Path.Combine(folder.DirectoryPath, "Logzilla.exe")), Is.False);
        }
        finally
        {
            process?.Kill(entireProcessTree: true);
            process?.WaitForExit(5000);
            process?.Dispose();
        }
    }

    [Test]
    public void AnAlreadyMissingFolderIsNotAFailure()
    {
        // Uninstalling twice, or after the files were deleted by hand.
        var missing = Path.Combine(Path.GetTempPath(), "Logzilla.Tests", Guid.NewGuid().ToString("N"));
        Assert.That(DeleteDirectory(missing, null), Is.True);
    }

    private static bool DeleteDirectory(string directory, string self)
    {
        var method = typeof(ApplicationInstaller).GetMethod(
            "DeleteDirectory", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null, "Could not find ApplicationInstaller.DeleteDirectory.");
        return (bool)method!.Invoke(null, [directory, self]);
    }

    private static FieldInfo ScheduledField()
    {
        var field = typeof(PendingDelete).GetField("Scheduled", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, "Could not find PendingDelete.Scheduled.");
        return field!;
    }

    private sealed class TemporaryFolder : IDisposable
    {
        public TemporaryFolder()
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), "Logzilla.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
        }

        public string DirectoryPath { get; }

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }
    }
}
