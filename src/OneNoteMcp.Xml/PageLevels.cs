namespace OneNoteMcp.Xml;

/// <summary>Why <see cref="PageLevels"/> refused a level change or a subpage placement; <see cref="None"/> when it didn't.</summary>
public enum PageLevelProblem
{
    None,

    /// <summary>The level asked for is outside 1 to <see cref="PageLevels.Deepest"/>.</summary>
    LevelOutOfRange,

    /// <summary>The page isn't a live page of the section.</summary>
    PageNotListed,

    /// <summary>The section's first page would get a level other than 1.</summary>
    FirstPage,

    /// <summary>The page would sit more than one level below the page before it.</summary>
    GapBefore,

    /// <summary>One of the page's subpages would leave 1 to <see cref="PageLevels.Deepest"/> (3.14 Q2).</summary>
    SubpageOutOfRange,

    /// <summary>The first page after the moved block would sit more than one level below the block's new last page.</summary>
    GapAfter,

    /// <summary>The parent page isn't a live page of the section.</summary>
    ParentNotListed,

    /// <summary>The parent page is already at level <see cref="PageLevels.Deepest"/>, so it can't take a subpage.</summary>
    ParentAtDeepest,

    /// <summary>The new page isn't listed in the section.</summary>
    NewPageNotListed,
}

/// <summary>
/// What <see cref="PageLevels"/> decided. <see cref="Pages"/> is the section's pages in the order to send, each with the
/// level to send (the input unchanged when refused); <see cref="Moved"/> the pages whose level or place changes (the page
/// and its block for a level change, the new page for a placement; empty when unchanged or refused); <see cref="Index"/>
/// the 0-based place of the first of them; and <see cref="Offender"/> the page a refusal is about.
/// </summary>
public sealed record PageLevelResult(
    PageLevelProblem Problem,
    IReadOnlyList<HierarchyMember> Pages,
    IReadOnlyList<string> Moved,
    int Index,
    string? Offender)
{
    /// <summary>Nothing to change: no problem and no page moved.</summary>
    public bool Unchanged => Problem == PageLevelProblem.None && Moved.Count == 0;
}

/// <summary>
/// The subpage rules of story 3.14 (<c>set_page_level</c> and <c>create_page</c> with <c>parentPageId</c>), pure, over a
/// section's live pages as <see cref="HierarchyTarget.Read"/> lists them. A page's block is the pages straight after it at a
/// deeper level, as <see cref="HierarchyTarget.Page"/> counts its subpages. A level change moves the block by the same
/// amount (3.14 Q2); every other page keeps its level as read (5.4 L6). Levels run 1 to <see cref="Deepest"/>, the 2013
/// XSD's <c>pageLevel</c> range.
/// </summary>
public static class PageLevels
{
    /// <summary>The deepest <c>pageLevel</c> the 2013 XSD allows (<c>OneNoteApplication_2013.xsd</c>, l.143–150).</summary>
    public const int Deepest = 3;

    /// <summary>
    /// <paramref name="pageId"/> to the absolute <paramref name="level"/> (3.14 Q3), with its block moved by the same
    /// amount. Refused, in this order: a level outside 1 to 3, a page that isn't listed, a first page whose new level isn't
    /// 1, a new level more than one below the page before it, a subpage that would leave 1 to 3, and a first page after the
    /// block more than one level below the block's new last page.
    /// </summary>
    public static PageLevelResult Change(IReadOnlyList<HierarchyMember> pages, string pageId, int level)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(pageId);
        if (level is < 1 or > Deepest)
        {
            return Refused(PageLevelProblem.LevelOutOfRange, pages, pageId);
        }

        var index = IndexOf(pages, pageId);
        if (index < 0)
        {
            return Refused(PageLevelProblem.PageNotListed, pages, pageId);
        }

        var current = pages[index].PageLevel;
        if (level == current)
        {
            return new PageLevelResult(PageLevelProblem.None, pages, [], index, null);
        }

