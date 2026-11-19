namespace OneNoteMcp.Xml;

/// <summary>
/// One live child of a node a reorder reads (story 5.4): a section or section group under a notebook or section group, or a
/// page under a section. <see cref="PageLevel"/> is a page's <c>pageLevel</c> (1 when it has none, it isn't a whole
/// number or it is below 1), and 0 for anything that isn't a page.
/// </summary>
public sealed record HierarchyMember(HierarchyKind Kind, string Id, string Name, int PageLevel);

/// <summary>A page as its section's <c>Pages</c>-scope read lists it: <c>name</c>, hierarchy <c>lastModifiedTime</c>, <c>pageLevel</c> (1 when absent, unreadable or below 1), and how many subpages follow it.</summary>
public sealed record SectionPage(string Name, string? LastModifiedTime, int PageLevel, int Subpages);

/// <summary>
/// The target of a hierarchy delete or change, as a <c>GetHierarchy</c> read shows it: the one reader for
/// <c>delete_hierarchy</c> (story 5.5, AD-7 <c>SoftDelete</c>), <c>permanently_delete</c> (story 5.6, AD-7
/// <c>PermanentDelete</c>) and <c>update_hierarchy</c> (story 5.4, AD-7 <c>HierarchyWrite</c>). It holds the root's kind,
/// <c>ID</c>, <c>name</c>, a notebook's <c>nickname</c> (null when it has none), <c>path</c>, <c>lastModifiedTime</c> (the
/// exact string OneNote wrote, null when absent, AD-17), whether it is a notebook's recycle bin (<c>isRecycleBin</c> on any
/// element, or a section group named <c>OneNote_RecycleBin</c>), <c>isInRecycleBin</c> and <c>isDeletedPages</c> each as
/// written (<see cref="InRecycleBin"/> is either), a section's <c>encrypted</c> marker (not "protected": Core decides that, entry 15) and <c>locked</c>, and its live children
/// in document order: sections and section groups under a notebook or section group, pages under a section. A child that
/// is the recycle bin or in it is never listed. Read at <c>self</c> scope it has no children.
/// </summary>
public sealed record HierarchyTarget(
    HierarchyKind Kind,
    string Id,
    string Name,
    string? Nickname,
    string Path,
    string? LastModifiedTime,
    bool IsRecycleBin,
    bool IsInRecycleBin,
    bool IsDeletedPages,
    bool EncryptedMarker,
    bool Locked,
    IReadOnlyList<HierarchyMember> Children)
{
    /// <summary>Inside the recycle bin: <see cref="IsInRecycleBin"/> or <see cref="IsDeletedPages"/>.</summary>
    public bool InRecycleBin => IsInRecycleBin || IsDeletedPages;

    /// <summary>The name OneNote shows: a notebook's <c>nickname</c> when it has one, else <c>name</c>.</summary>
    public string DisplayName => Kind == HierarchyKind.Notebook && !string.IsNullOrEmpty(Nickname) ? Nickname : Name;

    /// <summary>
    /// The root of <paramref name="hierarchyXml"/> and its live children. Throws <see cref="OneNoteXmlException"/> when the
    /// XML isn't well-formed or has no root.
    /// </summary>
    public static HierarchyTarget Read(string hierarchyXml)
    {
        var root = OneNoteXml.ParseRoot(hierarchyXml);
        var kind = HierarchyElements.KindOf(root);
        var children = new List<HierarchyMember>();
        foreach (var child in root.Elements())
        {
            var childKind = HierarchyElements.KindOf(child);
            var listed = kind switch
            {
                HierarchyKind.Notebook or HierarchyKind.SectionGroup => childKind is HierarchyKind.Section or HierarchyKind.SectionGroup,
                HierarchyKind.Section => childKind == HierarchyKind.Page,
                _ => false,
            };
            if (!listed || HierarchyElements.IsRecycleBin(child) || HierarchyElements.InRecycleBin(child))
            {
                continue;
            }

            children.Add(new HierarchyMember(
                childKind,
                (string?)child.Attribute("ID") ?? string.Empty,
                (string?)child.Attribute("name") ?? string.Empty,
                childKind == HierarchyKind.Page ? HierarchyElements.PageLevel(child) : 0));
        }

        return new HierarchyTarget(
            kind,
            (string?)root.Attribute("ID") ?? string.Empty,
            (string?)root.Attribute("name") ?? string.Empty,
            kind == HierarchyKind.Notebook ? (string?)root.Attribute("nickname") : null,
            (string?)root.Attribute("path") ?? string.Empty,
            (string?)root.Attribute("lastModifiedTime"),
            HierarchyElements.IsRecycleBin(root),
            OneNoteXml.IsTrue((string?)root.Attribute("isInRecycleBin")),
            OneNoteXml.IsTrue((string?)root.Attribute("isDeletedPages")),
            kind == HierarchyKind.Section && EncryptedSections.IsEncrypted(root),
            OneNoteXml.IsTrue((string?)root.Attribute("locked")),
            children);
    }

    /// <summary>
    /// The page <paramref name="pageId"/> among the <c>one:Page</c> elements directly under the root of
    /// <paramref name="sectionPagesXml"/> (its section read at <c>Pages</c> scope), or null when it isn't listed there. Its
    /// subpages are the pages straight after it whose <c>pageLevel</c> is greater than its own, up to the next page at its
    /// level or above (5.5 plan Q2; OneNote keeps a page's subpages directly after it). Throws
    /// <see cref="OneNoteXmlException"/> when the XML isn't well-formed or has no root.
    /// </summary>
    public static SectionPage? Page(string sectionPagesXml, string pageId)
    {
        var root = OneNoteXml.ParseRoot(sectionPagesXml);
        var pages = root.Elements(OneNoteXml.One + "Page").ToList();
        var index = pages.FindIndex(p => string.Equals((string?)p.Attribute("ID"), pageId, StringComparison.Ordinal));
        if (index < 0)
        {
            return null;
        }

        var page = pages[index];
        var level = HierarchyElements.PageLevel(page);
        var subpages = pages.Skip(index + 1).TakeWhile(p => HierarchyElements.PageLevel(p) > level).Count();
        return new SectionPage((string?)page.Attribute("name") ?? string.Empty, (string?)page.Attribute("lastModifiedTime"), level, subpages);
    }

    /// <summary>
    /// The <c>path</c> of every section below the root of <paramref name="hierarchyXml"/> (a section group read at
    /// <c>sections</c> scope), nested ones included, in document order; an empty string for a section with no
    /// <c>path</c>. A rename of the group moves each of them (story 5.4 security review). Throws
    /// <see cref="OneNoteXmlException"/> when the XML isn't well-formed or has no root.
    /// </summary>
    public static IReadOnlyList<string> SectionPathsBelow(string hierarchyXml)
    {
        var root = OneNoteXml.ParseRoot(hierarchyXml);
        return [.. root.Descendants(OneNoteXml.One + "Section").Select(s => (string?)s.Attribute("path") ?? string.Empty)];
    }

    /// <summary>
    /// The <c>ID</c> of every section and section group below the root of <paramref name="hierarchyXml"/> (a section group
    /// read at <c>sections</c> scope), nested ones included: a move may not put a group inside itself (story 3.12). Throws
    /// <see cref="OneNoteXmlException"/> when the XML isn't well-formed or has no root.
    /// </summary>
    public static IReadOnlyList<string> ContainerIdsBelow(string hierarchyXml)
    {
        var root = OneNoteXml.ParseRoot(hierarchyXml);
        return [.. root.Descendants()
            .Where(e => e.Name == OneNoteXml.One + "Section" || e.Name == OneNoteXml.One + "SectionGroup")
            .Select(e => (string?)e.Attribute("ID") ?? string.Empty)
            .Where(id => id.Length > 0)];
    }
}
