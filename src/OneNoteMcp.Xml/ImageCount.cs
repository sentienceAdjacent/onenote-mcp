using System.Xml.Linq;

namespace OneNoteMcp.Xml;

/// <summary>
/// One binary a page snapshot doesn't hold (AD-8 <c>missingBinary[]</c>). <see cref="Kind"/> is <c>image</c> for an image
/// the piBinaryData read lacks, or <c>attachment</c> for a <c>one:InsertedFile</c>, whose bytes a snapshot never copies
/// (story 2.8 decision Q4). <see cref="ObjectId"/> is the element's own <c>objectID</c>, else its nearest ancestor's;
/// null when neither has one. <see cref="CallbackId"/> is its <c>one:CallbackID@callbackID</c> in the piBasic read, or
/// null.
/// </summary>
public sealed record MissingBinary(string Kind, string? ObjectId, string? CallbackId)
{
    /// <summary>An image with no local binary.</summary>
    public const string ImageKind = "image";

    /// <summary>An inserted file, whose bytes aren't in the page XML.</summary>
    public const string AttachmentKind = "attachment";
}

/// <summary>
/// Counts <c>one:Image</c> elements, for AD-4's degrade check. A cloud page's piBinaryData read drops each image whose
/// binary isn't cached locally (spike 1, findings row 6), so the difference against a piBasic read of the same page is
/// the number of images with no local binary (story 8's <c>meta.binaryUnavailable</c>).
/// </summary>
public static class ImageCount
{
    /// <summary>The number of <c>one:Image</c> elements anywhere in <paramref name="xml"/>.</summary>
    public static int Count(string xml) => OneNoteXml.Parse(xml).Descendants(OneNoteXml.One + "Image").Count();

    /// <summary>Images in the piBasic read that the piBinaryData read of the same page lacks; never negative.</summary>
    public static int Missing(string basicXml, string binaryXml) => Math.Max(0, Count(basicXml) - Count(binaryXml));

    /// <summary>
    /// What a snapshot of the page lacks (AD-8 <c>missingBinary[]</c>), in the piBasic read's document order: each image
    /// the piBinaryData read lacks, and every <c>one:InsertedFile</c> (story 2.8 decision Q4). Images carry no
    /// <c>objectID</c> of their own in OE content, so an image is matched by its own <c>objectID</c>, else its nearest
    /// ancestor's, and counted per ID: when an ID has fewer images in <paramref name="binaryXml"/> than in
    /// <paramref name="basicXml"/>, the last ones under it are the missing ones. Throws <see cref="OneNoteXmlException"/>
    /// when either XML isn't well-formed.
    /// </summary>
    public static IReadOnlyList<MissingBinary> MissingBinaries(string basicXml, string binaryXml)
    {
        var basic = OneNoteXml.Parse(basicXml);
        var binary = OneNoteXml.Parse(binaryXml);

        var present = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var image in binary.Descendants(OneNoteXml.One + "Image"))
        {
            var key = MatchKey(image);
            present[key] = present.GetValueOrDefault(key) + 1;
        }

        var images = basic.Descendants(OneNoteXml.One + "Image").ToList();
        var missingImages = new HashSet<XElement>();
        foreach (var group in images.GroupBy(MatchKey, StringComparer.Ordinal))
        {
            var count = group.Count();
            var lacking = count - Math.Min(count, present.GetValueOrDefault(group.Key));
            foreach (var image in group.Skip(count - lacking))
            {
                missingImages.Add(image);
            }
        }

        var missing = new List<MissingBinary>();
        foreach (var element in basic.Descendants())
        {
            if (element.Name == OneNoteXml.One + "Image" && missingImages.Contains(element))
            {
                missing.Add(From(MissingBinary.ImageKind, element));
            }
            else if (element.Name == OneNoteXml.One + "InsertedFile")
            {
                missing.Add(From(MissingBinary.AttachmentKind, element));
            }
        }

        return missing;
    }

    private static MissingBinary From(string kind, XElement element) =>
        new(kind, ObjectId(element), (string?)element.Element(OneNoteXml.One + "CallbackID")?.Attribute("callbackID"));

    /// <summary>The element's own <c>objectID</c>, else its nearest ancestor's; null when none has one.</summary>
    private static string? ObjectId(XElement element) =>
        element.AncestorsAndSelf().Select(e => (string?)e.Attribute("objectID")).FirstOrDefault(id => !string.IsNullOrEmpty(id));

    private static string MatchKey(XElement image) => ObjectId(image) ?? string.Empty;
}
