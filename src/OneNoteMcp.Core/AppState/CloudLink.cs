using System.Globalization;
using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.Writes;
using OneNoteMcp.Xml;

namespace OneNoteMcp.Core.AppState;

/// <summary>What a <c>navigate_to_url</c> link names (story 4.7; spine AD-7 <c>AppState</c>, <c>navigate_to_url</c>).</summary>
public enum CloudLinkKind
{
    /// <summary>A <c>….one</c> with a <c>page-id</c> (an <c>object-id</c> allowed): a page or paragraph link.</summary>
    Page,

    /// <summary>A <c>….one</c> with no <c>page-id</c>.</summary>
    Section,

    /// <summary>The notebook's own folder.</summary>
    Notebook,
}

/// <summary>
/// A <c>onenote:</c> link to a notebook stored online, as step 1 read it: the web address's scheme (<c>http</c> or
/// <c>https</c>, lower case), host and path segments, each percent-decoded once, what it names, the <c>page-id</c> GUID of a
/// page link, and the <c>base-path</c> address (scheme, host and segments) if it carries one.
/// </summary>
public sealed record CloudLink(
    string Scheme,
    string Host,
    IReadOnlyList<string> Segments,
    CloudLinkKind Kind,
    Guid? PageGuid,
    string? BasePathScheme,
    string? BasePathHost,
    IReadOnlyList<string>? BasePathSegments);

/// <summary>
/// What a <see cref="CloudLink"/> names among the open notebooks (step 3): its kind, the notebook's ID, and for a page or
/// section link the section's ID and whether OneNote reports it locked.
/// </summary>
public sealed record CloudLinkTarget(CloudLinkKind Kind, string NotebookId, string? SectionId, bool SectionLocked, Guid? PageGuid);

/// <summary>Why a link was refused: the rule's name, for the log (AD-13), and the error's message and userAction. Neither names the link.</summary>
public sealed record CloudLinkRefusal(string Rule, string Message, string UserAction);

/// <summary>
/// Story 4.7: reads and checks a <c>navigate_to_url</c> link before COM (spine AD-7 <c>AppState</c>, <c>navigate_to_url</c>;
/// Phill, 2026-11-12, epic 4 Q6 a, and 2026-11-22, OQ4 a). Pure: it works on strings and one hierarchy XML, so every rule
/// is unit-tested without OneNote.
/// <list type="bullet">
/// <item><see cref="Parse"/> (step 1): only <c>onenote:</c> links whose target is an <c>http(s)://</c> address. Refused: a
/// web or other link; a local, UNC, <c>///</c> or fragment-only link (a local link's Security Notice can block the call
/// unseen, spike 4.12 N1), whose userAction is to call <c>navigate_to</c> with the object's ID; control or format
/// characters, a backslash, a query, a fragment parameter outside OneNote's own set (a title, <c>section-id</c>,
/// <c>page-id</c>, <c>object-id</c>, <c>base-path</c>, <c>end</c>, a number), and any part still changing after four rounds
/// of percent-decoding or changing again after one, and a title that holds <c>&amp;</c> or <c>#</c>, raw or encoded (OneNote drops both from titles; <c>=</c> is allowed, 2026-11-25 live check; HtmlStopgap's discipline, not the class,
/// which refuses the <c>base-path</c> cloud page links carry).</item>
/// <item><see cref="Match"/> (step 3): the link must lie inside an open notebook's <c>NotebookKey</c> at a segment
/// boundary (scheme equal, so an <c>http://</c> link never matches an <c>https://</c> notebook; host and leading segments
/// equal, ignoring case), the notebook's <c>path</c> read both as reported
/// (unencoded) and as <see cref="UrlPath"/> reads it (AD-21 Web addresses); a <c>base-path</c> must be that same notebook;
/// a <c>….one</c> must be the <c>path</c> of a section in it; a folder must be the notebook itself. A section group's
/// folder is refused with a userAction to call <c>navigate_to</c> with the group's ID (Decision OQ3).</item>
/// </list>
/// </summary>
public static class CloudLinks
{
    /// <summary>The rule names the log line carries (AD-13), one per refusal.</summary>
    public static class Rules
    {
        public const string None = "none";
        public const string Empty = "empty";
        public const string ControlCharacters = "controlCharacters";
        public const string NotOneNote = "notOneNote";
        public const string LocalLink = "localLink";
        public const string Backslash = "backslash";
        public const string Query = "query";
        public const string FragmentParameter = "fragmentParameter";
        public const string Encoding = "encoding";
        public const string NotInOpenNotebook = "notInOpenNotebook";
        public const string BasePath = "basePath";
        public const string NotASection = "notASection";
        public const string SectionGroupFolder = "sectionGroupFolder";
        public const string NotTheNotebook = "notTheNotebook";
    }

