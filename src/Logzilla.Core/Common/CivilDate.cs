namespace Logzilla.Core.Common;

/// <summary>Calendar arithmetic on the proleptic Gregorian calendar.</summary>
public static class CivilDate
{
    /// <summary>Days since 1970-01-01 for a civil date (Howard Hinnant's algorithm).</summary>
    public static long DaysSinceEpoch(int year, int month, int day)
    {
        year -= month <= 2 ? 1 : 0;
        long era = (year >= 0 ? year : year - 399) / 400;
        var yearOfEra = year - (era * 400);
        var dayOfYear = ((153 * (month + (month > 2 ? -3 : 9))) + 2) / 5 + day - 1;
        var dayOfEra = (yearOfEra * 365) + (yearOfEra / 4) - (yearOfEra / 100) + dayOfYear;
        return (era * 146097) + dayOfEra - 719468;
    }
}
