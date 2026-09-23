using System;
using System.Windows.Forms;
using Logzilla.App.Infrastructure;
using NUnit.Framework;

namespace Logzilla.App.Tests;

/// <summary>
/// Runs once before any test in this assembly. Mirrors what MainForm's constructor
/// does at startup (Dpi.Init then Theme.InitFonts) so every owner-drawn control under
/// test sees the same non-null fonts and a deterministic Dpi.Factor that individual
/// fixtures (see DpiTests) restore to after they deliberately change it.
/// </summary>
[SetUpFixture]
public sealed class AssemblySetup
{
    [OneTimeSetUp]
    public void Init()
    {
        Environment.SetEnvironmentVariable("LOGZILLA_SCALE", "1");
        using (var probe = new Control())
        {
            Dpi.Init(probe);
        }
        Environment.SetEnvironmentVariable("LOGZILLA_SCALE", null);
        Theme.InitFonts();
    }
}
