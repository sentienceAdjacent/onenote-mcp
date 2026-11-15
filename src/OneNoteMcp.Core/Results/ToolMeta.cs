namespace OneNoteMcp.Core.Results;

/// <summary>
/// The envelope's <c>meta</c> object. <c>correlationId</c> is on both the success and the error shape (AD-9). The other
/// fields carry a read tool's scalar result, like AD-11's <c>meta.outputPath</c>: <see cref="ParentId"/>
/// (<c>get_hierarchy_parent</c>; empty at a notebook), <see cref="Path"/> (<c>get_special_location</c>),
/// <see cref="Link"/> (the hyperlink tools), <see cref="Basis"/> and <see cref="BinaryUnavailable"/>
/// (<c>get_page_content</c>; AD-17, AD-4), <see cref="Binary"/> (<c>get_binary_page_content</c>),
/// <see cref="Filtered"/> and <see cref="PossiblyIncomplete"/> (the search tools; AD-12), <see cref="Result"/> (a
/// stored oversized result and every <c>read_result</c> response; AD-20), <see cref="Withheld"/> (the IDs of the
/// password-protected sections whose pages a broad read left out; AD-21), <see cref="Grants"/> (<c>list_grants</c>) and
/// <see cref="PageId"/> (the page <c>create_page</c> or <c>restore_from_journal</c> made; Phill, 2026-11-10, 2.4 plan Q2),
/// <see cref="ParentId"/> also for the section <c>restore_from_journal</c> wrote to, and <see cref="JournalEntryId"/> (the
/// recovery-journal entry a page write took, for <c>restore_from_journal</c>; Phill, 2026-11-12, 2.11 plan Q1), and
/// <see cref="ObjectId"/> (the section, section group or notebook a create made or found, with <see cref="ParentId"/> for a
/// section or group and <see cref="Path"/>, read back, for a notebook; Phill, 2026-11-13, 5.3 plan Q5). They are null, and
/// so omitted on the wire, on every other result.
/// </summary>
public sealed record ToolMeta(
    string CorrelationId,
    string? ParentId = null,
    string? Path = null,
    string? Link = null,
    PageBasisResult? Basis = null,
    int? BinaryUnavailable = null,
    string? Binary = null,
    IReadOnlyList<FilteredPage>? Filtered = null,
    bool? PossiblyIncomplete = null,
    ResultMeta? Result = null,
    IReadOnlyList<string>? Withheld = null,
    IReadOnlyList<GrantListing>? Grants = null,
    string? PageId = null,
    string? JournalEntryId = null,
    string? ObjectId = null);

/// <summary>
/// One grant as <c>list_grants</c> shows it (AD-6, AD-10). <see cref="Kind"/> is <c>readSection</c> for an AD-21 read
/// grant, whose <see cref="Key"/> is its <c>SectionKey</c>, <see cref="Status"/> <c>active</c>, <c>sectionNotOpen</c>
/// or <c>expired</c>, and <see cref="Lifetime"/> <c>once</c> (this process's live window, never stored), <c>hours</c> or
/// <c>untilRevoked</c>. It is <c>writeNotebook</c> for an AD-6 write grant, whose <see cref="Key"/> and
/// <see cref="NotebookKey"/> are its <c>NotebookKey</c>, <see cref="Status"/> <c>active</c> or <c>notebookNotOpen</c>,
/// and <see cref="Lifetime"/> always <c>untilRevoked</c>. <see cref="Name"/> and <see cref="IdAtGrant"/> are stored for
/// information only. Dates are UTC ISO-8601.
/// </summary>
public sealed record GrantListing(
    string Kind,
    string Key,
    string Name,
    string NotebookKey,
    string IdAtGrant,
    string GrantedAt,
    string GrantedVia,
    string Lifetime,
    string Status,
    string? ExpiresAt = null);
