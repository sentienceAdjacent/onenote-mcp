using System.Xml;

namespace OneNoteMcp.Xml;

/// <summary>
/// One <c>one:Page</c> of a <c>FindPages</c> or <c>FindMeta</c> result, in document order. <see cref="InRecycleBin"/> is
/// true when the page, or any <c>one:Section</c> or <c>one:SectionGroup</c> around it, has <c>isInRecycleBin</c> or
/// <c>isRecycleBin</c> set (AD-12; OneMore flags the group and its sections, our fixtures also flag the page).
/// <see cref="MetaNames"/> are the <c>name</c>s of the page's child <c>one:Meta</c> elements. Values are unchanged (AD-4).
/// <see cref="LastModifiedTime"/> is the page's <c>lastModifiedTime</c> read as an <c>xs:dateTime</c>, or null when it is
/// missing or unparsable; the search page-text cache keys on it (AD-12) and never caches a page without one.
/// <see cref="SectionId"/> is the <c>ID</c> of the nearest <c>one:Section</c> around the page, or empty when there is
/// none in the XML; protected-section withholding keys on it (AD-21).
/// </summary>
public sealed record SearchResultPage(int Index, string Id, string Name, bool InRecycleBin, IReadOnlyList<string> MetaNames, DateTimeOffset? LastModifiedTime, string SectionId = "");

/// <summary>The search XML with pages cut out, and the IDs of the pages cut, in document order.</summary>
public sealed record SearchResultCut(string Xml, IReadOnlyList<string> RemovedIds);

/// <summary>
/// AD-4's exceptions: search may remove whole <c>one:Page</c> elements, and so may AD-21's withholding, from search or
/// hierarchy XML alike. The cut is by text span, so every byte
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

    /// <summary>
    /// AD-21's cut for hierarchy or search XML: every <c>one:Page</c> whose nearest <c>one:Section</c> is in
    /// <paramref name="sectionIds"/> is removed, and the <c>one:Section</c> elements themselves stay.
    /// </summary>
    public static SearchResultCut RemovePagesInSections(string xml, IReadOnlySet<string> sectionIds)
    {
        ArgumentNullException.ThrowIfNull(sectionIds);
        return sectionIds.Count == 0
            ? new SearchResultCut(xml, [])
            : Remove(xml, p => p.SectionId.Length > 0 && sectionIds.Contains(p.SectionId));
    }

    private static List<PageSpan> Scan(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);

        var positions = new XmlPositions(xml);
        var result = new List<PageSpan>();

        // One entry per open element: whether it, or an ancestor, is a recycle-bin Section or SectionGroup.
        var binStack = new Stack<bool>();

        // One entry per open element: the ID of the nearest Section that is it or around it ("" for none).
        var sectionStack = new Stack<string>();
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
                        var parentSection = sectionStack.Count > 0 ? sectionStack.Peek() : string.Empty;
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
                                parentInBin || IsBinFlagged(reader),
                                ParseDateTime(reader.GetAttribute("lastModifiedTime")),
                                parentSection);
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
                            sectionStack.Push(isOne && local == "Section" ? reader.GetAttribute("ID") ?? string.Empty : parentSection);
                        }

                        break;
                    }

                    case XmlNodeType.EndElement:
                    {
                        binStack.Pop();
                        sectionStack.Pop();
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

    private static DateTimeOffset? ParseDateTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return XmlConvert.ToDateTimeOffset(value);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentOutOfRangeException or OverflowException)
        {
            // A well-formed xs:dateTime can still be out of DateTimeOffset's range (year 1 at +14:00, year 9999 at -14:00).
            return null;
        }
    }

    private sealed record PageSpan(SearchResultPage Page, int Start, int End);

    private sealed class PageBuilder(int index, int start, string id, string name, bool inRecycleBin, DateTimeOffset? lastModifiedTime, string sectionId)
    {
        public List<string> MetaNames { get; } = [];

        public PageSpan Build(int end) => new(new SearchResultPage(index, id, name, inRecycleBin, [.. MetaNames], lastModifiedTime, sectionId), start, end);
    }
}
