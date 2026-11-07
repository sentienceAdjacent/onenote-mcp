using System.Xml;

namespace OneNoteMcp.Xml;

/// <summary>
/// One <c>one:Page</c> of a <c>FindPages</c> or <c>FindMeta</c> result, in document order. <see cref="InRecycleBin"/> is
/// true when the page, or any <c>one:Section</c> or <c>one:SectionGroup</c> around it, has <c>isInRecycleBin</c> or
/// <c>isRecycleBin</c> set (AD-12; OneMore flags the group and its sections, our fixtures also flag the page).
/// <see cref="MetaNames"/> are the <c>name</c>s of the page's child <c>one:Meta</c> elements. Values are unchanged (AD-4).
/// </summary>
public sealed record SearchResultPage(int Index, string Id, string Name, bool InRecycleBin, IReadOnlyList<string> MetaNames);

/// <summary>The search XML with pages cut out, and the IDs of the pages cut, in document order.</summary>
public sealed record SearchResultCut(string Xml, IReadOnlyList<string> RemovedIds);

/// <summary>
/// AD-4's one exception: search may remove whole <c>one:Page</c> elements. The cut is by text span, so every byte
/// outside a removed page is exactly what OneNote returned, and with nothing removed the input string itself comes back.
/// The XML is never re-serialised. Malformed XML throws <see cref="OneNoteXmlException"/>.
/// </summary>
public static class SearchResultFilter
{
    /// <summary>Every outermost <c>one:Page</c> in <paramref name="xml"/>, in document order.</summary>
    public static IReadOnlyList<SearchResultPage> Pages(string xml) => [.. Scan(xml).Select(s => s.Page)];

    /// <summary>
    /// Returns <paramref name="xml"/> with each page that <paramref name="remove"/> picks cut out, start tag to end tag
    /// (or the whole self-closed tag), and nothing else changed.
    /// </summary>
    public static SearchResultCut Remove(string xml, Func<SearchResultPage, bool> remove)
    {
        ArgumentNullException.ThrowIfNull(remove);

        var spans = Scan(xml).Where(s => remove(s.Page)).ToList();
        if (spans.Count == 0)
        {
            return new SearchResultCut(xml, []);
        }

        var builder = new System.Text.StringBuilder(xml.Length);
        var next = 0;
        foreach (var span in spans)
        {
            builder.Append(xml, next, span.Start - next);
            next = span.End;
        }

        builder.Append(xml, next, xml.Length - next);
        return new SearchResultCut(builder.ToString(), [.. spans.Select(s => s.Page.Id)]);
    }

    private static List<PageSpan> Scan(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);

        var positions = new XmlPositions(xml);
        var result = new List<PageSpan>();

        // One entry per open element: whether it, or an ancestor, is a recycle-bin Section or SectionGroup.
        var binStack = new Stack<bool>();
        PageBuilder? page = null;
        var pageDepth = -1;

        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), XmlPositions.ReaderSettings);
            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                    {
                        var depth = reader.Depth;
                        var isEmpty = reader.IsEmptyElement;
                        var parentInBin = binStack.Count > 0 && binStack.Peek();
                        var isOne = reader.NamespaceURI == OneNoteXml.Namespace2013;
                        var local = reader.LocalName;

                        if (page is null && isOne && local == "Page")
                        {
                            var start = positions.StartTagStart(reader);
                            page = new PageBuilder(
                                result.Count,
                                start,
                                reader.GetAttribute("ID") ?? string.Empty,
                                reader.GetAttribute("name") ?? string.Empty,
                                parentInBin || IsBinFlagged(reader));
                            pageDepth = depth;
                            if (isEmpty)
                            {
                                result.Add(page.Build(positions.StartTagEnd(start)));
                                page = null;
                            }
                        }
                        else if (page is not null && isOne && local == "Meta" && depth == pageDepth + 1)
                        {
                            page.MetaNames.Add(reader.GetAttribute("name") ?? string.Empty);
                        }

                        var inBin = parentInBin || (isOne && (local is "Section" or "SectionGroup") && IsBinFlagged(reader));
                        if (!isEmpty)
                        {
                            binStack.Push(inBin);
                        }

                        break;
                    }

                    case XmlNodeType.EndElement:
                    {
                        binStack.Pop();
                        if (page is not null && reader.Depth == pageDepth)
                        {
                            result.Add(page.Build(positions.EndTagEnd(reader)));
                            page = null;
                        }

                        break;
                    }
                }
            }
        }
        catch (XmlException ex)
        {
            throw XmlPositions.NotWellFormed(ex);
        }

        return result;
    }

    // xs:boolean allows "true" or "1".
    private static bool IsBinFlagged(XmlReader reader) =>
        IsTrue(reader.GetAttribute("isInRecycleBin")) || IsTrue(reader.GetAttribute("isRecycleBin"));

    private static bool IsTrue(string? value) => value is "true" or "1";

    private sealed record PageSpan(SearchResultPage Page, int Start, int End);

    private sealed class PageBuilder(int index, int start, string id, string name, bool inRecycleBin)
    {
        public List<string> MetaNames { get; } = [];

        public PageSpan Build(int end) => new(new SearchResultPage(index, id, name, inRecycleBin, [.. MetaNames]), start, end);
    }
}
