using OneNoteMcp.Xml;

namespace OneNoteMcp.Core;

/// <summary>The one mapping from XML the Xml model can't use to <see cref="ErrorCode.InvalidXml"/> (AD-9).</summary>
internal static class XmlReads
{
    /// <summary>
    /// Runs <paramref name="read"/>. When it throws <see cref="OneNoteXmlException"/>, throws <see cref="ErrorCode.InvalidXml"/>
    /// with the message <c>"{what} returned XML the server can't read: {reason}"</c>, where <paramref name="what"/> names the
    /// OneNote call (and the page, when there is one). The parse exception is never attached as the inner exception:
    /// <see cref="OneNoteXmlException"/> messages carry only positions and element names, never content (AD-13).
    /// </summary>
    internal static T Readable<T>(Func<T> read, string what)
    {
        try
        {
            return read();
        }
        catch (OneNoteXmlException ex)
        {
            throw new OneNoteException(ErrorCode.InvalidXml, $"{what} returned XML the server can't read: {ex.Message}");
        }
    }
}
