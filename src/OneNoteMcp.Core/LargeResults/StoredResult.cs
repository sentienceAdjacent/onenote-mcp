using OneNoteMcp.Core.Results;
using OneNoteMcp.Xml;

namespace OneNoteMcp.Core.LargeResults;

/// <summary>
/// One AD-21 grant that allowed the original call, recorded so <c>read_result</c> can check it again (AD-20).
/// </summary>
/// <param name="SectionKey">The protected section's <c>SectionKey</c>.</param>
/// <param name="SectionId">The section's ID in the stored body, whose span the grant covers.</param>
/// <param name="Lifetime">The grant's lifetime. A grant that couldn't be saved is recorded as <c>once</c>.</param>
/// <param name="ExpiresAt">When an <c>hours</c> grant ends.</param>
/// <param name="WholeResult">True when the whole body is inside the section (a page read), so every part needs the grant.</param>
public sealed record ResultGrant(string SectionKey, string SectionId, Access.ReadGrantLifetime Lifetime, DateTimeOffset? ExpiresAt, bool WholeResult);

/// <summary>The AD-21 grants that allowed the original call (AD-20): <c>{sectionKey, lifetime, expiresAt?}</c> per section.</summary>
public sealed record ResultGrants(IReadOnlyList<ResultGrant> Grants)
{
    public static ResultGrants None { get; } = new([]);

    /// <summary>True when a <c>once</c> grant allowed part of the result: the entry then expires <c>ttlMinutes</c> after the original call, never extended by reads.</summary>
    public bool HasOnce => Grants.Any(g => g.Lifetime == Access.ReadGrantLifetime.Once);
}

/// <summary>
/// One oversized result in the <see cref="ResultStore"/> (AD-20): the body the tool would have returned inline, its span
/// tree, and what <c>read_result</c> needs to answer from it. Held in memory only, never written or logged (AD-13).
/// </summary>
public sealed class StoredResult
{
    public StoredResult(
        string resultId,
        ResultKind kind,
        SpanTree tree,
        string? piBasicXml,
        ToolSuccess original,
        IReadOnlyList<int> subtreeHashChars,
        ResultGrants grants)
    {
        ArgumentException.ThrowIfNullOrEmpty(resultId);
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(subtreeHashChars);
        ArgumentNullException.ThrowIfNull(grants);

        ResultId = resultId;
        Kind = kind;
        Tree = tree;
        PiBasicXml = piBasicXml;
        Original = original;
        SubtreeHashChars = subtreeHashChars;
        Grants = grants;

        // UTF-16 bytes (AD-20). A piBasic read that is the body itself is counted once. The span tree is counted too.
        var chars = (long)tree.Text.Length + (piBasicXml is null || ReferenceEquals(piBasicXml, tree.Text) ? 0 : piBasicXml.Length);
        Bytes = (chars * sizeof(char)) + TreeBytes(tree);
    }

    /// <summary>
    /// An estimate of a span tree's memory, as <c>results.maxStoreMB</c> counts it: <see cref="NodeBytes"/> per node (the
    /// node, its list and map slots, and its <see cref="SubtreeHashChars"/> slot), plus its ref and label as UTF-16.
    /// </summary>
    public static long TreeBytes(SpanTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        long bytes = 0;
        foreach (var node in tree.Nodes)
        {
            bytes += NodeBytes + ((long)(node.Ref.Length + (node.Label?.Length ?? 0)) * sizeof(char));
        }

        return bytes;
    }

    /// <summary>The fixed cost counted per span node.</summary>
    public const int NodeBytes = 160;

    /// <summary>A GUID in <c>N</c> format.</summary>
    public string ResultId { get; }

    public ResultKind Kind { get; }

    /// <summary>The stored body and its spans.</summary>
    public SpanTree Tree { get; }

    public string Body => Tree.Text;

    /// <summary>For a page read, the piBasic XML the basis came from (AD-17), so part hashes never come from piBinaryData.</summary>
    public string? PiBasicXml { get; }

    /// <summary>The original success: its meta (the full basis included) and warnings come with the first index.</summary>
    public ToolSuccess Original { get; }

    /// <summary>The page basis computed from <see cref="PiBasicXml"/>, with every Outline and OE hash; null for other kinds.</summary>
    public PageBasisResult? Basis => Original.Meta.Basis;

    /// <summary>
    /// By <see cref="SpanNode.Order"/>: the serialized length of the <c>elementHashes</c> members a part holding that
    /// node's whole subtree would carry. Runs are sized with it (AD-20's header allowance).
    /// </summary>
    public IReadOnlyList<int> SubtreeHashChars { get; }

    public ResultGrants Grants { get; }

    /// <summary>The entry's size for <c>results.maxStoreMB</c>, in UTF-16 bytes.</summary>
    public long Bytes { get; }

    /// <summary>
    /// For an entry made under a <c>once</c> grant, <c>ttlMinutes</c> after the original call: it expires then whatever
    /// its reads (AD-20). Null otherwise. The store's lock guards it.
    /// </summary>
    internal DateTimeOffset? AbsoluteExpiry { get; set; }

    /// <summary>When the entry was last read; the store's lock guards it.</summary>
    internal DateTimeOffset LastRead { get; set; }

    /// <summary>The store's read counter at the last read: least recently read goes first, even when two reads share a clock tick.</summary>
    internal long ReadSequence { get; set; }
}
