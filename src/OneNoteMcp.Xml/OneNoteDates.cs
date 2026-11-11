using System.Globalization;

namespace OneNoteMcp.Xml;

/// <summary>
/// OneNote's dates, as page and hierarchy XML carry them (<c>lastModifiedTime</c>, ISO 8601 with a <c>Z</c>). Only this
/// project parses them (AD-17): a write's expected date reaches the port as OneNote's exact string, and the gateway
/// converts it here before COM.
/// </summary>
public static class OneNoteDates
{
    /// <summary>
    /// <paramref name="value"/> as a <see cref="DateTimeKind.Utc"/> <see cref="DateTime"/>, read in the invariant culture
    /// with no offset meaning UTC (the parse spike 2.2's probes used). Null when it is empty, unparsable or
    /// <see cref="DateTime.MinValue"/>, which OneNote would read as "no date".
    /// </summary>
    public static DateTime? ToUtc(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date)
            || date == DateTime.MinValue)
        {
            return null;
        }

        return DateTime.SpecifyKind(date, DateTimeKind.Utc);
    }
}
