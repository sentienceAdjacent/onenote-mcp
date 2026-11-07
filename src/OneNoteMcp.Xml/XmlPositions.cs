using System.Xml;

namespace OneNoteMcp.Xml;

/// <summary>
/// Maps an <see cref="XmlReader"/>'s line and column back to character offsets in the original string, so callers
/// can cut or index exact substrings without re-serialising (AD-4). Shared by <see cref="SearchResultFilter"/> and
/// <see cref="ElementSpans"/>. Messages name only positions and tags, never content (AD-13).
/// </summary>
internal sealed class XmlPositions
{
    private readonly string xml;
    private readonly List<int> lineStarts;

    public XmlPositions(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        this.xml = xml;
        lineStarts = LineStarts(xml);
    }

    public static XmlReaderSettings ReaderSettings { get; } = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreWhitespace = false,
        IgnoreComments = false,
    };

    /// <summary>The offset of the '&lt;' of the start tag the reader is on, checked against the text.</summary>
    public int StartTagStart(XmlReader reader)
    {
        var start = Offset((IXmlLineInfo)reader, reader.Name) - 1;
        Expect(start, reader.Name);
        return start;
    }

    /// <summary>The offset just past the '&gt;' of the end tag the reader is on, checked against the text.</summary>
    public int EndTagEnd(XmlReader reader)
    {
        // The reader points at the name after "</"; an end tag holds only its name and whitespace.
        var nameStart = Offset((IXmlLineInfo)reader, reader.Name);
        Expect(nameStart - 2, "/" + reader.Name);
        var close = xml.IndexOf('>', nameStart);
        if (close < 0)
        {
            throw new OneNoteXmlException($"A {reader.Name} end tag has no closing '>'.");
        }

        return close + 1;
    }

    /// <summary>
    /// The offset just past the '&gt;' that closes the start tag at <paramref name="ltOffset"/>. Attribute values are
    /// skipped quote to quote, since a value may hold an unescaped '&gt;'.
    /// </summary>
    public int StartTagEnd(int ltOffset)
    {
        char? quote = null;
        for (var i = ltOffset + 1; i < xml.Length; i++)
        {
            var c = xml[i];
            if (quote is { } q)
            {
                if (c == q)
                {
                    quote = null;
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == '>')
            {
                return i + 1;
            }
        }

        throw new OneNoteXmlException("A start tag has no closing '>'.");
    }

    /// <summary>A parser error with its position only: the parser's message can quote content (AD-13).</summary>
    public static OneNoteXmlException NotWellFormed(XmlException ex) =>
        new($"The XML is not well-formed (line {ex.LineNumber}, position {ex.LinePosition}).", ex);

    /// <summary>The start of each line, breaking lines the way <see cref="XmlReader"/> counts them: \r\n, \r or \n.</summary>
    private static List<int> LineStarts(string xml)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < xml.Length; i++)
        {
            if (xml[i] == '\r')
            {
                if (i + 1 < xml.Length && xml[i + 1] == '\n')
                {
                    i++;
                }

                starts.Add(i + 1);
            }
            else if (xml[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }

        return starts;
    }

    private int Offset(IXmlLineInfo info, string tag)
    {
        if (!info.HasLineInfo() || info.LineNumber < 1 || info.LineNumber > lineStarts.Count)
        {
            throw new OneNoteXmlException($"The XML reader gave no usable position for a {tag}.");
        }

        var offset = lineStarts[info.LineNumber - 1] + info.LinePosition - 1;
        if (offset < 0 || offset > xml.Length)
        {
            throw new OneNoteXmlException($"The XML reader gave a position outside the XML for a {tag}.");
        }

        return offset;
    }

    /// <summary>Guards the position arithmetic: the tag must really start at <paramref name="ltOffset"/>.</summary>
    private void Expect(int ltOffset, string nameAfterLt)
    {
        if (ltOffset < 0
            || ltOffset >= xml.Length
            || xml[ltOffset] != '<'
            || string.CompareOrdinal(xml, ltOffset + 1, nameAfterLt, 0, nameAfterLt.Length) != 0)
        {
            throw new OneNoteXmlException($"A {nameAfterLt.TrimStart('/')} tag is not where the XML reader placed it.");
        }
    }
}
