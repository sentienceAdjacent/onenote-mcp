using System.Text.Json.Serialization;

namespace OneNoteMcp.Core.Results;

/// <summary>
/// <c>meta.result</c> (AD-20): on the index an oversized read returns, and on every <c>read_result</c> response.
/// <see cref="Namespaces"/> holds the root element's namespace declarations, keyed by attribute name
/// (<c>xmlns:one</c>), because a part's prefixes rely on them (AD-4); it is empty for a body that isn't XML.
/// <see cref="Pages"/> is the page count of the index in this response; it is omitted on a part.
/// </summary>
public sealed record ResultMeta(
    [property: JsonPropertyOrder(0)] string ResultId,
    [property: JsonPropertyOrder(1)] int TotalChars,
    [property: JsonPropertyOrder(2)] string ExpiresAt,
    [property: JsonPropertyOrder(3)] IReadOnlyDictionary<string, string> Namespaces,
    [property: JsonPropertyOrder(4)] int? Pages = null);

/// <summary>
/// An AD-20 index: one page of the entries for the children of <see cref="Ref"/>, or for the members of
/// <see cref="Run"/> when a run was too big to return. <see cref="Element"/> and <see cref="Chars"/> describe the
/// element (or run) indexed; <see cref="Chars"/> is its span's length, so <c>offset</c> reads know where it ends.
/// </summary>
public sealed record ResultIndex(
    [property: JsonPropertyOrder(0)] string Ref,
    [property: JsonPropertyOrder(3)] int Chars,
    [property: JsonPropertyOrder(4)] int Page,
    [property: JsonPropertyOrder(5)] IReadOnlyList<IndexEntry> Entries,
    [property: JsonPropertyOrder(1)] IReadOnlyList<string>? Run = null,
    [property: JsonPropertyOrder(2)] string? Element = null);

/// <summary>
/// One index entry (AD-20): either one element (<see cref="Ref"/>, <see cref="Element"/>, <see cref="Children"/>,
/// <see cref="Contains"/>, <see cref="Label"/>) or a run of consecutive siblings (<see cref="Run"/> as
/// <c>[firstRef, lastRef]</c>, <see cref="Count"/>, <see cref="FirstLabel"/>, <see cref="LastLabel"/>).
/// <see cref="Chars"/> is the length of its span in the stored result. Unused fields are omitted.
/// </summary>
public sealed record IndexEntry(
    [property: JsonPropertyOrder(3)] int Chars,
    [property: JsonPropertyOrder(0)] string? Ref = null,
    [property: JsonPropertyOrder(1)] IReadOnlyList<string>? Run = null,
    [property: JsonPropertyOrder(2)] string? Element = null,
    [property: JsonPropertyOrder(4)] int? Children = null,
    [property: JsonPropertyOrder(5)] int? Count = null,
    [property: JsonPropertyOrder(6)] ContainsCounts? Contains = null,
    [property: JsonPropertyOrder(7)] string? Label = null,
    [property: JsonPropertyOrder(8)] string? FirstLabel = null,
    [property: JsonPropertyOrder(9)] string? LastLabel = null);

/// <summary>Counts of <c>one:Table</c>, <c>one:Image</c> and <c>one:InsertedFile</c> anywhere below an element (AD-20). Zero counts are omitted.</summary>
public sealed record ContainsCounts(
    [property: JsonPropertyOrder(0)] int? Table = null,
    [property: JsonPropertyOrder(1)] int? Image = null,
    [property: JsonPropertyOrder(2)] int? InsertedFile = null);
