using System.Text.Json.Serialization;
using OneNoteMcp.Xml;

namespace OneNoteMcp.Core.Results;

/// <summary>
/// AD-17's <c>meta.basis</c> on the wire: <c>{lastModifiedTime?, pageHash, elementHashes}</c>, exactly as
/// <see cref="PageBasis"/> computes it from a piBasic read. The caller passes it back unchanged on a write.
/// <see cref="LastModifiedTime"/> is the raw root string OneNote returned, omitted when the page XML has none; it is an
/// optional parameter so the output schema doesn't list it as required.
/// </summary>
public sealed record PageBasisResult(
    [property: JsonPropertyOrder(1)] string PageHash,
    [property: JsonPropertyOrder(2)] IReadOnlyDictionary<string, string> ElementHashes,
    [property: JsonPropertyOrder(0)] string? LastModifiedTime = null)
{
    public static PageBasisResult From(PageBasis basis)
    {
        ArgumentNullException.ThrowIfNull(basis);
        return new PageBasisResult(basis.PageHash, basis.ElementHashes, basis.LastModifiedTime);
    }
}
