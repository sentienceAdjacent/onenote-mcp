namespace OneNoteMcp.Xml;

/// <summary>
/// Finds the password-locked sections in hierarchy XML, for the <c>sectionLocked</c> read warning (AD-4). OneNote marks a
/// locked section with <c>locked="true"</c> on its <c>one:Section</c> and leaves its pages out until it is unlocked.
/// </summary>
public static class LockedSections
{
    /// <summary>
    /// The <c>ID</c> of every <c>one:Section</c> in <paramref name="hierarchyXml"/> whose <c>locked</c> attribute is true,
    /// in document order, unchanged (AD-4). Throws <see cref="OneNoteXmlException"/> when the XML isn't well-formed.
    /// </summary>
    public static IReadOnlyList<string> Find(string hierarchyXml) =>
        [.. OneNoteXml.Parse(hierarchyXml)
            .Descendants(OneNoteXml.One + "Section")
            .Where(s => IsTrue((string?)s.Attribute("locked")))
            .Select(s => (string?)s.Attribute("ID") ?? string.Empty)];

    // xs:boolean allows "true" or "1".
    private static bool IsTrue(string? value) => value is "true" or "1";
}
