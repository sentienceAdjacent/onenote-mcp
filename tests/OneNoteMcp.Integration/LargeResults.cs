using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OneNoteMcp.Core.LargeResults;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Server;
using OneNoteMcp.Server.Tools;
using Xunit;

namespace OneNoteMcp.Integration;

/// <summary>
/// A runner with its own result store, and the <c>read_result</c> tool over that store, so a test can read back a
/// result that was too large to return whole (AD-20). MCP Test can hold story 17's Ballast fixture, whose pages-scope
/// hierarchy and oversized page are over the default inline threshold.
/// </summary>
internal sealed class LargeResults
{
    private readonly ResultStore store = ResultStore.WithDefaults();
    private readonly ReadResultTool readTool;

    public LargeResults(ILogger<ToolRunner>? logger = null)
    {
        Runner = new ToolRunner(logger ?? NullLogger<ToolRunner>.Instance, new ResultDelivery(store, EnvelopeMeasure.Instance));
        readTool = new ReadResultTool(Runner, new ReadResultUseCase(store, EnvelopeMeasure.Instance));
    }

    public ToolRunner Runner { get; }

    /// <summary>The envelope's <c>xml</c>, or, for a stored result, the whole body read back by offset reads of <c>/</c>.</summary>
    public async Task<string> XmlAsync(JsonElement root, CancellationToken ct)
    {
        if (root.TryGetProperty("xml", out var xml))
        {
            return xml.GetString()!;
        }

        var result = root.GetProperty("meta").GetProperty("result");
        var id = result.GetProperty("resultId").GetString()!;
        var total = result.GetProperty("totalChars").GetInt32();
        var body = new StringBuilder(total);
        while (body.Length < total)
        {
            var part = await readTool.ReadResult(id, "/", offset: body.Length, cancellationToken: ct);
            Assert.False(part.IsError, part.StructuredContent?.GetRawText());
            body.Append(part.StructuredContent!.Value.GetProperty("xml").GetString());
        }

        return body.ToString();
    }
}
