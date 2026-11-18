using System.Globalization;
using System.Runtime.Versioning;
using OneNoteMcp.Core.Access;
using OneNoteMcp.Xml;

namespace OneNoteMcp.Core.Journal;

/// <summary>What a soft-delete entry recorded (story 3.5): a page, a section, a section group, or something else.</summary>
public enum SoftDeleteKind
{
    Other,
    Page,
    Section,
    SectionGroup,
}

/// <summary>
/// How a recycled item matched its soft-delete entries (story 3.5; AD-8): the one <see cref="Entry"/>, and whether it was
/// matched <see cref="ByName"/>; or, when several match (Q1 a), every one of them in <see cref="Candidates"/> and no entry;
/// or nothing at all.
/// </summary>
public sealed record SoftDeleteMatch(JournalEntry? Entry, bool ByName, IReadOnlyList<JournalEntry> Candidates)
{
    /// <summary>No entry matched.</summary>
    public static SoftDeleteMatch None { get; } = new(null, false, []);
}

/// <summary>
/// <c>restore_from_recycle_bin</c>'s journal side (story 3.5; AD-8): the soft-delete entries of one notebook, read from
/// their plain metadata only (<see cref="JournalStore.ReadMetadata"/>; no protected entry's DPAPI blob is ever opened),
/// and the matching of a recycled page or section to them. Everything but <see cref="Load"/> is pure over the entry list.
/// <list type="bullet">
/// <item>Only entries whose <c>operation</c> is <c>delete_hierarchy_object</c> and whose <c>notebookPath</c> is the item's
/// notebook's key count (<see cref="Of"/>).</item>
/// <item>A page matches by <c>recycleBinId</c>, else by its name against the entry's <c>title</c>; an entry whose
/// <c>recycleBinId</c> is another page still in the bin belongs to that page and is left out.</item>
/// <item>A section matches by its ID, else, after a reopen gave it a new ID, by name against a plain entry's <c>title</c>
/// (Q5), or for a protected section by the file name in the entry's <c>SectionKey</c>; an entry whose <c>objectId</c> is
/// another section still in the bin is left out.</item>
/// <item>Several matches are returned as candidates; the caller refuses them (Q1 a).</item>
/// </list>
/// </summary>
[SupportedOSPlatform("windows")]
public static class SoftDeleteEntries
{
    /// <summary>The soft delete's tool name, as a journal entry's <c>operation</c> holds it.</summary>
    public const string Operation = UseCases.SoftDeleteUseCase.OperationName;

    private static readonly char[] Separators = ['\\', '/'];

    /// <summary>
    /// Every journal entry in <paramref name="folder"/>, from its metadata only, in file-name order. An entry that can't be
    /// read (a damaged file, a newer <c>schemaVersion</c>, a file pruned while listing) is skipped. A missing folder has no
    /// entries. A folder that can't be listed is <see cref="ErrorCode.JournalFailed"/>, with no path in the message.
    /// </summary>
    public static IReadOnlyList<JournalEntry> Load(string folder)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        IReadOnlyList<string> files;
        try
        {
            if (!Directory.Exists(folder))
            {
                return [];
            }

            files = [.. Directory.EnumerateFiles(folder, "*" + JournalFiles.Extension, SearchOption.TopDirectoryOnly)
                .Where(path => JournalFiles.IsEntry(Path.GetFileName(path)))
                .Order(StringComparer.Ordinal)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new OneNoteException(ErrorCode.JournalFailed, $"The journal folder couldn't be listed ({ex.GetType().Name}).", innerException: ex);
        }

        var entries = new List<JournalEntry>();
        foreach (var file in files)
        {
            try
            {
                entries.Add(JournalStore.ReadMetadata(file));
            }
            catch (OneNoteException)
            {
                // Unreadable, newer or gone: it can't be matched, and the restore then works as without it.
            }
        }

        return entries;
    }

    /// <summary>What <paramref name="entry"/> recorded: a page, a section (a plain one by its XML's root, a protected one by its <c>SectionKey</c>), a section group, or something else.</summary>
    public static SoftDeleteKind KindOf(JournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.SnapshotKind == JournalEntry.PageKind)
        {
            return SoftDeleteKind.Page;
        }

        if (entry.SnapshotKind != JournalEntry.HierarchyKind)
        {
            return SoftDeleteKind.Other;
        }

        if (entry.SectionKey is not null)
        {
            // Only a protected section's own snapshot carries a SectionKey (HierarchySnapshot).
            return SoftDeleteKind.Section;
        }

