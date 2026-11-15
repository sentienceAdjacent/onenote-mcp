using System.Xml;

namespace OneNoteMcp.Xml;

/// <summary>
/// The one change the server makes to page XML it sends (backlog 2; Phill, 2026-11-15): the <c>z</c> attribute is cut
/// from every <c>one:Position</c>. Resending an Outline with its <c>z</c>, even the value just read, makes OneNote
/// re-stack the page's Outlines, which changes the hash of Outlines the caller didn't send; sent without <c>z</c>,
/// nothing moves (Z probe 57, 2026-11-15 sitting). Only an unprefixed <c>z</c> on a <c>Position</c> in the 2013
/// namespace is cut, with the whitespace before it, by text span (AD-4): every other byte stays, and with nothing to
/// cut the input string itself comes back. Malformed XML throws <see cref="OneNoteXmlException"/> with its position only.
/// </summary>
public static class SentPositions
{
    /// <summary><paramref name="xml"/> with each <c>one:Position</c>'s <c>z</c> attribute removed, and nothing else changed.</summary>
    public static string WithoutZ(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);

        var positions = new XmlPositions(xml);
        var cuts = new List<(int Start, int End)>();
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), XmlPositions.ReaderSettings);
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element
                    && reader.LocalName == "Position"
                    && reader.NamespaceURI == OneNoteXml.Namespace2013
                    && reader.MoveToAttribute("z"))
                {
                    cuts.Add(positions.AttributeSpan(reader));
                    reader.MoveToElement();
                }
            }
        }
        catch (XmlException ex)
        {
            throw XmlPositions.NotWellFormed(ex);
        }

        if (cuts.Count == 0)
        {
            return xml;
        }

        var builder = new System.Text.StringBuilder(xml.Length);
        var next = 0;
        foreach (var (start, end) in cuts)
        {
            builder.Append(xml, next, start - next);
            next = end;
        }

        builder.Append(xml, next, xml.Length - next);
        return builder.ToString();
    }
}
