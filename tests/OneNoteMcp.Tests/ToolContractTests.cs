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
/// AD-14: tool names, parameter schemas, output schemas, annotations and _meta (AD-20) are a public contract. The snapshot is
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
        var meta = new Core.Results.ToolMeta("c");
        var success = ToolRunner.ToResult(ToolEnvelope.Success(Core.Results.ToolSuccess.WithXml("<x/>", meta)));
        var scalar = ToolRunner.ToResult(ToolEnvelope.Success(new Core.Results.ToolSuccess(null, meta with { Link = string.Empty }, [])));
        var failure = ToolRunner.ToResult(ToolEnvelope.Failure(
            Core.Results.ToolErrors.From(new Core.OneNoteException(Core.ErrorCode.OneNoteNotRunning, "m"), Core.OperationClass.Read),
            meta));

        var tools = services.GetServices<McpServerTool>().ToList();
        Assert.NotEmpty(tools);
        foreach (var tool in tools)
        {
            var schema = JsonNode.Parse(tool.ProtocolTool.OutputSchema!.Value.GetRawText())!;
            foreach (var result in new[] { success, scalar })
            {
                var node = JsonNode.Parse(result.StructuredContent!.Value.GetRawText())!;
                AssertRequiredPresent(schema, node);
                AssertRequiredPresent(schema["properties"]!["meta"]!, node["meta"]!);
            }

            var failureNode = JsonNode.Parse(failure.StructuredContent!.Value.GetRawText())!;
            AssertRequiredPresent(schema, failureNode);
            AssertRequiredPresent(schema["properties"]!["meta"]!, failureNode["meta"]!);
            AssertRequiredPresent(schema["properties"]!["error"]!, failureNode["error"]!);
        }
    }

    [Fact]
    public void Server_ListsTheReadTools_NavigateTo_ListGrants_AndTheWriteTools()
    {
        using var services = BuildServer();

        Assert.Equal(
            ["create_notebook", "create_page", "create_section", "create_section_group", "delete_page_content", "find_meta", "find_pages", "get_binary_page_content", "get_hierarchy", "get_hierarchy_parent", "get_hyperlink_to_object", "get_page_content", "get_special_location", "get_web_hyperlink_to_object", "list_grants", "navigate_to", "read_result", "restore_from_journal", "update_page_content"],
            services.GetServices<McpServerTool>().Select(t => t.ProtocolTool.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void EveryTool_DeclaresClaudeCodesResultSizeCeiling_AsTheWholeNumber500000()
    {
        // AD-20. McpMeta takes a double; the wire must still carry 500000, not 500000.0 or 5E+05.
        using var services = BuildServer();

        foreach (var tool in services.GetServices<McpServerTool>().Select(t => t.ProtocolTool))
        {
            var json = JsonSerializer.Serialize(tool, McpJsonUtilities.DefaultOptions);
            Assert.Contains("\"_meta\":{\"anthropic/maxResultSizeChars\":500000}", json, StringComparison.Ordinal);
        }
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
            tools.Add(SnapshotOf(tool));
        }

        return new JsonObject { ["contractVersion"] = contractVersion, ["tools"] = tools };
    }

    /// <summary>One tool as the snapshot records it: descriptions stripped. Shared with the SDK test's wire <c>tools/list</c>.</summary>
    internal static JsonObject SnapshotOf(ModelContextProtocol.Protocol.Tool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        return new JsonObject
        {
            ["name"] = tool.Name,
            ["inputSchema"] = StripDescriptions(JsonNode.Parse(tool.InputSchema.GetRawText())),
            ["outputSchema"] = tool.OutputSchema is { } output ? StripDescriptions(JsonNode.Parse(output.GetRawText())) : null,
            ["annotations"] = StripDescriptions(JsonSerializer.SerializeToNode(tool.Annotations, McpJsonUtilities.DefaultOptions)),
            ["_meta"] = tool.Meta is { } meta ? JsonNode.Parse(meta.ToJsonString(McpJsonUtilities.DefaultOptions)) : null,
        };
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
