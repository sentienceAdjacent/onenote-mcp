using System.Xml.Linq;

namespace OneNoteMcp.Xml;

/// <summary>
/// What <see cref="RestoredPage.Build"/> made: the page XML to send, what it dropped by kind (<c>image</c>,
/// <c>ink</c>, <c>attachment</c>, <c>media</c>, <c>other</c>; counts only), and how many attachments and media files it
/// kept (sent with <c>pathCache</c> alone).
/// </summary>
public sealed record RestoredPageXml(string Xml, IReadOnlyDictionary<string, int> Dropped, int KeptFiles);

/// <summary>
/// <c>restore_from_journal</c>'s page transform (AD-8; Phill, 2026-11-12, 2.11 plan Q2 and Q4). Learned from OneMore's
/// Duplicate Page (which strips every <c>objectID</c> so OneNote makes new ones), not copied from it.
/// </summary>
public static class RestoredPage
{
    /// <summary>An image whose binary isn't inline.</summary>
    public const string ImageKind = "image";

    /// <summary>Ink (<c>InkDrawing</c>, <c>InkWord</c>, <c>InkParagraph</c>) whose binary isn't inline.</summary>
    public const string InkKind = "ink";

    /// <summary>A <c>one:InsertedFile</c> not restored.</summary>
    public const string AttachmentKind = "attachment";

    /// <summary>A <c>one:MediaFile</c> not restored.</summary>
    public const string MediaKind = "media";

    /// <summary>Any other object whose binary is referenced by path or callback, such as an XPS printout, and any <c>one:HTMLBlock</c>.</summary>
    public const string OtherKind = "other";

    private static readonly XName[] TopLevelContent =
    [
        OneNoteXml.One + "Outline",
        OneNoteXml.One + "Image",
        OneNoteXml.One + "InkDrawing",
        OneNoteXml.One + "InsertedFile",
        OneNoteXml.One + "MediaFile",
    ];

    /// <summary>
    /// The page to send, built from <paramref name="snapshotXml"/> (a journal entry's piBinaryData XML): parsed with DTD
    /// processing prohibited (a DOCTYPE throws), the root <c>ID</c> set to <paramref name="newPageId"/>, and every
    /// <c>objectID</c> removed, so OneNote makes new ones. Images and ink keep their inline <c>one:Data</c>.
    /// <list type="bullet">
    /// <item>Each <c>one:InsertedFile</c> and <c>one:MediaFile</c> loses <c>pathSource</c>; it is kept only when
    /// <paramref name="keepFiles"/> is true and <paramref name="keepPathCache"/> accepts its <c>pathCache</c>, else it is
    /// dropped and counted.</item>
    /// <item>Any other object whose binary is named by a path (<c>one:File@path</c>) or a callback (<c>one:CallbackID</c>)
    /// rather than held inline is dropped and counted.</item>
    /// <item>Every <c>one:HTMLBlock</c>, inline or by path, is dropped and counted as <c>other</c> (Phill, 2026-11-13).</item>
    /// </list>
    /// Throws <see cref="OneNoteXmlException"/> when the XML has a DOCTYPE, isn't well-formed, or isn't a <c>one:Page</c>.
    /// The exception's message carries positions and element names only (AD-13).
    /// </summary>
    public static RestoredPageXml Build(string snapshotXml, string newPageId, Func<string, bool> keepPathCache, bool keepFiles = true)
    {
        ArgumentNullException.ThrowIfNull(snapshotXml);
        ArgumentNullException.ThrowIfNull(newPageId);
        ArgumentNullException.ThrowIfNull(keepPathCache);
        if (OneNoteXml.HasDoctype(snapshotXml))
        {
            throw new OneNoteXmlException("The snapshot XML has a DOCTYPE.");
        }

        var document = OneNoteXml.Parse(snapshotXml);
        var root = OneNoteXml.RequireRoot(document, "Page");
        var dropped = new Dictionary<string, int>(StringComparer.Ordinal);
        var kept = 0;

        foreach (var file in root.Descendants().Where(e => e.Name == OneNoteXml.One + "InsertedFile" || e.Name == OneNoteXml.One + "MediaFile").ToList())
        {
            if (file.Parent is null)
            {
                continue; // inside a file already dropped
            }

            file.Attribute("pathSource")?.Remove();
            var pathCache = (string?)file.Attribute("pathCache");
            if (keepFiles && !string.IsNullOrEmpty(pathCache) && keepPathCache(pathCache))
            {
                kept++;
                continue;
            }

            Drop(file, file.Name.LocalName == "MediaFile" ? MediaKind : AttachmentKind, dropped);
        }

        // Phill, 2026-11-13: an HTMLBlock goes whether its HTML is inline or by path. OneNote fetches the local files the
        // HTML names, and a page write refuses it (PageWriteScan.HtmlProblems), so a forged journal entry mustn't carry it.
        foreach (var html in root.Descendants(OneNoteXml.One + "HTMLBlock").ToList())
        {
            if (html.Parent is not null)
            {
                Drop(html, OtherKind, dropped);
            }
        }

        var byReference = root.Descendants()
            .Where(e => e.Name == OneNoteXml.One + "File" || e.Name == OneNoteXml.One + "CallbackID")
            .Select(e => e.Parent)
            .OfType<XElement>()
            .Distinct()
            .ToList();
        foreach (var holder in byReference)
        {
            if (holder.Parent is null || holder == root)
            {
                continue;
            }

            Drop(holder, KindOf(holder), dropped);
        }

        foreach (var withId in root.DescendantsAndSelf().Where(e => e.Attribute("objectID") is not null).ToList())
        {
            withId.Attribute("objectID")!.Remove();
        }

        root.SetAttributeValue("ID", newPageId);
        return new RestoredPageXml(document.Declaration is null ? document.ToString(SaveOptions.DisableFormatting) : document.Declaration + document.ToString(SaveOptions.DisableFormatting), dropped, kept);
    }

