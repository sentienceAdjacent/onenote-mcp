using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace OneNoteMcp.Xml;

/// <summary>
/// Validates XML against the vendored 2013 XSD (AD-16). Reads never call this at runtime: AD-4 returns XML unchanged.
/// <see cref="Problems"/> is the hard gate of a page write's step 4 (AD-7; Phill, 2026-11-12, 2.9 plan Q3), which the
/// partial-update tests showed accepts partial-update XML; <see cref="Validate"/> keeps the validator's text, for tests.
/// </summary>
public static class SchemaValidation
{
    /// <summary>The embedded resource name of <c>Schema/OneNoteApplication_2013.xsd</c>.</summary>
    public const string XsdResourceName = "OneNoteMcp.Xml.OneNoteApplication_2013.xsd";

    private static readonly Lazy<XmlSchemaSet> Schemas = new(LoadSchemas, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The vendored XSD's bytes, unchanged.</summary>
    public static byte[] XsdBytes()
    {
        using var stream = OpenXsd();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary>
    /// Every schema error and warning in <paramref name="xml"/>, each with its line and position; empty when it's valid.
    /// XML that isn't well-formed, or whose root isn't in the 2013 namespace, gives one error saying so.
    /// Schema messages come from the .NET validator and can quote content (an invalid attribute value, for example):
    /// never log them (AD-13).
    /// </summary>
    public static IReadOnlyList<string> Validate(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        var errors = new List<string>();
        var settings = new XmlReaderSettings
        {
            ValidationType = ValidationType.Schema,
            Schemas = Schemas.Value,
            ValidationFlags = XmlSchemaValidationFlags.ReportValidationWarnings,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
        };
        settings.ValidationEventHandler += (_, e) =>
            errors.Add($"{(e.Severity == XmlSeverityType.Error ? "error" : "warning")} (line {e.Exception.LineNumber}, position {e.Exception.LinePosition}): {e.Message}");

        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            var rootChecked = false;
            while (reader.Read())
            {
                if (!rootChecked && reader.NodeType == XmlNodeType.Element)
                {
                    rootChecked = true;
                    if (reader.NamespaceURI != OneNoteXml.Namespace2013)
                    {
                        return [$"error (line 1): the root element is not in the 2013 namespace ({OneNoteXml.Namespace2013})."];
                    }
                }
            }

            if (!rootChecked)
            {
                errors.Add("error: the XML has no root element.");
            }
        }
        catch (XmlException ex)
        {
            errors.Add($"error (line {ex.LineNumber}, position {ex.LinePosition}): the XML is not well-formed.");
        }

        return errors;
    }

    /// <summary>
    /// The content-free form of <see cref="Validate"/>, for the write gate (Q3): every schema error and warning in
    /// <paramref name="xml"/> as its line, position and the local name of the element it concerns, never the
    /// validator's text, which can quote content (AD-9, AD-13). XML that isn't well-formed gives one problem with its
    /// position and no element; a root that isn't in the 2013 namespace gives one problem naming the root. Empty when
    /// the XML is valid.
    /// </summary>
    public static IReadOnlyList<SchemaProblem> Problems(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        XDocument document;
        try
        {
            document = XDocument.Parse(xml, LoadOptions.SetLineInfo);
        }
        catch (XmlException ex)
        {
            return [new SchemaProblem(ex.LineNumber, ex.LinePosition, null)];
        }

        if (document.Root is not { } root)
        {
            return [new SchemaProblem(0, 0, null)];
        }

        if (root.Name.NamespaceName != OneNoteXml.Namespace2013)
        {
            return [At(root)];
        }

        var problems = new List<SchemaProblem>();
        document.Validate(Schemas.Value, (sender, _) =>
        {
            var element = sender switch
            {
                XElement e => e,
                XAttribute a => a.Parent,
                _ => null,
            };
            var (line, position) = sender is IXmlLineInfo info && info.HasLineInfo() ? (info.LineNumber, info.LinePosition) : (0, 0);
            problems.Add(new SchemaProblem(line, position, element?.Name.LocalName, element is null || element.Name.Namespace == OneNoteXml.One));
        });
        return problems;
    }

    private static SchemaProblem At(XElement element) =>
        element is IXmlLineInfo info && info.HasLineInfo()
            ? new SchemaProblem(info.LineNumber, info.LinePosition, element.Name.LocalName, In2013: false)
            : new SchemaProblem(0, 0, element.Name.LocalName, In2013: false);

    private static Stream OpenXsd() =>
        typeof(SchemaValidation).Assembly.GetManifestResourceStream(XsdResourceName)
        ?? throw new InvalidOperationException($"The embedded XSD {XsdResourceName} is missing.");

    private static XmlSchemaSet LoadSchemas()
    {
        using var stream = OpenXsd();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var set = new XmlSchemaSet { XmlResolver = null };
        set.Add(OneNoteXml.Namespace2013, reader);
        set.Compile();
        return set;
    }
}

/// <summary>
/// One schema problem as the write gate reports it (<see cref="SchemaValidation.Problems"/>): where it is and the local
/// name of the element it concerns (null when the XML isn't well-formed). Never the validator's text (AD-9, AD-13).
/// </summary>
/// <param name="Line">1-based line, or 0 when unknown.</param>
/// <param name="Position">1-based position on the line, or 0 when unknown.</param>
/// <param name="Element">The element's local name, such as <c>OE</c>; null when there is none.</param>
/// <param name="In2013">False when the element isn't in the OneNote 2013 namespace.</param>
public sealed record SchemaProblem(int Line, int Position, string? Element, bool In2013 = true)
{
    /// <summary>
    /// The element as a message names it: <c>one:OE</c> in the 2013 namespace, otherwise the bare local name followed by
    /// " (not in the OneNote 2013 namespace)", never the namespace itself; null when there is no element.
    /// </summary>
    public string? DisplayName => Element is null ? null : In2013 ? "one:" + Element : Element + " (not in the OneNote 2013 namespace)";
}
