using System.Xml.Linq;

namespace OneNoteMcp.Xml;

/// <summary>OneNote 2013 XML schema constants (Consistency Conventions).</summary>
public static class OneNoteXml
{
    /// <summary>The 2013 schema namespace, always declared in XML the server handles (Consistency Conventions).</summary>
    public const string Namespace2013 = "http://schemas.microsoft.com/office/onenote/2013/onenote";

    /// <summary><see cref="Namespace2013"/> as an <see cref="XNamespace"/>, for building element names (<c>One + "Page"</c>).</summary>
    public static readonly XNamespace One = Namespace2013;

    /// <summary>
    /// Parses OneNote XML. Insignificant whitespace is dropped, so pretty-printing never changes a hash; CDATA is kept.
    /// Throws <see cref="OneNoteXmlException"/> when the text isn't well-formed.
    /// </summary>
    internal static XDocument Parse(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        try
        {
            return XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException ex)
        {
            // The parser's message can quote content; keep only the position (AD-13).
            throw new OneNoteXmlException($"The XML is not well-formed (line {ex.LineNumber}, position {ex.LinePosition}).", ex);
        }
    }

    /// <summary>Throws <see cref="OneNoteXmlException"/> unless the root element is <paramref name="localName"/> in the 2013 namespace.</summary>
    internal static XElement RequireRoot(XDocument document, string localName)
    {
        var root = document.Root ?? throw new OneNoteXmlException("The XML has no root element.");
        if (root.Name != One + localName)
        {
            throw new OneNoteXmlException(
                root.Name.Namespace == One
                    ? $"Expected a one:{localName} root element, found one:{root.Name.LocalName}."
                    : $"Expected a one:{localName} root element in the 2013 namespace ({Namespace2013}).");
        }

        return root;
    }
}
