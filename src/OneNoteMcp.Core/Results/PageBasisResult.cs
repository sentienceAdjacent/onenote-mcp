using System.Text.Json.Serialization;
using OneNoteMcp.Xml;

namespace OneNoteMcp.Core.Results;

/// <summary>
/// AD-17's <c>meta.basis</c> on the wire: <c>{lastModifiedTime?, pageHash, elementHashes?}</c>, from a piBasic read
/// (<see cref="PageBasis"/>). The caller passes it back unchanged on a write. <see cref="LastModifiedTime"/> is the raw root
/// string OneNote returned, omitted when the page XML has none. <see cref="ElementHashes"/> is omitted from an oversized
/// page's index (AD-20), whose parts each carry the hashes of the elements wholly inside them. Both are optional
/// parameters so the output schema doesn't list them as required.
/// </summary>
public sealed record PageBasisResult(
    [property: JsonPropertyOrder(1)] string PageHash,
    [property: JsonPropertyOrder(0)] string? LastModifiedTime = null,
    [property: JsonPropertyOrder(2)] IReadOnlyDictionary<string, string>? ElementHashes = null)
{
    public static PageBasisResult From(PageBasis basis)
    {
        ArgumentNullException.ThrowIfNull(basis);
        return new PageBasisResult(basis.PageHash, basis.LastModifiedTime, basis.ElementHashes);
    }
}