        if (index == 0 && level != 1)
        {
            return Refused(PageLevelProblem.FirstPage, pages, pageId);
        }

        if (index > 0 && level > pages[index - 1].PageLevel + 1)
        {
            return Refused(PageLevelProblem.GapBefore, pages, pageId);
        }

        var block = BlockSize(pages, index);
        var delta = level - current;
        for (var i = index + 1; i <= index + block; i++)
        {
            if (pages[i].PageLevel + delta is < 1 or > Deepest)
            {
                return Refused(PageLevelProblem.SubpageOutOfRange, pages, pages[i].Id);
            }
        }

        var after = index + block + 1;
        var lastLevel = pages[index + block].PageLevel + delta;
        if (after < pages.Count && pages[after].PageLevel > lastLevel + 1)
        {
            return Refused(PageLevelProblem.GapAfter, pages, pages[after].Id);
        }

        var sent = pages.Select((p, i) => i >= index && i <= index + block ? p with { PageLevel = p.PageLevel + delta } : p).ToList();
        var moved = pages.Skip(index).Take(block + 1).Select(p => p.Id).ToList();
        return new PageLevelResult(PageLevelProblem.None, sent, moved, index, null);
    }

    /// <summary>
    /// Why <paramref name="parentPageId"/> can't take a subpage in <paramref name="pages"/> (not listed, or at level 3), or
    /// <see cref="PageLevelProblem.None"/>.
    /// </summary>
    public static PageLevelProblem ParentProblem(IReadOnlyList<HierarchyMember> pages, string parentPageId)
    {
        ArgumentNullException.ThrowIfNull(pages);
        var index = IndexOf(pages, parentPageId);
        return index < 0 ? PageLevelProblem.ParentNotListed
            : pages[index].PageLevel >= Deepest ? PageLevelProblem.ParentAtDeepest
            : PageLevelProblem.None;
    }

    /// <summary>
    /// <paramref name="newPageId"/> (where <c>CreateNewPage</c> put it, last at level 1) moved to just after
    /// <paramref name="parentPageId"/>'s block, at the parent's level plus one. Every other page keeps its place and level.
    /// Refused: a parent that isn't listed or is the new page itself, a parent at level 3, and a new page that isn't listed.
    /// </summary>
    public static PageLevelResult PlaceSubpage(IReadOnlyList<HierarchyMember> pages, string parentPageId, string newPageId)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(parentPageId);
        ArgumentNullException.ThrowIfNull(newPageId);
        if (string.Equals(parentPageId, newPageId, StringComparison.Ordinal))
        {
            return Refused(PageLevelProblem.ParentNotListed, pages, parentPageId);
        }

        if (ParentProblem(pages, parentPageId) is not PageLevelProblem.None and var problem)
        {
            return Refused(problem, pages, parentPageId);
        }

        var newIndex = IndexOf(pages, newPageId);
        if (newIndex < 0)
        {
            return Refused(PageLevelProblem.NewPageNotListed, pages, newPageId);
        }

        var rest = pages.Where((_, i) => i != newIndex).ToList();
        var parent = IndexOf(rest, parentPageId);
        var at = parent + BlockSize(rest, parent) + 1;
        rest.Insert(at, pages[newIndex] with { PageLevel = rest[parent].PageLevel + 1 });
        return new PageLevelResult(PageLevelProblem.None, rest, [newPageId], at, null);
    }

    /// <summary>How many pages straight after <paramref name="index"/> sit deeper than it: its subpages.</summary>
    private static int BlockSize(IReadOnlyList<HierarchyMember> pages, int index) =>
        pages.Skip(index + 1).TakeWhile(p => p.PageLevel > pages[index].PageLevel).Count();

    private static int IndexOf(IReadOnlyList<HierarchyMember> pages, string? id)
    {
        for (var i = 0; i < pages.Count; i++)
        {
            if (string.Equals(pages[i].Id, id, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static PageLevelResult Refused(PageLevelProblem problem, IReadOnlyList<HierarchyMember> pages, string offender) =>
        new(problem, pages, [], -1, offender);
}
