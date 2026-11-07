using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>
/// AD-9's text content block: a success with a body (<c>xml</c>, or <c>meta.binary</c>) is one line of compact JSON,
/// the envelope without the body, then <c>\n</c> and the body unchanged; anything else is the whole envelope JSON.
/// </summary>
internal static class TextBlockAssert
{
    /// <summary>Asserts the single text block matches structuredContent by AD-9's rule, and returns the text.</summary>
    public static string Matches(CallToolResult result)
    {
        Assert.NotNull(result.StructuredContent);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        var raw = result.StructuredContent.Value.GetRawText();
        var envelope = JsonNode.Parse(raw)!.AsObject();

        var ok = envelope["ok"]!.GetValue<bool>();
        var body = !ok ? null : envelope["xml"]?.GetValue<string>() ?? envelope["meta"]?["binary"]?.GetValue<string>();
        if (body is null)
        {
            Assert.Equal(raw, text);
            return text;
        }

        var newline = text.IndexOf('\n', StringComparison.Ordinal);
        Assert.True(newline > 0, "A text block with a body starts with a header line.");
        Assert.Equal(body, text[(newline + 1)..]);

        var expectedHeader = envelope.DeepClone().AsObject();
        expectedHeader.Remove("xml");
        expectedHeader["meta"]!.AsObject().Remove("binary");
        Assert.True(
            JsonNode.DeepEquals(expectedHeader, JsonNode.Parse(text[..newline])),
            $"The header line should be the envelope without its body.\nheader: {text[..newline]}");
        return text;
    }

    /// <summary>The header line of a text block with a body, parsed.</summary>
    public static JsonObject Header(CallToolResult result)
    {
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        var newline = text.IndexOf('\n', StringComparison.Ordinal);
        return JsonNode.Parse(newline < 0 ? text : text[..newline])!.AsObject();
    }
}
