using System.Globalization;
using System.Text.Json;

namespace OneNoteMcp.Core.Settings;

/// <summary>How <c>settings.json</c> is parsed, shared by <see cref="SettingsStore"/> and <see cref="SettingsWriter"/> (AD-10).</summary>
internal static class SettingsJson
{
    /// <summary>
    /// The parse options. The store reads with <paramref name="allowDuplicateProperties"/> true: an exact duplicate key
    /// is allowed, a lookup gets the last one, and inside <c>notebooks{}</c> or <c>protectedSections{}</c> it counts like
    /// two keys that differ only in case, which makes the file invalid (AD-6). A writer's save loads with false: it refuses
    /// a file with any duplicate key. <c>grant --revoke</c> parses with true, since a <c>JsonObject</c> can't hold a
    /// duplicate key, and writes the file back property by property without every entry equal to its key, exact
    /// duplicates included (story 2.6, 2.3 plan Q7).
    /// </summary>
    internal static JsonDocumentOptions Options(bool allowDuplicateProperties) => new() { AllowDuplicateProperties = allowDuplicateProperties };

    /// <summary>An ISO-8601 date-time such as <c>expiresAt</c>, read as UTC (a value without an offset is taken as UTC).</summary>
    internal static bool TryParseUtc(string? text, out DateTimeOffset date) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out date);
}
