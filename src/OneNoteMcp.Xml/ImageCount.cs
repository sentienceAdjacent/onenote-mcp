namespace OneNoteMcp.Xml;

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
}
