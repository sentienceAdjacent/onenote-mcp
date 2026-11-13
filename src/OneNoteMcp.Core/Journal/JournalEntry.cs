using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using OneNoteMcp.Xml;

namespace OneNoteMcp.Core.Journal;

/// <summary>
/// One recovery-journal entry (AD-8), in AD-10's format: <c>{schemaVersion, entryId, correlationId, operation,
/// operationClass, snapshotKind, objectId, notebookId, notebookPath, notebookName, parentId, parentNamePath, sectionKey?,
/// protected?, title, takenAtUtc, binaryComplete, missingBinary[], xml | backupFile}</c>.
/// <para>Optional members are left out when null, and a reader ignores members it doesn't know, so a later story can add
/// a member to <c>schemaVersion</c> 1 without breaking older readers (epic 5.5's recycle-bin page ID).</para>
/// <para>An entry from a password-protected section carries <see cref="SectionKey"/>; on disk its <see cref="Title"/> and
/// <see cref="Xml"/> are absent and <see cref="Protected"/> holds them, encrypted (<see cref="JournalStore"/>).</para>
/// </summary>
public sealed record JournalEntry
{
    /// <summary>The format this server writes.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary><see cref="SnapshotKind"/> of a page snapshot.</summary>
    public const string PageKind = "page";

    /// <summary><see cref="SnapshotKind"/> of a hierarchy snapshot (story 5.4): the hierarchy XML of the node a write changed.</summary>
    public const string HierarchyKind = "hierarchy";

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>A GUID (Conventions), also in the file's name.</summary>
    public Guid EntryId { get; init; }

    /// <summary>The tool call that took the snapshot (AD-18).</summary>
    public string CorrelationId { get; init; } = string.Empty;

    /// <summary>The tool that wrote, such as <c>update_page_content</c>.</summary>
    public string Operation { get; init; } = string.Empty;

    /// <summary>The write's AD-7 class, such as <c>PageWrite</c>.</summary>
    public string OperationClass { get; init; } = string.Empty;

    /// <summary><c>page</c>, <c>hierarchy</c> (story 5.4) or <c>onenoteBackup</c>.</summary>
    public string SnapshotKind { get; init; } = PageKind;

    /// <summary>The snapshotted object's ID: for a page, the page's; for a hierarchy entry, the node at the root of <see cref="Xml"/>.</summary>
    public string ObjectId { get; init; } = string.Empty;

    public string NotebookId { get; init; } = string.Empty;

    /// <summary>The notebook's <c>NotebookKey</c> (AD-6): restore finds the notebook by it, never by ID (AD-8).</summary>
    public string NotebookPath { get; init; } = string.Empty;

    /// <summary>The notebook's displayed name, as in the grants.</summary>
    public string NotebookName { get; init; } = string.Empty;

    /// <summary>The owning section's ID; for a hierarchy entry, the node's parent (empty for a notebook).</summary>
    public string ParentId { get; init; } = string.Empty;

    /// <summary>
    /// The owning section's groups and name inside the notebook, joined by <c>/</c>, such as <c>Group/Section</c>; for a
    /// hierarchy entry, the section groups between the notebook and the node (empty at the notebook's top level or for a
    /// notebook).
    /// </summary>
    public string ParentNamePath { get; init; } = string.Empty;

    /// <summary>The protection marker (AD-8, AD-21): the section's <c>SectionKey</c>, only on an entry from a protected section.</summary>
    public string? SectionKey { get; init; }

    /// <summary>On disk only: the base64 DPAPI blob (current user) of the JSON <c>{title, xml}</c> of a protected entry.</summary>
    public string? Protected { get; init; }

    /// <summary>The page's title; a hierarchy entry's node's displayed name, as it was before the write. Absent on disk for a protected entry.</summary>
    public string? Title { get; init; }

    /// <summary>When the snapshot was taken, UTC.</summary>
    public DateTime TakenAtUtc { get; init; }

    /// <summary>False when <see cref="MissingBinary"/> lists anything.</summary>
    public bool BinaryComplete { get; init; }

    /// <summary>What the snapshot lacks: images with no local binary, and attachments (AD-8).</summary>
    public IReadOnlyList<MissingBinary> MissingBinary { get; init; } = [];

    /// <summary>The page's piBinaryData XML, unchanged; a hierarchy entry's <c>GetHierarchy</c> XML, unchanged. Absent on disk for a protected entry.</summary>
    public string? Xml { get; init; }

    /// <summary>A <c>onenoteBackup</c> entry's backup file (epic 5); never set by a page snapshot.</summary>
    public string? BackupFile { get; init; }

    /// <summary>
    /// The entry's JSON: camelCase members, nulls left out, unknown members ignored on read, dates as UTC ISO-8601. The
    /// relaxed encoder keeps the XML readable in the decompressed file; the file is never embedded in HTML.
    /// </summary>
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new UtcDateTimeConverter() },
    };

    /// <summary>Writes a UTC ISO-8601 date with milliseconds and <c>Z</c>; reads any ISO-8601 date as UTC.</summary>
    private sealed class UtcDateTimeConverter : JsonConverter<DateTime>
    {
        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetDateTimeOffset().UtcDateTime;

        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture));
    }
}
