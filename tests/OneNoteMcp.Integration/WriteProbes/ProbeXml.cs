using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using OneNoteMcp.Xml;

namespace OneNoteMcp.Integration.WriteProbes;

/// <summary>Builders and readers for story 2.2's probe page XML. Pure: unit-tested in <c>ProbeHelpersTests</c>.</summary>
internal static class ProbeXml
{
    public static readonly XNamespace One = OneNoteXml.Namespace2013;

    // 2x2 PNG, four colours (the same one FixtureCapture uses).
    public const string PngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAIAAAD91JpzAAAAFklEQVR4nGP4z8DwHwyBNBgw/IdQDACMsAj/6nrfNgAAAABJRU5ErkJggg==";

    // --- Builders (strings, so a probe controls every byte it sends) ----------------------------------------------

    /// <summary>A partial <c>UpdatePageContent</c> document in <paramref name="ns"/> (default 2013).</summary>
    public static string Page(string pageId, string body, string? ns = null) =>
        $"""<?xml version="1.0"?><one:Page xmlns:one="{ns ?? OneNoteXml.Namespace2013}" ID="{Attr(pageId)}">{body}</one:Page>""";

    public static string Title(string title) => $"<one:Title><one:OE><one:T>{Cdata(title)}</one:T></one:OE></one:Title>";

    /// <summary>An Outline (with <paramref name="objectId"/> when given) holding <paramref name="children"/> OEs.</summary>
    public static string Outline(string? objectId, params string[] children) => OutlineWith(objectId, string.Empty, children);

    /// <summary>An Outline with leading elements (<c>one:Position</c>, <c>one:Size</c>) before its OEChildren.</summary>
    public static string OutlineWith(string? objectId, string leading, params string[] children) =>
        $"<one:Outline{Id(objectId)}>{leading}<one:OEChildren>{string.Concat(children)}</one:OEChildren></one:Outline>";

    /// <summary>An OE with text (raw CDATA content) and optional extra attributes (pre-rendered, leading space included).</summary>
    public static string Oe(string? objectId, string text, string attributes = "") =>
        $"<one:OE{Id(objectId)}{attributes}><one:T>{Cdata(text)}</one:T></one:OE>";

    /// <summary>An OE whose inner XML is given verbatim.</summary>
    public static string OeRaw(string? objectId, string inner, string attributes = "") =>
        $"<one:OE{Id(objectId)}{attributes}>{inner}</one:OE>";

    public static string Position(double x, double y) =>
        string.Create(CultureInfo.InvariantCulture, $"""<one:Position x="{x:0.0#}" y="{y:0.0#}" z="0" />""");

    public static string Size(double width, double height) =>
        string.Create(CultureInfo.InvariantCulture, $"""<one:Size width="{width:0.0#}" height="{height:0.0#}" isSetByUser="true" />""");

    /// <summary>A table in an OE: <paramref name="columns"/> as (width, locked), then rows of cell texts.</summary>
    public static string Table(IReadOnlyList<(double Width, bool Locked)> columns, params string[][] rows)
    {
        var sb = new StringBuilder("<one:Table bordersVisible=\"true\"><one:Columns>");
        for (var i = 0; i < columns.Count; i++)
        {
            sb.Append(CultureInfo.InvariantCulture, $"""<one:Column index="{i}" width="{columns[i].Width:0.0#}"{(columns[i].Locked ? " isLocked=\"true\"" : string.Empty)} />""");
        }

        sb.Append("</one:Columns>");
        foreach (var row in rows)
        {
            sb.Append("<one:Row>");
            foreach (var cell in row)
            {
                sb.Append($"<one:Cell><one:OEChildren>{Oe(null, cell)}</one:OEChildren></one:Cell>");
            }

            sb.Append("</one:Row>");
        }

        return sb.Append("</one:Table>").ToString();
    }

    // --- Hierarchy XML (story 5.1) ---------------------------------------------------------------------------------

