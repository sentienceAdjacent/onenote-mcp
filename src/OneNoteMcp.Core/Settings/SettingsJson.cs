using System.Globalization;
using System.Text.Json;

namespace OneNoteMcp.Core.Settings;

/// <summary>How <c>settings.json</c> is parsed, shared by <see cref="SettingsStore"/> and <see cref="SettingsWriter"/> (AD-10).</summary>
internal static class SettingsJson
{
    /// <summary>
    /// The parse options. The store reads with <paramref name="allowDuplicateProperties"/> true: an exact duplicate key
    /// is allowed, a lookup gets the last one, and inside <c>protectedSections</c> it counts like two keys that differ
    /// only in case. The writer loads with false: it refuses a file with any duplicate key, so <c>grant --revoke</c> can't
    /// clear an exact duplicate. Exact duplicate keys are a spec gap (AD-21 covers only keys that differ in case), so this
    /// difference is kept, and kept visible here, until epic 2 decides it (<c>deferred-work.md</c>).
    /// </summary>
    internal static JsonDocumentOptions Options(bool allowDuplicateProperties) => new() { AllowDuplicateProperties = allowDuplicateProperties };

    /// <summary>An ISO-8601 date-time such as <c>expiresAt</c>, read as UTC (a value without an offset is taken as UTC).</summary>
    internal static bool TryParseUtc(string? text, out DateTimeOffset date) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out date);
}
