namespace OneNoteMcp.Xml;

/// <summary>
/// XML that the Xml model can't use: not well-formed, or the wrong root or namespace. Xml can't reference Core
/// (Core references Xml), so it throws its own type and callers in Core map it to <c>ErrorCode.InvalidXml</c> (AD-9).
/// Messages never contain page content (AD-13).
/// </summary>
public sealed class OneNoteXmlException : Exception
{
    public OneNoteXmlException(string message)
        : base(message)
    {
    }

    public OneNoteXmlException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public OneNoteXmlException()
    {
    }
}
