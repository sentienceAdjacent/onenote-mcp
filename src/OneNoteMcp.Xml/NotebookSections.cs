using System.Xml.Linq;

namespace OneNoteMcp.Xml;

/// <summary>
/// One section of a notebook as <see cref="NotebookSections.Find"/> lists it: its <c>ID</c>, <c>name</c> and
/// <c>path</c>, unchanged (AD-4); <see cref="Groups"/>, the <c>name</c>s of the section groups around it inside the
/// notebook, outermost first (empty directly under the notebook); and its <c>encrypted</c>, <c>locked</c> and
/// recycle-bin state (<c>isInRecycleBin</c>, <c>isDeletedPages</c>, or a group around it in the recycle bin).
/// </summary>
public sealed record NotebookSection(string Id, string Name, string Path, IReadOnlyList<string> Groups, bool Encrypted, bool Locked, bool InRecycleBin)
{
    /// <summary>The section's name path inside the notebook, as a journal entry's <c>parentNamePath</c> holds it: groups and name joined by <c>/</c>.</summary>
    public string NamePath => string.Join('/', [.. Groups, Name]);
}

/// <summary>
/// Lists a notebook's sections with their group paths (<c>restore_from_journal</c>, AD-8). The notebook's recycle bin
/// (the <c>OneNote_RecycleBin</c> section group, <c>isRecycleBin</c>) and everything in it is skipped.
/// </summary>
public static class NotebookSections
{
    /// <summary>The name OneNote gives a notebook's recycle-bin section group.</summary>
    public const string RecycleBinName = "OneNote_RecycleBin";

    /// <summary>
    /// Every <c>one:Section</c> in <paramref name="sectionsScopeXml"/> (a <c>GetHierarchy</c> read of a notebook at
    /// <c>Sections</c> scope) outside the recycle bin, in document order. Throws <see cref="OneNoteXmlException"/> when
    /// the XML isn't well-formed.
    /// </summary>
    public static IReadOnlyList<NotebookSection> Find(string sectionsScopeXml)
    {
        var document = OneNoteXml.Parse(sectionsScopeXml);
        var sections = new List<NotebookSection>();
        foreach (var section in document.Descendants(OneNoteXml.One + "Section"))
        {
            var groups = section.Ancestors(OneNoteXml.One + "SectionGroup").Reverse().ToList();
            if (groups.Any(IsRecycleBin))
            {
                continue;
            }

            sections.Add(new NotebookSection(
                (string?)section.Attribute("ID") ?? string.Empty,
                (string?)section.Attribute("name") ?? string.Empty,
                (string?)section.Attribute("path") ?? string.Empty,
                [.. groups.Select(g => (string?)g.Attribute("name") ?? string.Empty)],
                EncryptedSections.IsEncrypted(section),
                OneNoteXml.IsTrue((string?)section.Attribute("locked")),
                InRecycleBin(section) || groups.Any(InRecycleBin)));
        }

        return sections;
    }

    private static bool IsRecycleBin(XElement group) =>
        OneNoteXml.IsTrue((string?)group.Attribute("isRecycleBin"))
        || string.Equals((string?)group.Attribute("name"), RecycleBinName, StringComparison.Ordinal);

    private static bool InRecycleBin(XElement element) =>
        OneNoteXml.IsTrue((string?)element.Attribute("isInRecycleBin")) || OneNoteXml.IsTrue((string?)element.Attribute("isDeletedPages"));
}
