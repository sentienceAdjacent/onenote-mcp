namespace OneNoteMcp.Xml;

/// <summary>
/// Finds a page's <c>one:XPSFile</c> CallbackIDs. A File Printout keeps its printout document as a page-level
/// <c>one:XPSFile</c>, and <c>GetBinaryPageContent</c> on its callbackID fails with <c>0x80042014</c>
/// hrObjectDoesNotExist (spike 1.15, quirk 2), so story 1.21 reports it as an unavailable binary instead.
/// </summary>
public static class XpsFiles
{
    /// <summary>
    /// Whether <paramref name="callbackId"/> is the <c>one:CallbackID@callbackID</c> of a <c>one:XPSFile</c> child of the
    /// <c>one:Page</c> root of <paramref name="pageXml"/> (the 2013 schema puts <c>XPSFile</c> only there). Compared
    /// ordinally, as IDs are opaque. Throws <see cref="OneNoteXmlException"/> when the XML isn't a well-formed page.
    /// </summary>
    public static bool IsXpsFileCallbackId(string pageXml, string callbackId)
    {
        ArgumentNullException.ThrowIfNull(callbackId);
        var page = OneNoteXml.RequireRoot(OneNoteXml.Parse(pageXml), "Page");
        return page.Elements(OneNoteXml.One + "XPSFile")
            .Select(x => (string?)x.Element(OneNoteXml.One + "CallbackID")?.Attribute("callbackID"))
            .Any(id => string.Equals(id, callbackId, StringComparison.Ordinal));
    }
}
