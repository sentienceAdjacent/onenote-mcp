using System.Globalization;
using System.Xml.Linq;

namespace OneNoteMcp.Xml;

/// <summary>
/// What every hierarchy reader asks of one element of <c>GetHierarchy</c> XML: its kind, whether it is a notebook's
/// recycle bin, whether it is inside one, and a page's level. One copy, shared by the <c>XElement</c>-based hierarchy readers (5.8 refactor sweep).
/// </summary>
internal static class HierarchyElements
{
    /// <summary>The kind of <paramref name="element"/>: <see cref="HierarchyKind.Other"/> outside the 2013 namespace or for any other element name.</summary>
    internal static HierarchyKind KindOf(XElement element) =>
        element.Name.Namespace != OneNoteXml.One
            ? HierarchyKind.Other
            : element.Name.LocalName switch
            {
                "Notebook" => HierarchyKind.Notebook,
                "SectionGroup" => HierarchyKind.SectionGroup,
                "Section" => HierarchyKind.Section,
                "Page" => HierarchyKind.Page,
                _ => HierarchyKind.Other,
            };

    /// <summary>A notebook's recycle bin: <c>isRecycleBin</c>, or a section group named <c>OneNote_RecycleBin</c>.</summary>
    internal static bool IsRecycleBin(XElement element) =>
        OneNoteXml.IsTrue((string?)element.Attribute("isRecycleBin"))
        || (KindOf(element) == HierarchyKind.SectionGroup
            && string.Equals((string?)element.Attribute("name"), NotebookSections.RecycleBinName, StringComparison.Ordinal));

    /// <summary>Inside the recycle bin: <c>isInRecycleBin</c> or <c>isDeletedPages</c>.</summary>
    internal static bool InRecycleBin(XElement element) =>
        OneNoteXml.IsTrue((string?)element.Attribute("isInRecycleBin")) || OneNoteXml.IsTrue((string?)element.Attribute("isDeletedPages"));

    /// <summary>
    /// A page's <c>pageLevel</c>, read as an invariant <see cref="NumberStyles.Integer"/> (surrounding white space and a
    /// leading sign allowed): 1 when it is absent, unreadable or below 1. The one parse every hierarchy reader shares
    /// (backlog story "One reader for the target of a delete or change").
    /// </summary>
    internal static int PageLevel(XElement page) =>
        int.TryParse((string?)page.Attribute("pageLevel"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var level) && level >= 1 ? level : 1;
}
