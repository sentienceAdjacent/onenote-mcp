using System.Xml;
using System.Xml.Linq;

namespace OneNoteMcp.Xml;

/// <summary>
/// One element of a stored result (AD-20), located by its exact span in the original string: the element's text runs
/// from <see cref="Start"/> (its '&lt;') to <see cref="End"/> (just past its closing '&gt;'). The root node is special:
/// its ref is <c>/</c> and its span is the whole string, so offset reads of <c>/</c> also reach the XML declaration and
/// anything else outside the root element.
/// </summary>
public sealed class SpanNode
{
    private readonly List<SpanNode> children = [];

    internal SpanNode(int order, string? element, SpanNode? parent, int indexInParent, int start, string? oneLocalName = null)
    {
        Order = order;
        Element = element;
        OneLocalName = oneLocalName;
        Parent = parent;
        IndexInParent = indexInParent;
        Start = start;
        Ref = string.Empty;
    }

    /// <summary>The node's position in document order; the root is 0.</summary>
    public int Order { get; }

    /// <summary>The element's qualified name as written (<c>one:Outline</c>); null for the root of a body that isn't XML.</summary>
    public string? Element { get; }

    /// <summary>The local name when the element is in the 2013 namespace; null otherwise.</summary>
    internal string? OneLocalName { get; }

    public SpanNode? Parent { get; }

    /// <summary>The node's position among its parent's child elements, from 0.</summary>
    public int IndexInParent { get; }

    public int Start { get; internal set; }

    public int End { get; internal set; }

    public int Length => End - Start;

    /// <summary>
    /// The node's ref (AD-20): <c>/</c> for the root; else its <c>objectID</c> (page XML) or <c>ID</c> (hierarchy XML),
    /// unchanged; else, or when that value already names an earlier element, <c>&lt;parent's ref&gt;/&lt;n&gt;</c>.
    /// </summary>
    public string Ref { get; internal set; }

    public IReadOnlyList<SpanNode> Children => children;

    /// <summary>A hierarchy node's <c>name</c>, or the first 80 characters of a page element's text; null when there is none.</summary>
    public string? Label { get; internal set; }

    /// <summary>The <c>objectID</c> of a page's <c>one:Outline</c> or <c>one:OE</c>, the elements AD-17 hashes; null otherwise.</summary>
    public string? HashId { get; internal set; }

    /// <summary>Counts of <c>one:Table</c>, <c>one:Image</c> and <c>one:InsertedFile</c> anywhere below this node.</summary>
    public int Tables { get; internal set; }

    public int Images { get; internal set; }

    public int InsertedFiles { get; internal set; }

    internal void Add(SpanNode child) => children.Add(child);
}

/// <summary>The span tree of one stored result (AD-20): every element, in document order, with exact offsets.</summary>
public sealed class SpanTree
{
    private readonly Dictionary<string, SpanNode> byRef;

    internal SpanTree(string text, SpanNode root, IReadOnlyList<SpanNode> nodes, Dictionary<string, SpanNode> byRef, IReadOnlyDictionary<string, string> namespaces)
    {
        Text = text;
        Root = root;
        Nodes = nodes;
        this.byRef = byRef;
        Namespaces = namespaces;
    }

    /// <summary>The stored body the spans index into.</summary>
    public string Text { get; }

    public SpanNode Root { get; }

    /// <summary>Every node, in document order (by <see cref="SpanNode.Start"/>), the root first.</summary>
    public IReadOnlyList<SpanNode> Nodes { get; }

    /// <summary>The root element's namespace declarations, keyed by attribute name (<c>xmlns:one</c>). Empty for a body that isn't XML.</summary>
    public IReadOnlyDictionary<string, string> Namespaces { get; }

    public SpanNode? Find(string reference) =>
        reference is not null && byRef.TryGetValue(reference, out var node) ? node : null;

    /// <summary>The nodes whose spans lie wholly within [<paramref name="start"/>, <paramref name="end"/>), in document order.</summary>
    public IEnumerable<SpanNode> Within(int start, int end)
    {
        // Nodes are sorted by Start; find the first that starts at or after start.
        int lo = 0, hi = Nodes.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (Nodes[mid].Start < start)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        for (var i = lo; i < Nodes.Count && Nodes[i].Start < end; i++)
        {
            if (Nodes[i].End <= end && Nodes[i].Length > 0)
            {
                yield return Nodes[i];
            }
        }
    }
}

/// <summary>
/// Builds the <see cref="SpanTree"/> of a stored result (AD-20) from the original string, by reader position, never by
/// re-serialising (AD-4). Malformed XML throws <see cref="OneNoteXmlException"/>, whose message carries no content.
/// </summary>
public static class ElementSpans
{
    /// <summary>The length of a page element's label (AD-20).</summary>
    public const int LabelChars = 80;

    private static readonly XName Outline = OneNoteXml.One + "Outline";
    private static readonly XName OE = OneNoteXml.One + "OE";

