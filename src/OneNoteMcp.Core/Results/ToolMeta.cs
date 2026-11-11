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
/// <see cref="PageId"/> (the page <c>create_page</c> made; Phill, 2026-11-10, 2.4 plan Q2). They are null, and so omitted
/// on the wire, on every other result.
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
    string? PageId = null);

/// <summary>
/// One stored grant as <c>list_grants</c> shows it (AD-6, AD-10). <see cref="Kind"/> is <c>readSection</c> for an AD-21
/// read grant. <see cref="Key"/> is its <c>SectionKey</c>; <see cref="Name"/>, <see cref="NotebookKey"/> and
/// <see cref="IdAtGrant"/> are stored for information only. <see cref="Status"/> is <c>active</c>,
/// <c>sectionNotOpen</c> or <c>expired</c>. <see cref="Lifetime"/> is <c>hours</c> or <c>untilRevoked</c>; <c>once</c>
/// grants are never stored, so never listed. Dates are UTC ISO-8601.
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
