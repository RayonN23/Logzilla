namespace Logzilla.Core.Common;

/// <summary>
/// Supplies the current time. Reading the clock through this rather than
/// <see cref="System.DateTime.Now"/> keeps anything that depends on "now" testable.
/// </summary>
public interface ICurrentTimeSupplier
{
    System.DateTime Now { get; }
}
