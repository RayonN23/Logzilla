using System;
using Logzilla.Core.Common;

namespace Logzilla.Core.Implementation.Common;

/// <summary>The real clock. Tests substitute their own supplier instead.</summary>
internal sealed class SystemTimeSupplier : ICurrentTimeSupplier
{
    public DateTime Now => DateTime.Now;
}
