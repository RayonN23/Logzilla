namespace Logzilla.Core.Logs;

/// <summary>
/// A UTC offset that a file's timestamps were written with, in force from the
/// instant it first appeared. A file that logs local time with its offset
/// (<c>+01:00</c>, then <c>+02:00</c> after the clocks change) carries one per change.
/// </summary>
public readonly record struct ZoneOffset(double FromMs, int Minutes);