        return RootOf(entry)?.Kind switch
        {
            HierarchyKind.Section => SoftDeleteKind.Section,
            HierarchyKind.SectionGroup => SoftDeleteKind.SectionGroup,
            _ => SoftDeleteKind.Other,
        };
    }

    /// <summary>The section path a plain section's entry recorded (its XML root's <c>path</c>), or null when it has none or isn't readable.</summary>
    public static string? OldPath(JournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return RootOf(entry) is { Kind: HierarchyKind.Section, Path.Length: > 0 } root ? root.Path : null;
    }

    /// <summary>The soft-delete entries of the notebook keyed <paramref name="notebookKey"/> (AD-6 <c>NotebookKey</c>), in the order given.</summary>
    public static IReadOnlyList<JournalEntry> Of(IEnumerable<JournalEntry> entries, string notebookKey)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return [.. entries.Where(e =>
            string.Equals(e.Operation, Operation, StringComparison.Ordinal)
            && e.NotebookPath.Length > 0
            && GrantKeys.Comparer.Equals(e.NotebookPath, notebookKey ?? string.Empty))];
    }

    /// <summary>
    /// The entry <paramref name="entryId"/> among <paramref name="notebookEntries"/> (one notebook's soft-delete entries),
    /// when it records a <paramref name="kind"/>; else null.
    /// </summary>
    public static JournalEntry? ById(IReadOnlyList<JournalEntry> notebookEntries, Guid entryId, SoftDeleteKind kind)
    {
        ArgumentNullException.ThrowIfNull(notebookEntries);
        return notebookEntries.FirstOrDefault(e => e.EntryId == entryId && KindOf(e) == kind);
    }

    /// <summary>
    /// The recycled page <paramref name="recycledId"/>, named <paramref name="name"/>, against one notebook's soft-delete
    /// entries: by <c>recycleBinId</c>, else by name (ordinal, as OneNote stores it). <paramref name="binnedPageIds"/> are
    /// the IDs of every page now in Deleted Pages: an entry whose <c>recycleBinId</c> is another of them is left out.
    /// </summary>
    public static SoftDeleteMatch MatchPage(IReadOnlyList<JournalEntry> notebookEntries, string recycledId, string name, IReadOnlyCollection<string> binnedPageIds)
    {
        var byId = PagesById(notebookEntries, recycledId);
        return byId.Count > 0 ? Result(byId, byName: false) : Result(PagesByName(notebookEntries, recycledId, name, binnedPageIds), byName: true);
    }

    /// <summary>
    /// Every entry the recycled page matches, by <c>recycleBinId</c> or by name (<see cref="MatchPage"/>'s rules, both
    /// together): the only entries an <c>entryId</c> may pick for it (story 3.5 review fix 1).
    /// </summary>
    public static IReadOnlyList<JournalEntry> PageMatches(IReadOnlyList<JournalEntry> notebookEntries, string recycledId, string name, IReadOnlyCollection<string> binnedPageIds) =>
        [.. PagesById(notebookEntries, recycledId).Union(PagesByName(notebookEntries, recycledId, name, binnedPageIds))];

    /// <summary>
    /// The recycled section <paramref name="sectionId"/> against one notebook's soft-delete entries: by its ID, else by name.
    /// Several ID matches (deleted, restored and deleted again) give the newest by <c>takenAtUtc</c>; name matches never do
    /// (review fix 2). A plain section (<paramref name="isProtected"/> false) matches a plain entry's <c>title</c> by
    /// <paramref name="name"/>, ignoring case (Q5); a protected one matches a protected entry whose <c>SectionKey</c> ends in
    /// the file name of <paramref name="binPath"/>, its path in the recycle bin, ignoring case. An entry whose
    /// <c>objectId</c> is another of <paramref name="binnedSectionIds"/> (the sections now in the bin) is left out.
    /// </summary>
    public static SoftDeleteMatch MatchSection(
        IReadOnlyList<JournalEntry> notebookEntries,
        string sectionId,
        string name,
        string binPath,
        bool isProtected,
        IReadOnlyCollection<string> binnedSectionIds)
    {
        var byId = SectionsById(notebookEntries, sectionId);
        if (byId.Count > 0)
        {
            return new SoftDeleteMatch(byId.MaxBy(e => e.TakenAtUtc), false, []);
        }

        return Result(SectionsByName(notebookEntries, sectionId, name, binPath, isProtected, binnedSectionIds), byName: true);
    }

    /// <summary>
    /// Every entry the recycled section matches, by its ID or by name (<see cref="MatchSection"/>'s rules, both together):
    /// the only entries an <c>entryId</c> may pick for it (story 3.5 review fix 1).
    /// </summary>
    public static IReadOnlyList<JournalEntry> SectionMatches(
        IReadOnlyList<JournalEntry> notebookEntries,
        string sectionId,
        string name,
        string binPath,
        bool isProtected,
        IReadOnlyCollection<string> binnedSectionIds) =>
        [.. SectionsById(notebookEntries, sectionId).Union(SectionsByName(notebookEntries, sectionId, name, binPath, isProtected, binnedSectionIds))];

    private static List<JournalEntry> PagesById(IReadOnlyList<JournalEntry> notebookEntries, string recycledId)
    {
        ArgumentNullException.ThrowIfNull(notebookEntries);
        return [.. notebookEntries.Where(e => KindOf(e) == SoftDeleteKind.Page && string.Equals(e.RecycleBinId, recycledId, StringComparison.Ordinal))];
    }

    private static List<JournalEntry> PagesByName(IReadOnlyList<JournalEntry> notebookEntries, string recycledId, string name, IReadOnlyCollection<string> binnedPageIds)
    {
        ArgumentNullException.ThrowIfNull(notebookEntries);
        ArgumentNullException.ThrowIfNull(binnedPageIds);
        var others = binnedPageIds.Where(id => !string.Equals(id, recycledId, StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        return [.. notebookEntries.Where(e =>
            KindOf(e) == SoftDeleteKind.Page
            && e.Title is not null
            && string.Equals(e.Title, name, StringComparison.Ordinal)
            && !(e.RecycleBinId is { } owner && others.Contains(owner)))];
    }

    private static List<JournalEntry> SectionsById(IReadOnlyList<JournalEntry> notebookEntries, string sectionId)
    {
        ArgumentNullException.ThrowIfNull(notebookEntries);
        return [.. notebookEntries.Where(e => KindOf(e) == SoftDeleteKind.Section && string.Equals(e.ObjectId, sectionId, StringComparison.Ordinal))];
    }

    private static List<JournalEntry> SectionsByName(
        IReadOnlyList<JournalEntry> notebookEntries,
        string sectionId,
        string name,
        string binPath,
        bool isProtected,
        IReadOnlyCollection<string> binnedSectionIds)
    {
        ArgumentNullException.ThrowIfNull(notebookEntries);
        ArgumentNullException.ThrowIfNull(binnedSectionIds);
        var others = binnedSectionIds.Where(id => !string.Equals(id, sectionId, StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        var free = notebookEntries.Where(e => KindOf(e) == SoftDeleteKind.Section && !others.Contains(e.ObjectId));
        var leaf = Leaf(binPath);
        return isProtected
            ? [.. free.Where(e => e.SectionKey is { } key && leaf is not null && string.Equals(Leaf(key), leaf, StringComparison.OrdinalIgnoreCase))]
            : [.. free.Where(e => e.SectionKey is null && e.Title is not null && string.Equals(e.Title, name, StringComparison.OrdinalIgnoreCase))];
    }

    /// <summary>
    /// The folder a protected section's entry says it was in: its <c>SectionKey</c> without the file name, trimmed as a key
    /// is (AD-21). Null when the key has no folder part.
    /// </summary>
    public static string? FolderOf(string sectionKey)
    {
        var trimmed = (sectionKey ?? string.Empty).TrimEnd(Separators);
        var cut = trimmed.LastIndexOfAny(Separators);
        return cut <= 0 ? null : GrantKeys.SectionKey(trimmed[..cut]);
    }

    /// <summary>One line per candidate for a refusal (Q1 a): its entry ID, deletion time (UTC) and original parent ID. Never a name (AD-9).</summary>
    public static string Describe(IEnumerable<JournalEntry> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return string.Join("; ", candidates.Select(c => string.Create(
            CultureInfo.InvariantCulture,
            $"entry {c.EntryId:D}, deleted {c.TakenAtUtc.ToUniversalTime():yyyy-MM-dd'T'HH:mm:ss'Z'}, from parent {(c.ParentId.Length > 0 ? c.ParentId : "(none recorded)")}")));
    }

    private static SoftDeleteMatch Result(List<JournalEntry> matched, bool byName) => matched.Count switch
    {
        0 => SoftDeleteMatch.None,
        1 => new SoftDeleteMatch(matched[0], byName, []),
        _ => new SoftDeleteMatch(null, byName, matched),
    };

    private static string? Leaf(string? path)
    {
        var trimmed = (path ?? string.Empty).TrimEnd(Separators);
        return trimmed.Length == 0 ? null : trimmed[(trimmed.LastIndexOfAny(Separators) + 1)..];
    }

    private static HierarchyTarget? RootOf(JournalEntry entry)
    {
        if (entry.Xml is not { Length: > 0 } xml)
        {
            return null;
        }

        try
        {
            return HierarchyTarget.Read(xml);
        }
        catch (OneNoteXmlException)
        {
            return null;
        }
    }
}