    /// <summary>
    /// The <c>objectID</c>s of <paramref name="pageXml"/>'s top-level content objects (<c>Outline</c>, <c>Image</c>,
    /// <c>InkDrawing</c>, <c>InsertedFile</c>, <c>MediaFile</c>): what a new page's template put there (Q4). Objects
    /// without an <c>objectID</c> aren't listed. Throws <see cref="OneNoteXmlException"/> when the XML isn't a
    /// well-formed <c>one:Page</c>.
    /// </summary>
    public static IReadOnlyList<string> TemplateObjectIds(string pageXml) =>
        [.. TopLevel(pageXml).Select(e => (string?)e.Attribute("objectID")).OfType<string>().Where(id => id.Length > 0)];

    /// <summary>
    /// True when <paramref name="pageXml"/> holds any top-level content object (<c>Outline</c>, <c>Image</c>,
    /// <c>InkDrawing</c>, <c>InsertedFile</c>, <c>MediaFile</c>). Throws <see cref="OneNoteXmlException"/> when the XML
    /// isn't a well-formed <c>one:Page</c>.
    /// </summary>
    public static bool HasContent(string pageXml) => TopLevel(pageXml).Any();

    /// <summary>True when <paramref name="pageXml"/> has a <c>one:Title</c>. Throws <see cref="OneNoteXmlException"/> when it isn't a well-formed <c>one:Page</c>.</summary>
    public static bool HasTitle(string pageXml) =>
        OneNoteXml.RequireRoot(OneNoteXml.Parse(pageXml), "Page").Element(OneNoteXml.One + "Title") is not null;

    private static IEnumerable<XElement> TopLevel(string pageXml)
    {
        ArgumentNullException.ThrowIfNull(pageXml);
        var root = OneNoteXml.RequireRoot(OneNoteXml.Parse(pageXml), "Page");
        return [.. root.Elements().Where(e => TopLevelContent.Contains(e.Name))];
    }

    private static void Drop(XElement element, string kind, Dictionary<string, int> dropped)
    {
        element.Remove();
        dropped[kind] = dropped.GetValueOrDefault(kind) + 1;
    }

    private static string KindOf(XElement holder) => holder.Name.LocalName switch
    {
        "Image" => ImageKind,
        "InkDrawing" or "InkWord" or "InkParagraph" => InkKind,
        _ => OtherKind,
    };
}
