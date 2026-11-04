using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using OneNoteMcp.Server;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>
/// AD-14: tool names, parameter schemas, output schemas and annotations are a public contract. The snapshot is
/// <c>Contract/tool-contract.json</c>; <c>description</c> keys are stripped so wording can change freely. On a
/// deliberate change, bump <c>&lt;Version&gt;</c> by AD-14's rules and paste the printed JSON into the snapshot.
/// </summary>
public sealed class ToolContractTests
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static string SnapshotPath => RepoPaths.Combine("tests", "OneNoteMcp.Tests", "Contract", "tool-contract.json");

    [Fact]
    public void Tools_MatchTheContractSnapshot()
    {
        using var services = BuildServer();
        var expected = JsonNode.Parse(File.ReadAllText(SnapshotPath))!;
        var actual = BuildSnapshot(services, expected["contractVersion"]!.GetValue<string>());

        Assert.True(
            JsonNode.DeepEquals(expected, actual),
            $"The tool contract changed. If that is deliberate, bump <Version> per AD-14 and replace {SnapshotPath} with:\n{actual.ToJsonString(Indented)}");
    }

    [Fact]
    public void ServerInfo_NamesTheServer_AndItsMajorMinorIsTheContractVersion()
    {
        using var services = BuildServer();
        var info = services.GetRequiredService<IOptions<McpServerOptions>>().Value.ServerInfo;
        var contractVersion = JsonNode.Parse(File.ReadAllText(SnapshotPath))!["contractVersion"]!.GetValue<string>();

        Assert.NotNull(info);
        Assert.Equal("OneNoteMcp", info.Name);
        var parts = info.Version.Split('.', '-');
        Assert.Equal(contractVersion, $"{parts[0]}.{parts[1]}");
    }

    [Fact]
    public void BothEnvelopeShapes_CarryEveryPropertyTheOutputSchemaRequires()
    {
        // Nulls are omitted on the wire, so a schema property marked required but null would make clients that
        // validate structuredContent reject the result.
        using var services = BuildServer();
        var schema = JsonNode.Parse(services.GetServices<McpServerTool>().Single().ProtocolTool.OutputSchema!.Value.GetRawText())!;
        var meta = new Core.Results.ToolMeta("c");
        var success = ToolRunner.ToResult(ToolEnvelope.Success(Core.Results.ToolSuccess.WithXml("<x/>", meta)));
        var failure = ToolRunner.ToResult(ToolEnvelope.Failure(
            Core.Results.ToolErrors.From(new Core.OneNoteException(Core.ErrorCode.OneNoteNotRunning, "m"), Core.OperationClass.Read),
            meta));

        AssertRequiredPresent(schema, JsonNode.Parse(success.StructuredContent!.Value.GetRawText())!);
        var failureNode = JsonNode.Parse(failure.StructuredContent!.Value.GetRawText())!;
        AssertRequiredPresent(schema, failureNode);
        AssertRequiredPresent(schema["properties"]!["error"]!, failureNode["error"]!);
    }

    private static void AssertRequiredPresent(JsonNode schema, JsonNode payload)
    {
        foreach (var name in schema["required"]!.AsArray().Select(n => n!.GetValue<string>()))
        {
            Assert.True(payload.AsObject().ContainsKey(name), $"Required property '{name}' is missing from {payload.ToJsonString()}");
        }
    }

    private static ServiceProvider BuildServer()
    {
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddOneNoteMcp();
        return collection.BuildServiceProvider();
    }

    private static JsonObject BuildSnapshot(IServiceProvider services, string contractVersion)
    {
        var tools = new JsonArray();
        foreach (var tool in services.GetServices<McpServerTool>().Select(t => t.ProtocolTool).OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            tools.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["inputSchema"] = StripDescriptions(JsonNode.Parse(tool.InputSchema.GetRawText())),
                ["outputSchema"] = tool.OutputSchema is { } output ? StripDescriptions(JsonNode.Parse(output.GetRawText())) : null,
                ["annotations"] = StripDescriptions(JsonSerializer.SerializeToNode(tool.Annotations, McpJsonUtilities.DefaultOptions)),
            });
        }

        return new JsonObject { ["contractVersion"] = contractVersion, ["tools"] = tools };
    }

    // Strips "description" only as schema metadata. Keys directly under a "properties" object are names of
    // parameters or fields, so a property called "description" stays in the comparison.
    private static JsonNode? StripDescriptions(JsonNode? node, bool isPropertiesMap = false)
    {
        switch (node)
        {
            case JsonObject obj:
                if (!isPropertiesMap)
                {
                    obj.Remove("description");
                }

                foreach (var (key, child) in obj.ToList())
                {
                    StripDescriptions(child, isPropertiesMap: !isPropertiesMap && key == "properties");
                }

                break;
            case JsonArray array:
                foreach (var child in array)
                {
                    StripDescriptions(child);
                }

                break;
        }

        return node;
    }
}
