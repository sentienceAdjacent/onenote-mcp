using System.IO.Pipelines;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OneNoteMcp.Core;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Server;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>
/// Real <c>tools/list</c> and <c>tools/call</c> requests through the MCP SDK, client and server in this process over
/// pipes, against <see cref="ServerSetup.AddOneNoteMcp"/> with <see cref="FakeGateway"/>. Covers what direct tool calls
/// can't: argument binding from JSON, and the listed tools being the contract snapshot's.
/// </summary>
public sealed class McpSdkBindingTests : IAsyncLifetime
{
    private const string NotebooksXml =
        "<?xml version=\"1.0\"?>\n<one:Notebooks xmlns:one=\"http://schemas.microsoft.com/office/onenote/2013/onenote\">"
        + "<one:Notebook name=\"MCP Test\" ID=\"{1}{1}{B0}\" path=\"C:\\notebooks\\MCP Test\" lastModifiedTime=\"2026-11-03T00:00:00.000Z\" />"
        + "</one:Notebooks>";

    private readonly TempDirectory dir = new();
    private readonly FakeGateway gateway = new() { HierarchyXml = NotebooksXml };
    private readonly Pipe clientToServer = new();
    private readonly Pipe serverToClient = new();
    private IHost? host;
    private McpClient? client;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private McpClient Client => client ?? throw new InvalidOperationException("Not initialised.");

    public async ValueTask InitializeAsync()
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Logging.ClearProviders();
        builder.Services
            .AddOneNoteMcp()
            .WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());

        // The real registration, with the gateway and settings.json swapped for test ones (the last registration wins).
        builder.Services.AddSingleton<IOneNoteGateway>(gateway);
        builder.Services.AddSingleton(new SettingsStore(dir.Combine("settings.json")));

        host = builder.Build();
        await host.StartAsync(Ct);

        client = await McpClient.CreateAsync(
            new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream()),
            cancellationToken: Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (client is not null)
        {
            await client.DisposeAsync();
        }

        if (host is not null)
        {
            await host.StopAsync(CancellationToken.None);
            host.Dispose();
        }

        dir.Dispose();
    }

    [Fact]
    public async Task ToolsList_OnTheWire_MatchesTheContractSnapshot_ToolByTool()
    {
        var tools = await Client.ListToolsAsync(cancellationToken: Ct);

        // Each wire tool as the snapshot records it (name, inputSchema, outputSchema, annotations, _meta; descriptions
        // stripped), so a schema the SDK serialises differently from the registration fails here too.
        var snapshot = JsonNode.Parse(await File.ReadAllTextAsync(RepoPaths.Combine("tests", "OneNoteMcp.Tests", "Contract", "tool-contract.json"), Ct))!;
        var expected = snapshot["tools"]!.AsArray();
        var actual = tools.Select(t => t.ProtocolTool).OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
        Assert.Equal(12, actual.Count);
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < actual.Count; i++)
        {
            Assert.False(string.IsNullOrWhiteSpace(actual[i].Description), $"{actual[i].Name} has no description on the wire.");
            var wire = ToolContractTests.SnapshotOf(actual[i]);
            Assert.True(
                JsonNode.DeepEquals(expected[i], wire),
                $"tools/list differs from the snapshot for {actual[i].Name}:\n{wire.ToJsonString()}\nexpected:\n{expected[i]!.ToJsonString()}");
        }
    }

    [Fact]
    public async Task GetHierarchy_BindsItsArgumentsFromJson_AndReturnsTheOkEnvelope()
    {
        var result = await Client.CallToolAsync("get_hierarchy", new Dictionary<string, object?> { ["scope"] = "notebooks" }, cancellationToken: Ct);

        Assert.NotEqual(true, result.IsError);
        Assert.NotNull(result.StructuredContent);
        var envelope = result.StructuredContent.Value;
        Assert.True(envelope.GetProperty("ok").GetBoolean());
        Assert.Equal(NotebooksXml, envelope.GetProperty("xml").GetString());
        Assert.Equal([(string.Empty, HierarchyScope.Notebooks)], gateway.Calls);
    }

    [Fact]
    public async Task GetHierarchy_ScopeBindsFromItsWireName()
    {
        await Client.CallToolAsync("get_hierarchy", new Dictionary<string, object?> { ["scope"] = "sections" }, cancellationToken: Ct);

        Assert.Equal([(string.Empty, HierarchyScope.Sections)], gateway.Calls);
    }

    [Fact]
    public async Task MisnamedArgument_FailsLikeAMissingOne_InTheSdk_BeforeTheToolRuns()
    {
        // Argument names are case-sensitive: page_id doesn't bind to pageId. The SDK rejects both calls before the tool
        // runs, as an isError result with its own text and no envelope, so neither becomes our Internal (AD-9).
        var missing = await Outcome("get_page_content", []);
        var misnamed = await Outcome("get_page_content", new Dictionary<string, object?> { ["page_id"] = "x" });

        Assert.Equal(missing, misnamed);
        Assert.Equal("result isError=True [{\"type\":\"text\",\"text\":\"An error occurred invoking \\u0027get_page_content\\u0027.\"}] structuredContent=none", misnamed);
    }

    [Fact]
    public async Task UnknownTool_IsAJsonRpcError_AndAnUnbindableArgument_AnSdkErrorResult_BothOutsideTheEnvelope()
    {
        // AD-9: errors the SDK raises before a tool runs never carry the envelope.
        Assert.Equal("json-rpc InvalidParams Request failed (remote): Unknown tool: 'no_such_tool'", await Outcome("no_such_tool", []));
        Assert.Equal(
            "result isError=True [{\"type\":\"text\",\"text\":\"An error occurred invoking \\u0027get_hierarchy\\u0027.\"}] structuredContent=none",
            await Outcome("get_hierarchy", new Dictionary<string, object?> { ["scope"] = "bogus" }));
        Assert.Empty(gateway.Calls);
    }

    /// <summary>What the client sees, whether the SDK answers with a JSON-RPC error or a tool result.</summary>
    private async Task<string> Outcome(string tool, Dictionary<string, object?> arguments)
    {
        try
        {
            var result = await Client.CallToolAsync(tool, arguments, cancellationToken: Ct);
            return "result isError=" + result.IsError + " " + JsonSerializer.Serialize(result.Content, McpJsonUtilities.DefaultOptions)
                + " structuredContent=" + (result.StructuredContent?.GetRawText() ?? "none");
        }
        catch (McpProtocolException ex)
        {
            return "json-rpc " + ex.ErrorCode + " " + ex.Message;
        }
    }
}
