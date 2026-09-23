using System;
using Logzilla.Core.Common;

namespace Logzilla.Core.Implementation.Tests;

/// <summary>A clock frozen at a known instant, so nothing under test depends on today.</summary>
internal sealed class TestClock : ICurrentTimeSupplier
{
    public static readonly TestClock Instance = new(new DateTime(2026, 3, 1, 12, 0, 0));

    public TestClock(DateTime now) => Now = now;

    public DateTime Now { get; }
}
