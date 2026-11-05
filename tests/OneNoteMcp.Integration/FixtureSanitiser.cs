using System.Text.RegularExpressions;
using System.Xml.Linq;
using OneNoteMcp.Xml;

namespace OneNoteMcp.Integration;

/// <summary>
/// Makes captured OneNote output safe to commit (story 6). Pure, unit-tested in <c>FixtureSanitiserTests</c>.
/// Local MCP Test captures keep OneNote's exact text except for the edits below; the cloud capture is rewritten
/// structurally so nothing from Phill's notebook survives but the page's shape.
/// </summary>
internal static partial class FixtureSanitiser
{
    public const string PlaceholderProfile = @"C:\Users\mcp-test";
    public const string PlaceholderAuthor = "MCP Test";
    public const string PlaceholderInitials = "MT";

    /// <summary>Attributes that name a person. Initials get <see cref="PlaceholderInitials"/>, the rest <see cref="PlaceholderAuthor"/>.</summary>
    public static readonly IReadOnlyList<string> AuthorAttributes =
        ["author", "authorInitials", "authorResolutionID", "lastModifiedBy", "lastModifiedByInitials", "lastModifiedByResolutionID"];

    private static readonly XNamespace One = OneNoteXml.Namespace2013;

    /// <summary>
    /// For notebook-scope hierarchy XML (root <c>one:Notebooks</c>): keeps only the notebook whose ID is
    /// <paramref name="notebookId"/>, dropping every other notebook, <c>one:UnfiledNotes</c> and open sections.
    /// The text outside the removed elements is kept as OneNote wrote it.
    /// </summary>
    public static string KeepOnlyNotebook(string hierarchyXml, string notebookId)
    {
        var (prolog, document) = ParseKeepingText(hierarchyXml);
        var root = document.Root!;
        if (root.Name != One + "Notebooks")
        {
            throw new InvalidOperationException($"Expected a one:Notebooks root, found {root.Name.LocalName}.");
        }

        var kept = root.Elements().Where(e => e.Name == One + "Notebook" && (string?)e.Attribute("ID") == notebookId).ToList();
        if (kept.Count != 1)
        {
            throw new InvalidOperationException($"Expected exactly one MCP Test notebook in the capture, found {kept.Count}.");
        }

        foreach (var element in root.Elements().Except(kept).ToList())
        {
            RemoveWithLeadingWhitespace(element);
        }

        return prolog + root.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>
    /// Replaces the user-profile prefix with <see cref="PlaceholderProfile"/> (backslash, forward-slash and
    /// URL-encoded forms, case-insensitively) and sets every author attribute to its placeholder.
    /// </summary>
    public static string Scrub(string text, string userProfile)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(userProfile);

        var profile = userProfile.TrimEnd('\\', '/');
        foreach (var (from, to) in new[]
                 {
                     (profile, PlaceholderProfile),
                     (profile.Replace('\\', '/'), PlaceholderProfile.Replace('\\', '/')),
                     (Uri.EscapeDataString(profile), Uri.EscapeDataString(PlaceholderProfile)),
                     (profile.Replace(@"\", "%5C", StringComparison.Ordinal), PlaceholderProfile.Replace(@"\", "%5C", StringComparison.Ordinal)),
                 })
        {
            text = text.Replace(from, to, StringComparison.OrdinalIgnoreCase);
        }

        return AuthorAttribute().Replace(text, m =>
            $"{m.Groups["name"].Value}=\"{(m.Groups["name"].Value.EndsWith("Initials", StringComparison.Ordinal) ? PlaceholderInitials : PlaceholderAuthor)}\"");
    }

    /// <summary>
    /// For a value that points outside MCP Test (a special location): a OneDrive URL
    /// (<c>https://d.docs.live.net/&lt;cid&gt;/&lt;folders&gt;/&lt;file&gt;</c>) keeps its host, first folder and file
    /// name, while the account ID becomes zeros and every other folder a placeholder. Anything else is unchanged.
    /// </summary>
    public static string ScrubCloudUrl(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var match = OneDriveUrl().Match(value);
        if (!match.Success)
        {
            return value;
        }

        var segments = match.Groups["rest"].Value.Split('/');
        for (var i = 1; i < segments.Length - 1; i++)
        {
            segments[i] = "MCP Cloud Notebook";
        }

        return $"https://d.docs.live.net/0000000000000000/{string.Join('/', segments)}";
    }

    /// <summary>
    /// The cloud page capture: every GUID in an attribute value is mapped, consistently, to a placeholder GUID (so IDs
    /// keep the XSD's <c>ObjectID</c> pattern and their relationships); every text node except <c>one:Data</c> becomes
    /// placeholder text; the title, image alt text, links, Meta values and author attributes become placeholders.
    /// <paramref name="guidMap"/> is shared across the captures of one page so their IDs agree.
    /// </summary>
    public static string SanitiseCloud(string xml, Dictionary<string, string> guidMap)
    {
        ArgumentNullException.ThrowIfNull(guidMap);
        var (prolog, document) = ParseKeepingText(xml);
        var root = document.Root!;

        var textIndex = 0;
        foreach (var node in root.DescendantNodes().OfType<XText>().ToList())
        {
            if (string.IsNullOrWhiteSpace(node.Value) || node.Parent?.Name == One + "Data")
            {
                continue;
            }

            textIndex++;
            node.Value = $"Cloud placeholder text {textIndex}";
        }

        foreach (var element in root.DescendantsAndSelf())
        {
            foreach (var attribute in element.Attributes().Where(a => !a.IsNamespaceDeclaration).ToList())
            {
                var name = attribute.Name.LocalName;
                if (AuthorAttributes.Contains(name))
                {
                    attribute.Value = name.EndsWith("Initials", StringComparison.Ordinal) ? PlaceholderInitials : PlaceholderAuthor;
                }
                else if (name is "name" or "alt" or "hyperlink" or "content" or "path")
                {
                    attribute.Value = name switch
                    {
                        "name" when element.Name == One + "Page" => "Cloud Test Page",
                        "name" when element.Name == One + "Meta" => attribute.Value, // Meta names are keys, not content
                        "name" when element.Name == One + "TagDef" => attribute.Value, // tag names are OneNote's
                        "name" when element.Name == One + "QuickStyleDef" => attribute.Value, // style names are OneNote's (p, PageTitle)
                        "alt" => "Cloud image",
                        _ => "cloud-placeholder",
                    };
                }
                else
                {
                    attribute.Value = MapGuids(attribute.Value, guidMap);
                }
            }
        }

        return prolog + root.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>
    /// Maps every GUID in <paramref name="value"/>, and the two counter segments after a GUID in an object ID
    /// (<c>{GUID}{n}{Ln}</c>), through <paramref name="guidMap"/>, adding new placeholders as needed. Each placeholder
    /// keeps its segment's shape (a GUID; digits of the same length; the same letter), so mapped IDs still match the
    /// XSD's <c>ObjectID</c> pattern, and the same input always maps to the same placeholder.
    /// </summary>
    public static string MapGuids(string value, Dictionary<string, string> guidMap)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(guidMap);
        var ids = ObjectIdTail().Replace(value, m =>
            $"{MapGuid(m.Groups["guid"].Value, guidMap)}{{{MapDigits("n", m.Groups["n"].Value, guidMap)}}}{{{m.Groups["letter"].Value}{MapDigits("d", m.Groups["d"].Value, guidMap)}}}");
        return Guid().Replace(ids, m => m.Value.StartsWith("{C10D0000-", StringComparison.Ordinal) && guidMap.ContainsValue(m.Value) ? m.Value : MapGuid(m.Value, guidMap));
    }

    private static string MapGuid(string guid, Dictionary<string, string> map)
    {
        var key = "guid:" + guid.ToUpperInvariant();
        if (!map.TryGetValue(key, out var mapped))
        {
            mapped = $"{{C10D0000-0000-4000-8000-{map.Keys.Count(k => k.StartsWith("guid:", StringComparison.Ordinal)) + 1:X12}}}";
            map.Add(key, mapped);
        }

        return mapped;
    }

    /// <summary>A digit run of the same length: <c>1</c> then the placeholder's ordinal, zero-padded (a single digit gets the ordinal).</summary>
    private static string MapDigits(string kind, string digits, Dictionary<string, string> map)
    {
        var key = $"{kind}:{digits}";
        if (!map.TryGetValue(key, out var mapped))
        {
            var ordinal = map.Keys.Count(k => k.StartsWith(kind + ":", StringComparison.Ordinal)) + 1;
            mapped = digits.Length == 1
                ? ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "1" + ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(digits.Length - 1, '0');
            map.Add(key, mapped);
        }

        return mapped;
    }

    /// <summary>
    /// Refuses <paramref name="text"/> when it still holds <paramref name="userName"/> (case-insensitive), so a
    /// fixture can never carry the account name. Run on every file just before it's written.
    /// </summary>
    public static void EnsureClean(string text, string userName, string fileName)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (userName.Length >= 3 && text.Contains(userName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Refusing to write {fileName}: it still holds the user name. Extend FixtureSanitiser.");
        }
    }

    private static (string Prolog, XDocument Document) ParseKeepingText(string xml)
    {
        var rootStart = xml.IndexOf("<one:", StringComparison.Ordinal);
        if (rootStart < 0)
        {
            throw new InvalidOperationException("Expected XML with a one: root element.");
        }

        return (xml[..rootStart], XDocument.Parse(xml[rootStart..], LoadOptions.PreserveWhitespace));
    }

    private static void RemoveWithLeadingWhitespace(XElement element)
    {
        if (element.PreviousNode is XText text && string.IsNullOrWhiteSpace(text.Value))
        {
            text.Remove();
        }

        element.Remove();
    }

    [GeneratedRegex(@"(?<name>\b(?:author|authorInitials|authorResolutionID|lastModifiedBy|lastModifiedByInitials|lastModifiedByResolutionID))=""[^""]*""", RegexOptions.CultureInvariant)]
    private static partial Regex AuthorAttribute();

    [GeneratedRegex(@"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}", RegexOptions.CultureInvariant)]
    private static partial Regex Guid();

    [GeneratedRegex(@"^https://d\.docs\.live\.net/[0-9A-Fa-f]+/(?<rest>.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OneDriveUrl();

    [GeneratedRegex(@"(?<guid>\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\})\{(?<n>[0-9]+)\}\{(?<letter>[A-Z])(?<d>[0-9]+)\}", RegexOptions.CultureInvariant)]
    private static partial Regex ObjectIdTail();
}
