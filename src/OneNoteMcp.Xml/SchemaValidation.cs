using System.Xml;
using System.Xml.Schema;

namespace OneNoteMcp.Xml;

/// <summary>
/// Validates XML against the vendored 2013 XSD (AD-16). Reads never call this at runtime: AD-4 returns XML unchanged.
/// It's for tests now, and becomes epic 2's write gate only once partial-update XML is shown to pass.
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
