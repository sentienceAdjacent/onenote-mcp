namespace OneNoteMcp.Core.Results;

/// <summary>
/// The envelope's <c>meta</c> object. <c>correlationId</c> is on both the success and the error shape (AD-9). The other
/// fields carry a read tool's scalar result, like AD-11's <c>meta.outputPath</c>: <see cref="ParentId"/>
/// (<c>get_hierarchy_parent</c>; empty at a notebook), <see cref="Path"/> (<c>get_special_location</c>) and
/// <see cref="Link"/> (the hyperlink tools). They are null, and so omitted on the wire, on every other result.
/// </summary>
public sealed record ToolMeta(
    string CorrelationId,
    string? ParentId = null,
    string? Path = null,
    string? Link = null);
