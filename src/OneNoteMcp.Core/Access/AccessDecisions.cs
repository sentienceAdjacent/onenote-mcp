using OneNoteMcp.Core.LargeResults;
using OneNoteMcp.Core.Results;

namespace OneNoteMcp.Core.Access;

/// <summary>
/// What a target-scoped check decided (AD-21): no grant was needed (<see cref="Grant"/> null), or the read is allowed
/// under <see cref="Grant"/>. A denied read throws <see cref="ErrorCode.ReadNotGranted"/> instead. <see cref="Warnings"/>
/// carries <c>grantNotSaved</c> when the user's grant couldn't be stored.
/// </summary>
public sealed record TargetAccess(ResultGrant? Grant, IReadOnlyList<ToolWarning> Warnings)
{
    public static TargetAccess Unprotected { get; } = new(null, []);

    /// <summary>The grants a stored result made under this decision carries (AD-20).</summary>
    public ResultGrants ResultGrants => Grant is { } grant ? new ResultGrants([grant]) : ResultGrants.None;
}

/// <summary>
/// What a broad check decided (AD-21): the protected sections whose pages are withheld, by ID, and the grants for the
/// protected sections whose pages are included. <see cref="WithholdAll"/> is true when the read's start node is itself
/// inside a withheld section, so every page in it is withheld.
/// </summary>
public sealed record BroadAccess(IReadOnlyList<string> Withheld, IReadOnlyList<ResultGrant> Granted, bool WithholdAll = false)
{
    public static BroadAccess None { get; } = new([], []);

    /// <summary>
    /// True for a page in a withheld section. Fails closed: when anything is withheld, a page the XML shows outside any
    /// <c>one:Section</c> (an empty <paramref name="sectionId"/>) is withheld too, since its section can't be told apart.
    /// </summary>
    public bool IsWithheld(string sectionId) =>
        WithholdAll || (sectionId.Length > 0 ? Withheld.Contains(sectionId, StringComparer.Ordinal) : Withheld.Count > 0);

    /// <summary>
    /// True for a page in any protected section of the scope, withheld or granted: such a page is never looked up in or
    /// added to the search page-text cache (AD-12). A page the XML shows outside any section counts when the scope has
    /// any protected section.
    /// </summary>
    public bool IsProtected(string sectionId) =>
        IsWithheld(sectionId)
        || (sectionId.Length > 0 ? Granted.Any(g => string.Equals(g.SectionId, sectionId, StringComparison.Ordinal)) : Granted.Count > 0);

    /// <summary>One <c>readNotGranted</c> warning per withheld section, naming its ID only (AD-9).</summary>
    public IReadOnlyList<ToolWarning> Warnings =>
    [
        .. Withheld.Select(id => new ToolWarning(
            ToolWarnings.ReadNotGranted,
            $"Section {id} is password-protected, so its pages were left out: reading them needs the user's permission.",
            ReadAccess.WithheldUserAction)),
    ];

    /// <summary><c>meta.withheld</c>: the section IDs, or null when nothing was withheld, so the field is omitted.</summary>
    public IReadOnlyList<string>? Meta => Withheld.Count > 0 ? Withheld : null;

    public ResultGrants ResultGrants => Granted.Count > 0 ? new ResultGrants(Granted) : ResultGrants.None;
}
