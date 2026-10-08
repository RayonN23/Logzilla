using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Logzilla.Setup;
using NUnit.Framework;

namespace Logzilla.Core.Implementation.Tests;

[TestFixture]
public class ShortcutWriterTests
{
    [TestCase(Architecture.X64, Architecture.X64, false)]
    [TestCase(Architecture.X64, Architecture.Arm64, true)]
    [TestCase(Architecture.X86, Architecture.X64, true)]
    public void DetectsWhenShortcutCreationNeedsANativeHost(
        Architecture processArchitecture,
        Architecture osArchitecture,
        bool expected)
    {
        Assert.That(
            Invoke<bool>(nameof(ShortcutWriter.RequiresExternalShellHost), processArchitecture, osArchitecture),
            Is.EqualTo(expected));
    }

    [Test]
    public void EscapesSingleQuotesForPowerShellSingleQuotedStrings()
    {
        var escaped = Invoke<string>(nameof(ShortcutWriter.EscapePowerShellSingleQuotedString), @"C:\Users\O'Brien\Logzilla");

        Assert.That(escaped, Is.EqualTo(@"C:\Users\O''Brien\Logzilla"));
    }

    [Test]
    [SupportedOSPlatform("windows")]
    public void CreatesShortcutThroughShellLink()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Ignore("Shortcut creation is Windows-only.");
        }

        using var folder = new TemporaryFolder();
        var shortcutPath = Path.Combine(folder.DirectoryPath, "Logzilla.lnk");
        var targetPath = Environment.ProcessPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "notepad.exe");

        ShortcutWriter.Create(shortcutPath, targetPath, folder.DirectoryPath, "Test shortcut");

        Assert.That(File.Exists(shortcutPath), Is.True);
        Assert.That(new FileInfo(shortcutPath).Length, Is.GreaterThan(0));

        // Read back through the shell: a slot out of order in the COM declaration
        // would put each value into the wrong field rather than fail outright.
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        dynamic link = shell.CreateShortcut(shortcutPath);
        Assert.That((string)link.TargetPath, Is.EqualTo(targetPath).IgnoreCase);
        Assert.That((string)link.WorkingDirectory, Is.EqualTo(folder.DirectoryPath).IgnoreCase);
        Assert.That((string)link.Description, Is.EqualTo("Test shortcut"));
    }

    [Test]
    [SupportedOSPlatform("windows")]
    public void CreatesShortcutThroughPowerShellFallback()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Ignore("Shortcut creation is Windows-only.");
        }

        using var folder = new TemporaryFolder();
        var shortcutPath = Path.Combine(folder.DirectoryPath, "Logzilla PowerShell.lnk");
        var targetPath = Environment.ProcessPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "notepad.exe");

        Invoke<object>(nameof(ShortcutWriter.CreateWithPowerShell), shortcutPath, targetPath, folder.DirectoryPath, "Test shortcut");

        Assert.That(File.Exists(shortcutPath), Is.True);
        Assert.That(new FileInfo(shortcutPath).Length, Is.GreaterThan(0));
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

    private static T Invoke<T>(string methodName, params object[] args)
    {
        var method = typeof(ShortcutWriter).GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null, $"Could not find ShortcutWriter.{methodName}.");

        var result = method!.Invoke(null, args);
        return result is null ? default : (T)result;
    }
}