    /// <summary>An <c>UpdateHierarchy</c> document: a <paramref name="kind"/> root with its ID, extra attributes and children.</summary>
    public static string Hierarchy(string kind, string id, string attributes = "", params string[] children) =>
        $"""<?xml version="1.0"?><one:{kind} xmlns:one="{OneNoteXml.Namespace2013}" ID="{Attr(id)}"{attributes}{Close(kind, children)}""";

    /// <summary>A hierarchy element (<c>Section</c>, <c>SectionGroup</c>, <c>Page</c>), with an ID when given.</summary>
    public static string Node(string kind, string? id, string attributes = "", params string[] children) =>
        $"<one:{kind}{(id is null ? string.Empty : $" ID=\"{Attr(id)}\"")}{attributes}{Close(kind, children)}";

    /// <summary>One attribute, rendered with a leading space and escaped: <c> name="…"</c>.</summary>
    public static string A(string name, string value) => $" {name}=\"{Attr(value)}\"";

    private static string Close(string kind, string[] children) =>
        children.Length == 0 ? " />" : $">{string.Concat(children)}</one:{kind}>";

    /// <summary>The direct Section and SectionGroup children of <paramref name="node"/>, recycle bin left out, in order.</summary>
    public static IReadOnlyList<NodeInfo> Children(XElement node) =>
        node.Elements()
            .Where(e => e.Name.LocalName is "Section" or "SectionGroup")
            .Where(e => (string?)e.Attribute("isRecycleBin") != "true" && (string?)e.Attribute("isInRecycleBin") != "true")
            .Select(NodeInfo.From)
            .ToList();

    /// <summary>The names of the pages directly in <paramref name="section"/>, in order.</summary>
    public static IReadOnlyList<string?> PageNames(XElement section) =>
        section.Elements(section.Name.Namespace + "Page").Select(p => (string?)p.Attribute("name")).ToList();

    public static string Meta(string name, string content) => $"""<one:Meta name="{Attr(name)}" content="{Attr(content)}" />""";

    /// <summary>CDATA around <paramref name="text"/> verbatim (so <c>&amp;nbsp;</c> stays an entity reference for OneNote to parse).</summary>
    public static string Cdata(string text) => $"<![CDATA[{text}]]>";

    public static string Attr(string value) => value.Replace("&", "&amp;", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal);

    private static string Id(string? objectId) => objectId is null ? string.Empty : $" objectID=\"{Attr(objectId)}\"";

    // --- Readers --------------------------------------------------------------------------------------------------

    public static XElement? ById(XElement root, string id) =>
        root.DescendantsAndSelf().FirstOrDefault(e => (string?)e.Attribute("ID") == id || (string?)e.Attribute("objectID") == id);

    /// <summary>The text of an OE's own <c>one:T</c> (not its children's), or null.</summary>
    public static string? Text(XElement oe) => oe.Elements(oe.Name.Namespace + "T").Select(t => t.Value).FirstOrDefault();

    /// <summary>The first OE (anywhere below <paramref name="root"/>, title excluded) whose own text contains <paramref name="marker"/>.</summary>
    public static XElement? OeByText(XElement root, string marker) =>
        Oes(root).FirstOrDefault(oe => Text(oe)?.Contains(marker, StringComparison.Ordinal) == true);

    /// <summary>Every OE whose own text contains <paramref name="marker"/>, title excluded.</summary>
    public static IReadOnlyList<XElement> OesByText(XElement root, string marker) =>
        Oes(root).Where(oe => Text(oe)?.Contains(marker, StringComparison.Ordinal) == true).ToList();

    public static IEnumerable<XElement> Oes(XElement root) =>
        root.Descendants(root.Name.Namespace + "OE").Where(oe => !oe.Ancestors(root.Name.Namespace + "Title").Any());

    public static IEnumerable<XElement> Outlines(XElement page) => page.Elements(page.Name.Namespace + "Outline");

    public static string? ObjectId(XElement? e) => (string?)e?.Attribute("objectID");

    /// <summary>The objectID of the Outline holding <paramref name="e"/>, or null.</summary>
    public static string? OutlineOf(XElement e) => ObjectId(e.Ancestors(e.Name.Namespace + "Outline").FirstOrDefault());

    public static string? Lmt(XElement? e) => (string?)e?.Attribute("lastModifiedTime");

    /// <summary>An XML date attribute as a UTC <see cref="DateTime"/> (Kind Utc), for <c>dateExpectedLastModified</c>.</summary>
    public static DateTime ParseUtc(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    public static string FormatUtc(DateTime value) => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>AD-17's page hash of a 2013 page document.</summary>
    public static string PageHash(string pageXml) => PageBasis.From(pageXml).PageHash;

    /// <summary>The page hash after removing the named attribute on every element of the named local name (both null: plain hash).</summary>
    public static string PageHashWithout(string pageXml, string elementLocalName, string attributeName, Func<XElement, bool>? where = null)
    {
        var doc = XDocument.Parse(pageXml);
        foreach (var e in doc.Root!.Descendants(One + elementLocalName).Where(e => where?.Invoke(e) ?? true))
        {
            e.Attribute(attributeName)?.Remove();
        }

        return ElementHash.Compute(doc.Root);
    }

    /// <summary>A short, stable fingerprint for a value that shouldn't be logged verbatim (paths, text).</summary>
    public static string Fingerprint(string? value) =>
        value is null ? "null" : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..12];

    /// <summary>The <c>one:Column</c> elements of the first table in <paramref name="root"/>, as (index, width, locked).</summary>
    public static IReadOnlyList<(string Index, string Width, bool Locked)> Columns(XElement root) =>
        root.Descendants(root.Name.Namespace + "Table").Take(1)
            .SelectMany(t => t.Descendants(root.Name.Namespace + "Column"))
            .Select(c => ((string?)c.Attribute("index") ?? "?", (string?)c.Attribute("width") ?? "?", (string?)c.Attribute("isLocked") == "true"))
            .ToList();

    /// <summary>
    /// Summarises a sequence of read values: the distinct values, and the pattern as letters in first-seen order
    /// (<c>ABAB…</c>).
    /// </summary>
    public static (int Distinct, string Pattern) Pattern(IReadOnlyList<string> values)
    {
        var letters = new Dictionary<string, char>(StringComparer.Ordinal);
        var sb = new StringBuilder(values.Count);
        foreach (var value in values)
        {
            if (!letters.TryGetValue(value, out var letter))
            {
                letters[value] = letter = (char)('A' + Math.Min(letters.Count, 25));
            }

            sb.Append(letter);
        }

        return (letters.Count, sb.ToString());
    }
}

