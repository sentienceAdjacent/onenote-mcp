using System.Globalization;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace StressHarness;

/// <summary>
/// The cap probe's server: one stdio MCP tool, <c>filler</c>, returning exactly N characters of numbered filler lines
/// ending in an end marker, so the probe can tell from what the model received whether it was whole or cut, and where.
/// Two fillers with different characters-per-token ratios (<c>words</c>, <c>xml</c>) separate a token cap from a
/// character cap. The <c>structured</c> variant returns the filler the way the real server does: the envelope
/// <c>{ok, xml, meta, warnings}</c> as structuredContent and its JSON as the text block, so the probe can tell whether
/// structuredContent counts toward the cap. The <c>contract-no-readonly</c> variant advertises the nine contract names without readOnlyHint, to test the
/// harness's startup check. No notebook is involved.
/// </summary>
internal static class FakeServer
{
    public const string EndMarker = "END-OF-FILLER";

    public static async Task<int> RunAsync(int chars, string variant, string kind, IReadOnlyList<string> contractNames)
    {
        var filler = Filler(chars, kind);
        CallToolResult result;
        if (variant == "structured")
        {
            // Same shape and serialisation as the server's ToolRunner.ToResult: Web defaults, nulls omitted.
            var envelope = JsonSerializer.SerializeToElement(
                new { ok = true, xml = filler, meta = new { correlationId = new string('0', 32) }, warnings = Array.Empty<object>() },
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            result = new CallToolResult { StructuredContent = envelope, Content = [new TextContentBlock { Text = envelope.GetRawText() }] };
        }
        else
        {
            result = new CallToolResult { Content = [new TextContentBlock { Text = filler }] };
        }

        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "cap-probe", Version = "1.0.0" },
            Handlers = new McpServerHandlers
            {
                ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult
                {
                    Tools = variant != "contract-no-readonly"
                        ? [Tool("filler", readOnly: true)]
                        : [.. contractNames.Select(n => Tool(n, readOnly: false))],
                }),
                CallToolHandler = (_, _) => ValueTask.FromResult(result),
            },
        };

        await using var server = McpServer.Create(new StdioServerTransport("cap-probe"), options);
        await server.RunAsync();
        return Exit.Ok;
    }

    /// <summary>
    /// Lines of exactly 100 characters ("L000001 " plus words or XML-like tags, then "\n"); the last line is cut to fit
    /// and the string always ends with <see cref="EndMarker"/>. Total length is exactly <paramref name="chars"/>
    /// (13, the marker's length, or more; Options refuses less).
    /// </summary>
    public static string Filler(int chars, string kind = "words")
    {
        var Words = kind == "xml"
            ? "<one:OE><one:T><![CDATA[x]]></one:T></one:OE><one:OE><one:T><![CDATA[y]]></one:T></one:OE><one:OE><one:T>"
            : "alpha bravo charlie delta echo foxtrot golf hotel india juliet kilo lima mike november oscar papa quebec romeo ";
        var body = Math.Max(0, chars - EndMarker.Length);
        var sb = new StringBuilder(chars);
        var line = 1;
        while (sb.Length < body)
        {
            var prefix = "L" + line.ToString("D6", CultureInfo.InvariantCulture) + " ";
            var text = prefix + Words[..(99 - prefix.Length)] + "\n";
            sb.Append(text, 0, Math.Min(text.Length, body - sb.Length));
            line++;
        }

        sb.Append(EndMarker);
        return sb.ToString();
    }

    private static Tool Tool(string name, bool readOnly) => new()
    {
        Name = name,
        Description = "Returns filler text for the output-cap probe.",
        InputSchema = JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone(),
        Annotations = readOnly ? new ToolAnnotations { ReadOnlyHint = true } : null,
    };
}
