using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using FlaUI.Core.AutomationElements;
using System.Drawing;
using System.Drawing.Imaging;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using Logzilla.App.Infrastructure;
using NUnit.Framework;

namespace Logzilla.App.E2E;

/// <summary>
/// One running Logzilla, started for a test and closed after it. It gets a settings
/// folder of its own (<see cref="Paths.DataDirVariable"/>), so every run starts from
/// nothing and the real user's settings and recent files are never read or written.
/// Controls are found by automation id — the control's Name in the app.
/// </summary>
internal sealed class LogzillaApp : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    private readonly FlaUI.Core.Application _app;
    private readonly UIA3Automation _automation = new();
    private readonly string _dataDir;

    private LogzillaApp(FlaUI.Core.Application app, string dataDir)
    {
        _app = app;
        _dataDir = dataDir;
        Window = _app.GetMainWindow(_automation, Patience)
                 ?? throw new InvalidOperationException("Logzilla started but showed no window.");
    }

    public Window Window { get; }

    /// <summary>Starts Logzilla on the given sample logs (file names in samples\).</summary>
    public static LogzillaApp Start(params string[] samples) => Start(null, samples);

    /// <summary>
    /// Starts Logzilla with some settings already in place — written to its private
    /// settings file before it starts, as if a previous session had saved them.
    /// </summary>
    public static LogzillaApp Start(IDictionary<string, string> settings, params string[] samples)
    {
        if (Environment.GetEnvironmentVariable("LOGZILLA_E2E") != "1")
        {
            Assert.Ignore("End-to-end test: set LOGZILLA_E2E=1 to run it. It opens windows and uses the keyboard.");
        }
        var dataDir = Path.Combine(Path.GetTempPath(), "logzilla-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);
        if (settings != null)
        {
            File.WriteAllLines(Path.Combine(dataDir, "settings.ini"), settings.Select(kv => kv.Key + "=" + kv.Value));
        }
        var exe = Path.Combine(AppContext.BaseDirectory, "Logzilla.exe");
        Assert.That(File.Exists(exe), Is.True, "Logzilla.exe was not built beside the tests: " + exe);
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
        foreach (var sample in samples)
        {
            start.ArgumentList.Add(Sample(sample));
        }
        start.Environment[Paths.DataDirVariable] = dataDir;
        return new LogzillaApp(FlaUI.Core.Application.Launch(start), dataDir);
    }

    public static string Sample(string name) => Path.Combine(AppContext.BaseDirectory, "samples", name);

    /// <summary>The control with this automation id, waiting for it to appear.</summary>
    public AutomationElement Find(string automationId)
    {
        var found = Retry.WhileNull(() => Window.FindFirstDescendant(cf => cf.ByAutomationId(automationId)), Patience).Result;
        Assert.That(found, Is.Not.Null, "No control with automation id '" + automationId + "'.");
        return found;
    }

    /// <summary>The grid's own account of what is on screen: "showing 1 to 20 of 30".</summary>
    public string Showing => Value("LogGrid");

    /// <summary>How many results the current filter leaves: the "of N" in <see cref="Showing"/>.</summary>
    public int ResultCount
    {
        get
        {
            var s = Showing;
            var of = s.LastIndexOf(" of ", StringComparison.Ordinal);
            return of < 0 ? -1 : int.Parse(s[(of + 4)..], CultureInfo.InvariantCulture);
        }
    }

    public string Value(string automationId) =>
        Find(automationId).Patterns.LegacyIAccessible.Pattern.Value.ValueOrDefault ?? "";

    /// <summary>Presses a button, tab or chip the way a screen reader would — no mouse,
    /// so nothing else on the desktop can be clicked by mistake.</summary>
    public void Press(string automationId) =>
        Find(automationId).Patterns.LegacyIAccessible.Pattern.DoDefaultAction();

    public bool IsOn(string automationId) =>
        (Find(automationId).Patterns.LegacyIAccessible.Pattern.State.ValueOrDefault & AccessibilityState.STATE_SYSTEM_CHECKED) != 0;

    /// <summary>Puts text in a box the way pasting would: no keystrokes are sent.</summary>
    public void SetText(string automationId, string text) =>
        Find(automationId).Patterns.Value.Pattern.SetValue(text);

    public string Text(string automationId) =>
        Find(automationId).Patterns.Value.Pattern.Value.ValueOrDefault ?? "";

    /// <summary>Waits until <paramref name="condition"/> holds, failing with what was last seen.</summary>
    public void WaitUntil(Func<bool> condition, string what)
    {
        var ok = Retry.WhileFalse(() =>
        {
            try { return condition(); }
            catch (Exception ex) when (ex is COMException or InvalidOperationException) { return false; }
        }, Patience, TimeSpan.FromMilliseconds(100)).Result;
        if (!ok)
        {
            Screenshot("timed-out");
        }
        Assert.That(ok, Is.True, "Timed out waiting until " + what + ". The grid says: " + Showing);
    }

    /// <summary>Waits for the log to finish loading and filtering, and for the grid to
    /// report a result count that has stopped changing.</summary>
    public void WaitForResults()
    {
        var last = -2;
        var stable = 0;
        WaitUntil(() =>
        {
            var n = ResultCount;
            stable = n >= 0 && n == last ? stable + 1 : 0;
            last = n;
            return stable >= 3;
        }, "the result count settles");
    }

    /// <summary>
    /// Types into the focused control with real keystrokes — the only honest way to
    /// test keyboard handling. Refuses if Logzilla is not the foreground window, so a
    /// test can never type into whatever else is open on the desktop.
    /// </summary>
    public void Type(string automationId, string text)
    {
        var target = Find(automationId);
        Window.SetForeground();
        target.Focus();
        Wait.UntilInputIsProcessed();
        EnsureForeground();
        foreach (var c in text)
        {
            EnsureForeground();
            Keyboard.Type(c);
            Wait.UntilInputIsProcessed();
        }
    }

    public void Key(VirtualKeyShort key)
    {
        EnsureForeground();
        Keyboard.Press(key);
        Keyboard.Release(key);
        Wait.UntilInputIsProcessed();
    }

    private void EnsureForeground()
    {
        if (GetForegroundWindow() != Window.Properties.NativeWindowHandle.ValueOrDefault)
        {
            Assert.Inconclusive("Logzilla lost the foreground (another window was activated), so no keys were sent.");
        }
    }

    /// <summary>Resizes the window, through UI Automation rather than by dragging its edge.</summary>
    public void Resize(int width, int height)
    {
        if (Window.Patterns.Window.Pattern.WindowVisualState.Value != WindowVisualState.Normal)
        {
            Window.Patterns.Window.Pattern.SetWindowVisualState(WindowVisualState.Normal);
        }
        Window.Patterns.Transform.Pattern.Resize(width, height);
        Wait.UntilInputIsProcessed();
    }

    /// <summary>
    /// Saves a picture of Logzilla's window alone and attaches it to the test result.
    /// Rendered by the window itself (PrintWindow), not copied off the screen: a screen
    /// copy of the window's area shows whatever else sits on top of it, which would put
    /// the user's other applications into test output.
    /// </summary>
    public string Screenshot(string name)
    {
        var dir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "e2e-screenshots");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, Safe(TestContext.CurrentContext.Test.Name) + "-" + Safe(name) + ".png");
        var hwnd = Window.Properties.NativeWindowHandle.ValueOrDefault;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r) || r.Right <= r.Left || r.Bottom <= r.Top)
        {
            return null;
        }
        using var bitmap = new Bitmap(r.Right - r.Left, r.Bottom - r.Top);
        using (var g = Graphics.FromImage(bitmap))
        {
            var dc = g.GetHdc();
            try
            {
                if (!PrintWindow(hwnd, dc, PwRenderFullContent))
                {
                    return null;
                }
            }
            finally
            {
                g.ReleaseHdc(dc);
            }
        }
        bitmap.Save(file, ImageFormat.Png);
        TestContext.AddTestAttachment(file);
        return file;
    }

    private const uint PwRenderFullContent = 2;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    private static string Safe(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        }
        return sb.ToString();
    }

    public void Dispose()
    {
        try
        {
            if (!_app.HasExited)
            {
                _app.Close();
                Retry.WhileFalse(() => _app.HasExited, TimeSpan.FromSeconds(5));
            }
        }
        finally
        {
            if (!_app.HasExited)
            {
                _app.Kill();
            }
            _app.Dispose();
            _automation.Dispose();
            try { Directory.Delete(_dataDir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