    /// <summary>The link kinds the log line carries before a link is read as a page, section or notebook link.</summary>
    public static class Kinds
    {
        public const string Empty = "empty";
        public const string Web = "web";
        public const string Local = "local";
        public const string Other = "other";
        public const string Cloud = "cloud";
    }

    /// <summary>What to do about a local link: show the object by its ID (Decision OQ4; spike 4.12 N1).</summary>
    public const string LocalLinkUserAction =
        "navigate_to_url follows links into notebooks stored online only. To show something in a notebook on this PC, call navigate_to with the object's ID (find it with get_hierarchy or find_pages).";

    /// <summary>What to do about a link that isn't a <c>onenote:</c> link.</summary>
    public const string NotOneNoteUserAction =
        "navigate_to_url follows onenote: links only, as get_hyperlink_to_object returns them; a web link (https://…) is never followed. To show the object, call navigate_to with its ID.";

    /// <summary>What to do about a link the server can't read safely.</summary>
    public const string MalformedUserAction =
        "Pass the onenote: link exactly as get_hyperlink_to_object returned it, or call navigate_to with the object's ID.";

    /// <summary>What to do about a link outside every open notebook: the server never opens one (spike 4.12 N4).</summary>
    public const string NotInOpenNotebookUserAction =
        "navigate_to_url never opens a notebook, so it follows links into notebooks that are open in OneNote only. Check with get_hierarchy (scope notebooks); a closed notebook is reopened by the user in OneNote, then get a fresh link with get_hyperlink_to_object.";

    /// <summary>What to do about a <c>.one</c> that isn't an open section (spike 4.12 N5: OneNote would open it under Open Sections).</summary>
    public const string NotASectionUserAction =
        "The link names a .one that isn't a section of that open notebook (it may have been renamed, moved or deleted). Re-read the hierarchy and get a fresh link with get_hyperlink_to_object, or call navigate_to with the section's or page's ID.";

    /// <summary>What to do about a section group's folder (Decision OQ3).</summary>
    public const string SectionGroupFolderUserAction =
        "The link names a section group's folder, which navigate_to_url doesn't follow. Call navigate_to with the group's ID.";

    /// <summary>What to do about a folder inside a notebook that is neither the notebook nor a section group.</summary>
    public const string NotTheNotebookUserAction =
        "The link names a folder inside an open notebook that is neither the notebook itself nor one of its section groups. Get a fresh link with get_hyperlink_to_object, or call navigate_to with the object's ID.";

    private const string OneNoteScheme = "onenote:";

    private static readonly string[] IdKeys = ["section-id", "page-id", "object-id"];

    /// <summary>The kind of <paramref name="url"/> for the log, before it is read: empty, web, local, cloud or other. Never throws.</summary>
    public static string KindOf(string? url)
    {
        var text = url?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return Kinds.Empty;
        }

        if (IsHttp(text))
        {
            return Kinds.Web;
        }

        if (!text.StartsWith(OneNoteScheme, StringComparison.OrdinalIgnoreCase))
        {
            return Kinds.Other;
        }

