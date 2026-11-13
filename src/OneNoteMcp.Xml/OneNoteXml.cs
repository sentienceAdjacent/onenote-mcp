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
            return Load(xml, LoadOptions.None);
        }
        catch (System.Xml.XmlException ex)
        {
            // The parser's message can quote content; keep only the position (AD-13).
            throw new OneNoteXmlException($"The XML is not well-formed (line {ex.LineNumber}, position {ex.LinePosition}).", ex);
        }
    }

    /// <summary>
    /// Loads <paramref name="xml"/> the way <see cref="XDocument.Parse(string, LoadOptions)"/> does, but with DTD
    /// processing prohibited and no resolver, so a DOCTYPE throws and no entity is ever expanded (AD-16, Phill 2026-11-12,
    /// story 2.9 security review). Throws <see cref="System.Xml.XmlException"/> when the text isn't well-formed.
    /// </summary>
    internal static XDocument Load(string xml, LoadOptions options)
    {
        ArgumentNullException.ThrowIfNull(xml);
        var settings = new System.Xml.XmlReaderSettings
        {
            DtdProcessing = System.Xml.DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreWhitespace = (options & LoadOptions.PreserveWhitespace) == 0,
        };
        using var reader = System.Xml.XmlReader.Create(new StringReader(xml), settings);
        return XDocument.Load(reader, options);
    }

    /// <summary>
    /// True when <paramref name="xml"/> has a DOCTYPE: a <c>&lt;!DOCTYPE</c> in its prolog, after any byte-order mark,
    /// whitespace, XML declaration, processing instructions and comments. A DOCTYPE can appear nowhere else, so this
    /// needs no parser (AD-16: sent XML with a DOCTYPE is <c>InvalidXml</c> before any other check).
    /// </summary>
    public static bool HasDoctype(string xml)
    {
        const char ByteOrderMark = (char)0xFEFF;
        ArgumentNullException.ThrowIfNull(xml);
        var i = 0;
        while (i < xml.Length)
        {
            var c = xml[i];
            if (c == ByteOrderMark || char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (string.CompareOrdinal(xml, i, "<?", 0, 2) == 0)
            {
                var end = xml.IndexOf("?>", i + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    return false;
                }

                i = end + 2;
                continue;
            }

            if (string.CompareOrdinal(xml, i, "<!--", 0, 4) == 0)
            {
                var end = xml.IndexOf("-->", i + 4, StringComparison.Ordinal);
                if (end < 0)
                {
                    return false;
                }

                i = end + 3;
                continue;
            }

            return string.CompareOrdinal(xml, i, "<!DOCTYPE", 0, 9) == 0;
        }

        return false;
    }

    /// <summary>True when an xs:boolean attribute value is true: <c>"true"</c> or <c>"1"</c>.</summary>
    internal static bool IsTrue(string? value) => value is "true" or "1";

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
