using System.Xml.Linq;

namespace OneNoteMcp.Xml;

/// <summary>
/// One password-protected section in hierarchy XML (AD-21): its <c>ID</c>, <c>name</c> and <c>path</c>, unchanged
/// (AD-4). <see cref="Location"/> names its notebook (<c>nickname</c>, else <c>name</c>) and the section groups around it,
/// outermost first, as far as the XML shows them; it is empty when the section is the XML's root. <see cref="NotebookPath"/>
/// is the enclosing <c>one:Notebook@path</c>, or empty when the XML doesn't show it. <see cref="NotebookName"/> is that
/// notebook's displayed name, or null when the XML doesn't show the notebook; when it isn't null, it is
/// <see cref="Location"/>'s first entry. Only the CLI and the grant dialog show <see cref="Name"/> and
/// <see cref="Location"/>, to the user; tool messages carry the ID only (AD-9). <see cref="Marked"/> is false for a section
/// that is protected only by its path (entry 15).
/// </summary>
public sealed record EncryptedSection(string Id, string Name, string Path, bool Locked, IReadOnlyList<string> Location, string NotebookPath = "", string? NotebookName = null, bool Marked = true)
{
    /// <summary>The section groups around the section, outermost first: <see cref="Location"/> without the notebook.</summary>
    public IReadOnlyList<string> SectionGroups => NotebookName is null ? Location : [.. Location.Skip(1)];
}

/// <summary>
/// Finds the protected sections in hierarchy XML (AD-21), as <see cref="LockedSections"/> does for <c>locked</c>. OneNote
/// marks a password-protected one with <c>encrypted="true"</c> on its <c>one:Section</c>, whether or not it is currently
/// locked; AD-21's path rule (entry 15) also protects a section whose <c>path</c>, or whose notebook's <c>path</c>, the
/// caller's <c>underRoot</c> test accepts. That test is required, so no caller can forget the rule; Core supplies it.
/// </summary>
public static class EncryptedSections
{
    /// <summary>
    /// Every <c>one:Section</c> in <paramref name="hierarchyXml"/>, the root included, that is protected: its
    /// <c>encrypted</c> attribute is true, or <paramref name="underRoot"/> accepts its <c>path</c> or its notebook's
    /// <c>path</c> (when the XML shows the notebook), in document order. Sections under <c>one:OpenSections</c> (files
    /// opened by hand) have no notebook and go by their own path. Throws <see cref="OneNoteXmlException"/> when the XML
    /// isn't well-formed.
    /// </summary>
    public static IReadOnlyList<EncryptedSection> Find(string hierarchyXml, Func<string, bool> underRoot)
    {
        ArgumentNullException.ThrowIfNull(underRoot);
        return [.. OneNoteXml.Parse(hierarchyXml)
            .Descendants(OneNoteXml.One + "Section")
            .Select(s => (Section: s, Marked: IsEncrypted(s)))
            .Where(s => s.Marked || IsProtectedByPath(s.Section, underRoot))
            .Select(s => From(s.Section) with { Marked = s.Marked })];
    }

    /// <summary>True when <paramref name="section"/> is protected: marked, or by its path or its notebook's (AD-21, entry 15).</summary>
    internal static bool IsProtected(XElement section, Func<string, bool> underRoot) =>
        IsEncrypted(section) || IsProtectedByPath(section, underRoot);

    private static bool IsProtectedByPath(XElement section, Func<string, bool> underRoot) =>
        underRoot((string?)section.Attribute("path") ?? string.Empty)
        || (section.Ancestors(OneNoteXml.One + "Notebook").FirstOrDefault() is { } notebook && underRoot((string?)notebook.Attribute("path") ?? string.Empty));

    /// <summary>True when <paramref name="section"/> carries <c>encrypted="true"</c> (or <c>"1"</c>).</summary>
    internal static bool IsEncrypted(XElement section) => OneNoteXml.IsTrue((string?)section.Attribute("encrypted"));

    internal static EncryptedSection From(XElement section)
    {
        var notebook = section.Ancestors(OneNoteXml.One + "Notebook").FirstOrDefault();
        return new(
            (string?)section.Attribute("ID") ?? string.Empty,
            (string?)section.Attribute("name") ?? string.Empty,
            (string?)section.Attribute("path") ?? string.Empty,
            OneNoteXml.IsTrue((string?)section.Attribute("locked")),
            [.. section.Ancestors()
                .Where(a => a.Name == OneNoteXml.One + "Notebook" || a.Name == OneNoteXml.One + "SectionGroup")
                .Reverse()
                .Select(DisplayName)],
            (string?)notebook?.Attribute("path") ?? string.Empty,
            notebook is null ? null : DisplayName(notebook));
    }

    /// <summary>A notebook's displayed name: <c>nickname</c>, else <c>name</c> (AD-6). Any other element's <c>name</c>.</summary>
    internal static string DisplayName(XElement element)
    {
        var nickname = (string?)element.Attribute("nickname");
        return element.Name == OneNoteXml.One + "Notebook" && !string.IsNullOrEmpty(nickname)
            ? nickname
            : (string?)element.Attribute("name") ?? string.Empty;
    }
}
