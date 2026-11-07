using System.Xml.Linq;

namespace OneNoteMcp.Xml;

/// <summary>
/// One password-protected section in hierarchy XML (AD-21): its <c>ID</c>, <c>name</c> and <c>path</c>, unchanged
/// (AD-4). <see cref="Location"/> names its notebook (<c>nickname</c>, else <c>name</c>) and the section groups around it,
/// outermost first, as far as the XML shows them; it is empty when the section is the XML's root. <see cref="NotebookPath"/>
/// is the enclosing <c>one:Notebook@path</c>, or empty when the XML doesn't show it. Only the CLI and the
/// grant dialog show <see cref="Name"/> and <see cref="Location"/>, to the user; tool messages carry the ID only (AD-9).
/// </summary>
public sealed record EncryptedSection(string Id, string Name, string Path, bool Locked, IReadOnlyList<string> Location, string NotebookPath = "");

/// <summary>
/// Finds the password-protected sections in hierarchy XML (AD-21), as <see cref="LockedSections"/> does for
/// <c>locked</c>. OneNote marks one with <c>encrypted="true"</c> on its <c>one:Section</c>, whether or not it is
/// currently locked. The one function that decides what is protected.
/// </summary>
public static class EncryptedSections
{
    /// <summary>
    /// Every <c>one:Section</c> in <paramref name="hierarchyXml"/>, the root included, whose <c>encrypted</c> attribute is
    /// true, in document order. Throws <see cref="OneNoteXmlException"/> when the XML isn't well-formed.
    /// </summary>
    public static IReadOnlyList<EncryptedSection> Find(string hierarchyXml) =>
        [.. OneNoteXml.Parse(hierarchyXml)
            .Descendants(OneNoteXml.One + "Section")
            .Where(IsEncrypted)
            .Select(From)];

    /// <summary>True when <paramref name="section"/> carries <c>encrypted="true"</c> (or <c>"1"</c>).</summary>
    internal static bool IsEncrypted(XElement section) => IsTrue((string?)section.Attribute("encrypted"));

    internal static EncryptedSection From(XElement section) => new(
        (string?)section.Attribute("ID") ?? string.Empty,
        (string?)section.Attribute("name") ?? string.Empty,
        (string?)section.Attribute("path") ?? string.Empty,
        IsTrue((string?)section.Attribute("locked")),
        [.. section.Ancestors()
            .Where(a => a.Name == OneNoteXml.One + "Notebook" || a.Name == OneNoteXml.One + "SectionGroup")
            .Reverse()
            .Select(DisplayName)],
        (string?)section.Ancestors(OneNoteXml.One + "Notebook").FirstOrDefault()?.Attribute("path") ?? string.Empty);

    /// <summary>A notebook's displayed name: <c>nickname</c>, else <c>name</c> (AD-6). Any other element's <c>name</c>.</summary>
    internal static string DisplayName(XElement element)
    {
        var nickname = (string?)element.Attribute("nickname");
        return element.Name == OneNoteXml.One + "Notebook" && !string.IsNullOrEmpty(nickname)
            ? nickname
            : (string?)element.Attribute("name") ?? string.Empty;
    }

    // xs:boolean allows "true" or "1".
    internal static bool IsTrue(string? value) => value is "true" or "1";
}