        return IsHttp(text[OneNoteScheme.Length..]) ? Kinds.Cloud : Kinds.Local;
    }

    /// <summary>The name a link kind has in the log line.</summary>
    public static string KindName(CloudLinkKind kind) => kind switch
    {
        CloudLinkKind.Page => "page",
        CloudLinkKind.Section => "section",
        _ => "notebook",
    };

    /// <summary>
    /// Step 1: reads <paramref name="url"/> (trimmed) as a cloud <c>onenote:</c> link, or says which rule refuses it. Pure;
    /// never throws.
    /// </summary>
    public static CloudLinkRefusal? Parse(string? url, out CloudLink? link)
    {
        link = null;
        var text = url?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return new(Rules.Empty, "url is empty, so nothing was shown.", MalformedUserAction);
        }

        if (HasControlCharacters(text))
        {
            return new(Rules.ControlCharacters, "url holds control or invisible characters, so nothing was shown.", MalformedUserAction);
        }

        if (!text.StartsWith(OneNoteScheme, StringComparison.OrdinalIgnoreCase))
        {
            return new(Rules.NotOneNote, "url isn't a onenote: link, so nothing was shown: a web link raises a dialog in OneNote that blocks the call.", NotOneNoteUserAction);
        }

        var body = text[OneNoteScheme.Length..];
        if (!IsHttp(body))
        {
            return new(Rules.LocalLink, "url is a link to a notebook on this PC or a network share, not to one stored online, so nothing was shown: such a link can raise a security dialog in OneNote that blocks the call.", LocalLinkUserAction);
        }

        if (body.Contains('\\', StringComparison.Ordinal))
        {
            return Malformed(Rules.Backslash, "holds a backslash");
        }

        var hash = body.IndexOf('#', StringComparison.Ordinal);
        var target = hash < 0 ? body : body[..hash];
        var fragment = hash < 0 ? string.Empty : body[(hash + 1)..];
        if (target.Contains('?', StringComparison.Ordinal))
        {
            return Malformed(Rules.Query, "has a query (?…), which OneNote's own links never carry");
        }

        if (ReadAddress(target, allowTrailingSlash: true, out var host, out var segments) is { } addressRefusal)
        {
            return addressRefusal;
        }

        Guid? pageGuid = null;
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? basePathScheme = null;
        string? basePathHost = null;
        IReadOnlyList<string>? basePathSegments = null;
        var pieces = fragment.Length == 0 ? [] : fragment.Split('&');
        for (var i = 0; i < pieces.Length; i++)
        {
            var piece = pieces[i];
            var equals = piece.IndexOf('=', StringComparison.Ordinal);

            // The title comes first and may hold a raw "=" (OneNote's own links write it so, 2026-11-25 live check): the first
            // part is a parameter only when its key is one of OneNote's.
            var keyed = equals >= 0 && (i > 0 || IsParameterKey(piece[..equals]));
            if (!keyed)
            {
                if (Once(piece) is not { } plain)
                {
                    return Malformed(Rules.Encoding, "has a part that is percent-encoded more than once");
                }

                if (HasControlCharacters(plain) || plain.Contains('\\', StringComparison.Ordinal))
                {
                    return Malformed(plain.Contains('\\', StringComparison.Ordinal) ? Rules.Backslash : Rules.ControlCharacters, "has a part that decodes to a backslash or a control character");
                }

                // No parameter may hide in the decoded title: OneNote may decode the fragment before it splits it, so a title
                // decoding to "&base-path=file:…" could name a local notebook. A title holding "&" or "#" is refused, raw or
                // encoded: OneNote's own links drop both from titles (2026-11-25 live check), so only a crafted link has one.
                // "=" is allowed (Phill, 2026-11-25): with no "&" it can't start a parameter.
                if (plain.IndexOfAny(['&', '#']) >= 0)
                {
                    return Malformed(Rules.FragmentParameter, "has a title that holds & or #, which OneNote's own links never carry and where a parameter could hide");
                }

                // The title comes first; after it only "end" and a number (a paragraph index) carry no "=".
                if (i == 0 || string.Equals(piece, "end", StringComparison.OrdinalIgnoreCase) || IsNumber(piece))
                {
                    continue;
                }

                return Malformed(Rules.FragmentParameter, "has a part after its # that OneNote's own links don't carry");
            }

            var key = piece[..equals];
            var value = piece[(equals + 1)..];
            if (string.Equals(key, "base-path", StringComparison.OrdinalIgnoreCase) && basePathHost is null)
            {
                if (value.Contains('?', StringComparison.Ordinal) || !IsHttp(value))
                {
                    return new(Rules.BasePath, "url's base-path isn't the web address of a notebook stored online, so nothing was shown.", MalformedUserAction);
                }

                if (ReadAddress(value, allowTrailingSlash: true, out var baseHost, out var baseSegments) is { } baseRefusal)
                {
                    return baseRefusal;
                }

                basePathScheme = SchemeOf(value);
                basePathHost = baseHost;
                basePathSegments = baseSegments;
                continue;
            }

            if (!IdKeys.Contains(key, StringComparer.OrdinalIgnoreCase) || !ids.Add(key))
            {
                return Malformed(Rules.FragmentParameter, "has a part after its # that OneNote's own links don't carry, or one twice");
            }

            if (Once(value) is not { } decoded)
            {
                return Malformed(Rules.Encoding, "has a part that is percent-encoded more than once");
            }

            if (Braced(decoded) is not { } guid)
            {
                return Malformed(Rules.FragmentParameter, "has a " + key.ToLowerInvariant() + " that isn't a {GUID}");
            }

            if (string.Equals(key, "page-id", StringComparison.OrdinalIgnoreCase))
            {
                pageGuid = guid;
            }
        }

        var names = segments.Count > 0 && segments[^1].EndsWith(".one", StringComparison.OrdinalIgnoreCase);
        CloudLinkKind kind;
        if (names)
        {
            if (ids.Contains("object-id") && pageGuid is null)
            {
                return Malformed(Rules.FragmentParameter, "has an object-id but no page-id");
            }

            kind = pageGuid is null ? CloudLinkKind.Section : CloudLinkKind.Page;
        }
        else
        {
            if (ids.Count > 0)
            {
                return Malformed(Rules.FragmentParameter, "names a folder but carries a section, page or object ID");
            }

            kind = CloudLinkKind.Notebook;
        }

        link = new CloudLink(SchemeOf(target), host, segments, kind, pageGuid, basePathScheme, basePathHost, basePathSegments);
        return null;
    }

    /// <summary>
    /// Step 3: finds what <paramref name="link"/> names among the open notebooks in <paramref name="hierarchyXml"/> (one
    /// <c>GetHierarchy</c> of all notebooks at <c>Sections</c>), or says which rule refuses it. Throws
    /// <see cref="ErrorCode.InvalidXml"/> when the XML can't be read.
    /// </summary>
    public static CloudLinkRefusal? Match(CloudLink link, string hierarchyXml, out CloudLinkTarget? target)
    {
        ArgumentNullException.ThrowIfNull(link);
        target = null;
        var trees = XmlReads.Readable(() => HierarchyNode.NotebookTrees(hierarchyXml), "GetHierarchy");

        // The open notebook whose address the link lies inside, at a segment boundary; the longest if several do.
        NotebookTree? notebook = null;
        var depth = 0;
        foreach (var tree in trees)
        {
            foreach (var form in Forms(tree.Notebook.Path))
            {
                if (form.Segments.Count > depth && Inside(link.Scheme, link.Host, link.Segments, form))
                {
                    notebook = tree;
                    depth = form.Segments.Count;
                }
            }
        }

        if (notebook is null)
        {
            return new(Rules.NotInOpenNotebook, "url doesn't lead into a notebook that is open in OneNote, so nothing was shown.", NotInOpenNotebookUserAction);
        }

        if (link.BasePathHost is { } baseHost
            && !Forms(notebook.Notebook.Path).Any(f => SameAddress(link.BasePathScheme!, baseHost, link.BasePathSegments!, f)))
        {
            return new(Rules.BasePath, "url's base-path isn't the notebook its address leads into, so nothing was shown.", MalformedUserAction);
        }

        if (link.Kind == CloudLinkKind.Notebook)
        {
            if (link.Segments.Count == depth)
            {
                target = new CloudLinkTarget(CloudLinkKind.Notebook, notebook.Notebook.Id, null, false, null);
                return null;
            }

            var group = notebook.SectionGroups.FirstOrDefault(g => Forms(g.Path).Any(f => SameAddress(link.Scheme, link.Host, link.Segments, f)));
            return group is not null
                ? new(Rules.SectionGroupFolder, $"url names the folder of the section group {group.Id}, not a notebook, section or page, so nothing was shown.", SectionGroupFolderUserAction)
                : new(Rules.NotTheNotebook, "url names a folder inside an open notebook that isn't the notebook itself, so nothing was shown.", NotTheNotebookUserAction);
        }

        var section = notebook.Sections.FirstOrDefault(s => Forms(s.Path).Any(f => SameAddress(link.Scheme, link.Host, link.Segments, f)));
        if (section is null)
        {
            return new(Rules.NotASection, "url names a .one that isn't a section of the open notebook its address leads into, so nothing was shown.", NotASectionUserAction);
        }

        target = new CloudLinkTarget(link.Kind, notebook.Notebook.Id, section.Id, section.Locked, link.PageGuid);
        return null;
    }

    /// <summary>
    /// The <c>page-id</c> GUID of any <c>onenote:</c> link (a local one included), as <c>GetHyperlinkToObject</c> returns it,
    /// in its <c>{…}</c> or <c>%7B…%7D</c> form; null when it carries none or it isn't a GUID. For the landing check.
    /// </summary>
    public static Guid? PageGuidOf(string? link)
    {
        var hash = link?.IndexOf('#', StringComparison.Ordinal) ?? -1;
        if (hash < 0)
        {
            return null;
        }

        foreach (var piece in link![(hash + 1)..].Split('&'))
        {
            if (piece.StartsWith("page-id=", StringComparison.OrdinalIgnoreCase))
            {
                return Once(piece["page-id=".Length..]) is { } value ? Braced(value) : null;
            }
        }

        return null;
    }

    /// <summary>
    /// Reads an <c>http(s)://</c> address with no query or fragment: its host, and its path segments decoded once. Refuses an
    /// empty or encoded host, a part encoded more than once (or still changing after four rounds), a segment that decodes to
    /// a <c>/</c>, a backslash, a control character, <c>.</c> or <c>..</c>, and an empty segment except a trailing <c>/</c>.
    /// </summary>
    private static CloudLinkRefusal? ReadAddress(string address, bool allowTrailingSlash, out string host, out IReadOnlyList<string> segments)
    {
        host = string.Empty;
        segments = [];
        var rest = address[(address.IndexOf("://", StringComparison.Ordinal) + 3)..];
        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        host = slash < 0 ? rest : rest[..slash];
        if (host.Length == 0 || host.IndexOfAny(['%', '@']) >= 0)
        {
            return Malformed(Rules.Encoding, "has a web address whose host is empty, encoded or carries a user name");
        }

        if (slash < 0)
        {
            return null;
        }

        var raw = rest[(slash + 1)..].Split('/');
        var decoded = new List<string>(raw.Length);
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i].Length == 0)
            {
                if (i == raw.Length - 1 && allowTrailingSlash)
                {
                    continue;
                }

                return Malformed(Rules.Encoding, "has an empty part in its web address (//)");
            }

            if (Once(raw[i]) is not { } segment)
            {
                return Malformed(Rules.Encoding, "has a part that is percent-encoded more than once");
            }

            if (segment.Contains('\\', StringComparison.Ordinal))
            {
                return Malformed(Rules.Backslash, "has a part that decodes to a backslash");
            }

            if (HasControlCharacters(segment))
            {
                return Malformed(Rules.ControlCharacters, "has a part that decodes to a control character");
            }

            if (segment.Contains('/', StringComparison.Ordinal) || segment is "." or "..")
            {
                return Malformed(Rules.Encoding, "has a part that decodes to a / or is . or ..");
            }

            decoded.Add(segment);
        }

        segments = decoded;
        return null;
    }

    /// <summary>The text decoded once, or null when that isn't where four rounds of decoding settle (HtmlStopgap's discipline).</summary>
    private static string? Once(string text)
    {
        var once = Unescape(text);
        var settled = once;
        for (var i = 0; i < 4; i++)
        {
            var next = Unescape(settled);
            if (next == settled)
            {
                return settled == once ? once : null;
            }

            settled = next;
        }

        return null;
    }

    private static string Unescape(string text)
    {
        try
        {
            return Uri.UnescapeDataString(text);
        }
        catch (UriFormatException)
        {
            return text;
        }
    }

    /// <summary>A <c>{GUID}</c> (braces required), or null.</summary>
    private static Guid? Braced(string value) =>
        value.Length == 38 && value[0] == '{' && value[^1] == '}' && Guid.TryParseExact(value, "B", out var guid) ? guid : null;

    /// <summary>A fragment key OneNote's own links carry: <c>section-id</c>, <c>page-id</c>, <c>object-id</c> or <c>base-path</c>.</summary>
    private static bool IsParameterKey(string key) =>
        IdKeys.Contains(key, StringComparer.OrdinalIgnoreCase) || string.Equals(key, "base-path", StringComparison.OrdinalIgnoreCase);

    private static bool IsNumber(string piece) =>
        piece.Length is > 0 and <= 8 && piece.All(char.IsAsciiHexDigit);

    /// <summary>The scheme of an <c>http(s)://</c> address, lower case: <c>https</c> or <c>http</c>.</summary>
    private static string SchemeOf(string address) =>
        address.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? "https" : "http";

    private static bool IsHttp(string text) =>
        text.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

    private static bool HasControlCharacters(string text) =>
        text.Any(c => char.IsControl(c) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format);

    private static CloudLinkRefusal Malformed(string rule, string what) =>
        new(rule, $"url {what}, so nothing was shown.", MalformedUserAction);

    /// <summary>
    /// The ways a <c>path</c> OneNote reports for a notebook, section or section group stored online can be read (AD-21 Web
    /// addresses): its scheme and host with each of <see cref="UrlPath.Readings"/>, the as-reported (unencoded) and the
    /// decoded segments among them. None for a local path.
    /// </summary>
    private static IEnumerable<(string Scheme, string Host, IReadOnlyList<string> Segments)> Forms(string path)
    {
        var key = GrantKeys.NotebookKey(path);
        if (!UrlPath.TryParse(key, out var host, out _))
        {
            yield break;
        }

        foreach (var reading in UrlPath.Readings(key))
        {
            yield return (SchemeOf(key), host, reading);
        }
    }

    // The scheme must match too (2026-11-23 security review): an http:// link to an https:// notebook would send OneNote
    // to an unencrypted address that only looks like the open notebook's.
    private static bool Inside(string scheme, string host, IReadOnlyList<string> segments, (string Scheme, string Host, IReadOnlyList<string> Segments) form) =>
        form.Segments.Count > 0
        && string.Equals(scheme, form.Scheme, StringComparison.Ordinal)
        && string.Equals(host, form.Host, StringComparison.OrdinalIgnoreCase)
        && segments.Count >= form.Segments.Count
        && form.Segments.Select((s, i) => string.Equals(s, segments[i], StringComparison.OrdinalIgnoreCase)).All(same => same);

    private static bool SameAddress(string scheme, string host, IReadOnlyList<string> segments, (string Scheme, string Host, IReadOnlyList<string> Segments) form) =>
        segments.Count == form.Segments.Count && Inside(scheme, host, segments, form);
}