    /// <summary>Page XML: refs are <c>objectID</c>s, labels are the element's text by AD-12's rule, Outlines and OEs carry <see cref="SpanNode.HashId"/>.</summary>
    public static SpanTree Page(string xml)
    {
        var tree = Scan(xml, "objectID", nameLabels: false);

        // Document order is the same for the reader and for XLinq, so the two walks pair up element by element.
        var elements = OneNoteXml.Parse(xml).Root!.DescendantsAndSelf().ToList();
        if (elements.Count != tree.Nodes.Count)
        {
            throw new OneNoteXmlException("The page XML's elements could not be paired with their positions.");
        }

        for (var i = 0; i < elements.Count; i++)
        {
            var element = elements[i];
            var node = tree.Nodes[i];
            if (PageText.Label(element, LabelChars) is { Length: > 0 } label)
            {
                node.Label = label;
            }

            if ((element.Name == Outline || element.Name == OE) && (string?)element.Attribute("objectID") is { Length: > 0 } objectId)
            {
                node.HashId = objectId;
            }
        }

        return tree;
    }

    /// <summary>Hierarchy XML: refs are <c>ID</c>s, labels are <c>name</c>s.</summary>
    public static SpanTree Hierarchy(string xml) => Scan(xml, "ID", nameLabels: true);

    /// <summary>A body that isn't XML: one root node, <c>/</c>, spanning it all, with no children.</summary>
    public static SpanTree Text(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var root = new SpanNode(0, element: null, parent: null, indexInParent: 0, start: 0) { End = text.Length, Ref = "/" };
        return new SpanTree(text, root, [root], new Dictionary<string, SpanNode>(StringComparer.Ordinal) { ["/"] = root }, new Dictionary<string, string>());
    }

    private static SpanTree Scan(string xml, string refAttribute, bool nameLabels)
    {
        ArgumentNullException.ThrowIfNull(xml);

        var positions = new XmlPositions(xml);
        var nodes = new List<SpanNode>();
        var byRef = new Dictionary<string, SpanNode>(StringComparer.Ordinal);
        var namespaces = new Dictionary<string, string>(StringComparer.Ordinal);
        var open = new Stack<SpanNode>();
        SpanNode? root = null;

        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), XmlPositions.ReaderSettings);
            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                    {
                        var start = positions.StartTagStart(reader);
                        var isEmpty = reader.IsEmptyElement;
                        SpanNode node;
                        if (root is null)
                        {
                            // The root's span is the whole body (see SpanNode).
                            node = root = new SpanNode(0, reader.Name, parent: null, indexInParent: 0, start: 0) { End = xml.Length, Ref = "/" };
                            byRef.Add("/", node);
                            if (reader.MoveToFirstAttribute())
                            {
                                do
                                {
                                    if (reader.Name == "xmlns" || reader.Name.StartsWith("xmlns:", StringComparison.Ordinal))
                                    {
                                        namespaces[reader.Name] = reader.Value;
                                    }
                                }
                                while (reader.MoveToNextAttribute());
                                reader.MoveToElement();
                            }
                        }
                        else
                        {
                            var parent = open.Peek();
                            var oneLocal = reader.NamespaceURI == OneNoteXml.Namespace2013 ? reader.LocalName : null;
                            node = new SpanNode(nodes.Count, reader.Name, parent, parent.Children.Count, start, oneLocal);
                            parent.Add(node);
                            node.Ref = UniqueRef(byRef, reader.GetAttribute(refAttribute), parent, node.IndexInParent);
                            byRef.Add(node.Ref, node);
                        }

                        if (nameLabels && reader.GetAttribute("name") is { Length: > 0 } name)
                        {
                            node.Label = name;
                        }

                        nodes.Add(node);
                        if (isEmpty)
                        {
                            Close(node, positions.StartTagEnd(start));
                        }
                        else
                        {
                            open.Push(node);
                        }

                        break;
                    }

                    case XmlNodeType.EndElement:
                        Close(open.Pop(), positions.EndTagEnd(reader));
                        break;
                }
            }
        }
        catch (XmlException ex)
        {
            throw XmlPositions.NotWellFormed(ex);
        }

        if (root is null)
        {
            throw new OneNoteXmlException("The XML has no root element.");
        }

        return new SpanTree(xml, root, nodes, byRef, namespaces);
    }

    private static void Close(SpanNode node, int end)
    {
        if (node.Parent is null)
        {
            return; // the root keeps its whole-body span
        }

        node.End = end;
        var parent = node.Parent;
        parent.Tables += node.Tables + (node.OneLocalName == "Table" ? 1 : 0);
        parent.Images += node.Images + (node.OneLocalName == "Image" ? 1 : 0);
        parent.InsertedFiles += node.InsertedFiles + (node.OneLocalName == "InsertedFile" ? 1 : 0);
    }

    private static string UniqueRef(Dictionary<string, SpanNode> byRef, string? id, SpanNode parent, int index)
    {
        if (!string.IsNullOrEmpty(id) && !byRef.ContainsKey(id))
        {
            return id;
        }

        var positional = (parent.Ref == "/" ? "/" : parent.Ref + "/") + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        while (byRef.ContainsKey(positional))
        {
            positional += "'"; // only if a real ID happens to look positional
        }

        return positional;
    }
}
