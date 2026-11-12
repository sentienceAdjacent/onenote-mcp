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
    /// The OLE Automation epoch, 1899-12-30 00:00 (OADate 0). COM passes a <see cref="DateTime"/> as an OLE
    /// <c>DATE</c>: this instant becomes 0, which OneNote reads as "no date" and so skips its last-modified check, and a
    /// date before the year 100 can't be converted at all (<see cref="OverflowException"/>).
    /// </summary>
    public static readonly DateTime OleAutomationEpoch = new(1899, 12, 30, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// <paramref name="value"/> as a <see cref="DateTimeKind.Utc"/> <see cref="DateTime"/>, read in the invariant culture
    /// with no offset meaning UTC (the parse spike 2.2's probes used). Null when it is empty, unparsable, or on or before
    /// <see cref="OleAutomationEpoch"/> (<see cref="DateTime.MinValue"/> included): such a date would reach OneNote as
    /// "no date", skipping the check, or fail to convert. OneNote never writes one, so it is never a page's real date.
    /// </summary>
    public static DateTime? ToUtc(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date)
            || date <= OleAutomationEpoch)
        {
            return null;
        }

        return DateTime.SpecifyKind(date, DateTimeKind.Utc);
    }
}