/// <summary>
/// Story 5.1's read-back of one hierarchy node: what a delete or hierarchy write probe logs. <see cref="InRecycleBin"/>
/// is true when the node or any ancestor is the recycle bin, or the node is marked <c>isInRecycleBin</c>.
/// </summary>
internal sealed record NodeInfo(
    string Kind,
    string? Id,
    string? Name,
    string? Nickname,
    string? ParentKind,
    string? ParentName,
    bool InRecycleBin,
    string? Path,
    string? Encrypted,
    string? Locked,
    string? LastModifiedTime,
    int Pages)
{
    public static NodeInfo From(XElement e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var parent = e.Parent;
        var inBin = (string?)e.Attribute("isInRecycleBin") == "true"
            || e.AncestorsAndSelf().Any(a => (string?)a.Attribute("isRecycleBin") == "true");
        return new NodeInfo(
            e.Name.LocalName,
            (string?)e.Attribute("ID"),
            (string?)e.Attribute("name"),
            (string?)e.Attribute("nickname"),
            parent?.Name.LocalName,
            (string?)parent?.Attribute("name"),
            inBin,
            (string?)e.Attribute("path"),
            (string?)e.Attribute("encrypted"),
            (string?)e.Attribute("locked"),
            (string?)e.Attribute("lastModifiedTime"),
            e.Elements(e.Name.Namespace + "Page").Count());
    }
}
