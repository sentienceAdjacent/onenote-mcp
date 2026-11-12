using System.ComponentModel;
using OneNoteMcp.Core.Writes;

namespace OneNoteMcp.Server.Tools;

/// <summary>
/// The input twin of <see cref="Core.Results.PageBasisResult"/>: AD-17's <c>basis</c> argument,
/// <c>{lastModifiedTime?, pageHash?, elementHashes?}</c>, passed back unchanged from a read's <c>meta.basis</c>. Every
/// member is optional.
/// </summary>
public sealed record PageBasisArgument(
    [property: Description("meta.basis.lastModifiedTime, unchanged.")] string? LastModifiedTime = null,
    [property: Description("meta.basis.pageHash, unchanged.")] string? PageHash = null,
    [property: Description("meta.basis.elementHashes, unchanged: objectID to hash.")] IReadOnlyDictionary<string, string>? ElementHashes = null)
{
    public PageWriteBasis ToCore() => new(LastModifiedTime, PageHash, ElementHashes);
}
