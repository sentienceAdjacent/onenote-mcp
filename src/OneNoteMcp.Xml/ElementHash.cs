using System.Security.Cryptography;
using System.Security.Cryptography.Xml;
using System.Xml;
using System.Xml.Linq;

namespace OneNoteMcp.Xml;

/// <summary>
/// AD-17's content hash. Only Xml computes it; callers never do. The hash is lowercase hex SHA-256 over the inclusive
/// C14N (<see cref="XmlDsigC14NTransform"/>, no comments) of a copy of the element's subtree from which, throughout the
/// subtree, the <see cref="ExcludedAttributes"/> and the <see cref="BinaryElements"/> have been removed.
/// </summary>
public static class ElementHash
{
    /// <summary>
    /// Attributes that record when, by whom, what's selected or how it's being viewed, not content. AD-17 names the first
    /// three; Phill's 2026-11-04 decision adds the selection and authorship ones, and his 2026-11-05 decision the view
    /// state (AD-17 amendment routed to <c>bmad-correct-course</c>). Matched by local name, on attributes with no namespace.
    /// </summary>
    public static readonly IReadOnlySet<string> ExcludedAttributes = new HashSet<string>(StringComparer.Ordinal)
    {
        "lastModifiedTime",
        "creationTime",
        "lastModifiedBy",
        "selected",
        "lastModifiedByInitials",
        "lastModifiedByResolutionID",
        "isCurrentlyViewed",
        "isUnread",
    };

    /// <summary>
    /// Binary content: a piBinaryData read inlines <c>one:Data</c> where a piBasic read gives a <c>one:CallbackID</c>,
    /// so both are removed and the two reads of a local page hash the same.
    /// </summary>
    public static readonly IReadOnlySet<XName> BinaryElements = new HashSet<XName>
    {
        OneNoteXml.One + "Data",
        OneNoteXml.One + "CallbackID",
    };

    /// <summary>The hash of <paramref name="element"/>'s subtree, in the context of its document's namespace declarations.</summary>
    public static string Compute(XElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        var copy = new XElement(element);

        // Inclusive C14N renders every namespace in scope, including those declared on ancestors. A detached copy loses
        // them, so redeclare them on the copy's root; the copy's own declarations take precedence.
        foreach (var declaration in element.Ancestors().SelectMany(a => a.Attributes()).Where(a => a.IsNamespaceDeclaration))
        {
            if (copy.Attribute(declaration.Name) is null)
            {
                copy.SetAttributeValue(declaration.Name, declaration.Value);
            }
        }

        foreach (var binary in copy.DescendantsAndSelf().Where(e => BinaryElements.Contains(e.Name)).ToList())
        {
            if (binary != copy)
            {
                binary.Remove();
            }
        }

        foreach (var attribute in copy.DescendantsAndSelf().Attributes().Where(IsExcluded).ToList())
        {
            attribute.Remove();
        }

        var document = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
        document.LoadXml(copy.ToString(SaveOptions.DisableFormatting));

        var transform = new XmlDsigC14NTransform(includeComments: false);
        transform.LoadInput(document);
        using var canonical = (Stream)transform.GetOutput(typeof(Stream));
        return Convert.ToHexStringLower(SHA256.HashData(canonical));
    }

    /// <summary>The hash of the root element of <paramref name="xml"/>. Throws <see cref="OneNoteXmlException"/> on XML that isn't well-formed or whose root isn't in the 2013 namespace.</summary>
    public static string Compute(string xml)
    {
        var root = OneNoteXml.Parse(xml).Root ?? throw new OneNoteXmlException("The XML has no root element.");
        if (root.Name.Namespace != OneNoteXml.One)
        {
            throw new OneNoteXmlException($"Expected a root element in the 2013 namespace ({OneNoteXml.Namespace2013}).");
        }

        return Compute(root);
    }

    private static bool IsExcluded(XAttribute attribute) =>
        !attribute.IsNamespaceDeclaration && attribute.Name.Namespace == XNamespace.None && ExcludedAttributes.Contains(attribute.Name.LocalName);
}
