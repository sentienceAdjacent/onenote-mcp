using System.Runtime.CompilerServices;
using OneNoteMcp.Xml;

namespace OneNoteMcp.Core.Access;

/// <summary>
/// The AD-21 check's two parent walks over one lazy iterator of ancestors (AD-6). Each ancestor costs one
/// <c>GetHierarchyParent</c> and one <c>GetHierarchy</c> at <c>self</c>, made only when the walk asks for that ancestor, so
/// a walk that stops early makes no further calls and never sees an error from higher up. Each walk keeps its own stop
/// rule and its own handling of a chain deeper than <see cref="MaxChainDepth"/>. An exception from either call is an
/// ordinary error and never ends a walk.
/// </summary>
internal sealed class HierarchyWalk(IOneNoteGateway gateway)
{
    /// <summary>A guard against a parent chain that never ends; real hierarchies are a few levels deep.</summary>
    internal const int MaxChainDepth = 64;

    /// <summary>
    /// The section that owns <paramref name="objectId"/>: up the chain to the first <c>one:Section</c>. Null when the chain
    /// reaches a section group or notebook first, or ends (an empty parent, AD-6). A chain of more than
    /// <see cref="MaxChainDepth"/> pages is an <see cref="ErrorCode.Internal"/> error.
    /// </summary>
    internal async Task<HierarchyNode?> OwningSectionAsync(string objectId, CancellationToken ct)
    {
        var depth = 0;
        await foreach (var node in AncestorsAsync(objectId ?? string.Empty, ct).ConfigureAwait(false))
        {
            if (node.Kind == HierarchyKind.Section)
            {
                return node;
            }

            if (node.Kind != HierarchyKind.Page)
            {
                return null;
            }

            if (++depth == MaxChainDepth)
            {
                throw new OneNoteException(ErrorCode.Internal, $"The parent chain of {objectId} is more than {MaxChainDepth} levels deep.");
            }
        }

        return null;
    }

    /// <summary>
    /// The notebook that owns <paramref name="objectId"/>, for the write grant (AD-6): up the chain to the first
    /// <c>one:Notebook</c>, whose <c>path</c> comes live from <c>GetHierarchy</c>. Null when the chain ends first (the
    /// object is itself a notebook) or runs past <see cref="MaxChainDepth"/> ancestors.
    /// </summary>
    internal async Task<HierarchyNode?> OwningNotebookAsync(string objectId, CancellationToken ct)
    {
        var depth = 0;
        await foreach (var node in AncestorsAsync(objectId ?? string.Empty, ct).ConfigureAwait(false))
        {
            if (node.Kind == HierarchyKind.Notebook)
            {
                return node;
            }

            if (++depth == MaxChainDepth)
            {
                break;
            }
        }

        return null;
    }

    /// <summary>
    /// The notebook and section groups around <paramref name="sectionId"/>, groups outermost first, for the grant dialog:
    /// up the chain to the first <c>one:Notebook</c>. Every other ancestor counts as a group. A chain that ends, or runs
    /// past <see cref="MaxChainDepth"/> ancestors, before a notebook gives a null notebook and the groups seen so far.
    /// </summary>
    internal async Task<(HierarchyNode? Notebook, IReadOnlyList<string> Groups)> LocationAsync(string sectionId, CancellationToken ct)
    {
        var groups = new List<string>();
        await foreach (var node in AncestorsAsync(sectionId, ct).ConfigureAwait(false))
        {
            if (node.Kind == HierarchyKind.Notebook)
            {
                groups.Reverse();
                return (node, groups);
            }

            groups.Add(node.Name);
            if (groups.Count == MaxChainDepth)
            {
                break;
            }
        }

        groups.Reverse();
        return (null, groups);
    }

    /// <summary>
    /// Where the page <paramref name="pageId"/> sits, in one walk, for a journal snapshot (AD-8): its owning
    /// <c>one:Section</c> (ID, name, path and <c>encrypted</c>, read live), the section groups between that section and the
    /// notebook (names, outermost first), and the notebook. Page ancestors (a subpage's parent page) are skipped. A null
    /// section or notebook means the chain ended first, reached something else first, or ran past
    /// <see cref="MaxChainDepth"/> ancestors.
    /// </summary>
    internal async Task<PageLocation> PageLocationAsync(string pageId, CancellationToken ct)
    {
        HierarchyNode? section = null;
        var groups = new List<string>();
        var depth = 0;
        await foreach (var node in AncestorsAsync(pageId ?? string.Empty, ct).ConfigureAwait(false))
        {
            if (++depth > MaxChainDepth)
            {
                break;
            }

            if (section is null)
            {
                if (node.Kind == HierarchyKind.Section)
                {
                    section = node;
                    continue;
                }

                if (node.Kind == HierarchyKind.Page)
                {
                    continue;
                }

                break;
            }

            if (node.Kind == HierarchyKind.Notebook)
            {
                groups.Reverse();
                return new PageLocation(section, groups, node);
            }

            groups.Add(node.Name);
        }

        groups.Reverse();
        return new PageLocation(section, groups, null);
    }

    /// <summary>
    /// The ancestors of <paramref name="objectId"/>, nearest first, each fetched only when asked for. Ends at an empty
    /// parent. It has no depth limit of its own: each walk stops it.
    /// </summary>
    private async IAsyncEnumerable<HierarchyNode> AncestorsAsync(string objectId, [EnumeratorCancellation] CancellationToken ct)
    {
        var current = objectId;
        while (true)
        {
            var parent = await gateway.GetHierarchyParentAsync(current, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(parent))
            {
                yield break;
            }

            var xml = await gateway.GetHierarchyAsync(parent, HierarchyScope.Self, ct).ConfigureAwait(false);
            yield return XmlReads.Readable(() => HierarchyNode.Root(xml), "GetHierarchy");
            current = parent;
        }
    }
}

/// <summary>Where a page sits (<see cref="HierarchyWalk.PageLocationAsync"/>): its section, the section groups around it (outermost first) and its notebook.</summary>
internal sealed record PageLocation(HierarchyNode? Section, IReadOnlyList<string> Groups, HierarchyNode? Notebook);
