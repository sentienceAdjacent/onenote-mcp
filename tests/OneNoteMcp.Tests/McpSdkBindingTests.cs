using System.Collections.Concurrent;
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

    private const string InvalidArgumentUserAction =
        "Call again with the arguments the message names corrected: only names the tool declares, spelled exactly as listed, each with a value of the type it expects.";

    private readonly TempDirectory dir = new();
    private readonly FakeGateway gateway = new() { HierarchyXml = NotebooksXml };
    private readonly Pipe clientToServer = new();
    private readonly Pipe serverToClient = new();
    private readonly CapturingLoggerProvider logs = new();
    private IHost? host;
    private McpClient? client;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private McpClient Client => client ?? throw new InvalidOperationException("Not initialised.");

    public async ValueTask InitializeAsync()
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
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
        Assert.Equal(13, actual.Count);
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
    public async Task MisnamedRequiredArgument_IsInvalidArgument_NamingTheMissingAndTheUndeclaredName()
    {
        // Story 2.14: until then the SDK answered with its bare "An error occurred invoking 'get_page_content'.".
        var result = await Client.CallToolAsync("get_page_content", new Dictionary<string, object?> { ["page_id"] = "x" }, cancellationToken: Ct);

        var error = AssertInvalidArgument(result);
        Assert.Equal(
            "get_page_content is missing the required argument \"pageId\" (a string). get_page_content doesn't declare the argument \"page_id\". Its arguments are: pageId, includeBinary.",
            error.GetProperty("message").GetString());
        Assert.Equal(InvalidArgumentUserAction, error.GetProperty("userAction").GetString());
        Assert.Empty(gateway.Calls);

        // One call line with the code; the caller's names and the message are never logged (AD-13).
        var correlationId = result.StructuredContent!.Value.GetProperty("meta").GetProperty("correlationId").GetString();
        var callLines = logs.Messages.Where(m => m.StartsWith("tool call correlationId=", StringComparison.Ordinal)).ToList();
        Assert.Equal(
            [$"tool call correlationId={correlationId} tool=get_page_content result=InvalidArgument hresult=-"],
            callLines.Select(l => l[..l.IndexOf(" ms=", StringComparison.Ordinal)]));
        Assert.DoesNotContain(logs.Messages, m => m.Contains("page_id", StringComparison.Ordinal) || m.Contains("pageId", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Messages, m => m.Contains("missing", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingRequiredArgument_IsInvalidArgument_WithEmptyOrNoArguments(bool sendEmptyArguments)
    {
        var result = await Client.CallToolAsync(
            "get_page_content",
            sendEmptyArguments ? new Dictionary<string, object?>() : null,
            cancellationToken: Ct);

        var error = AssertInvalidArgument(result);
        Assert.Equal(
            "get_page_content is missing the required argument \"pageId\" (a string). Its arguments are: pageId, includeBinary.",
            error.GetProperty("message").GetString());
        Assert.Empty(gateway.Calls);
    }

    [Fact]
    public async Task MisnamedRequiredArgument_WriteTool_IsInvalidArgument_AndNoPageIsCreated()
    {
        var result = await Client.CallToolAsync("create_page", new Dictionary<string, object?> { ["section_id"] = "x" }, cancellationToken: Ct);

        var error = AssertInvalidArgument(result);
        Assert.Equal(
            "create_page is missing the required argument \"sectionId\" (a string). create_page doesn't declare the argument \"section_id\". Its arguments are: sectionId, style.",
            error.GetProperty("message").GetString());
        Assert.False(error.TryGetProperty("outcomeUnknown", out _));
        Assert.Empty(gateway.CreateCalls);
        Assert.Empty(gateway.Calls);
    }

    [Fact]
    public async Task UnknownTool_IsStillAJsonRpcError_OutsideTheEnvelope()
    {
        // AD-9: an unknown tool has no input schema to check, so it stays the SDK's error.
        Assert.Equal("json-rpc InvalidParams Request failed (remote): Unknown tool: 'no_such_tool'", await Outcome("no_such_tool", []));
        Assert.Equal(
            "json-rpc InvalidParams Request failed (remote): Unknown tool: 'no_such_tool'",
            await Outcome("no_such_tool", new Dictionary<string, object?> { ["x"] = 1 }));
        Assert.Empty(gateway.Calls);
    }

    [Fact]
    public async Task EnumValue_OutsideTheEnum_IsInvalidArgument_ListingTheValues_WithoutEchoingIt()
    {
        var result = await Client.CallToolAsync("get_hierarchy", new Dictionary<string, object?> { ["scope"] = "bogus" }, cancellationToken: Ct);

        var error = AssertInvalidArgument(result);
        Assert.Equal(
            "get_hierarchy's argument \"scope\" must be one of: self, children, notebooks, sections, pages (it got a string that isn't one of them). Its arguments are: startNodeId, scope.",
            error.GetProperty("message").GetString());
        Assert.DoesNotContain("bogus", result.StructuredContent!.Value.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(logs.Messages, m => m.Contains("bogus", StringComparison.Ordinal));
        Assert.Empty(gateway.Calls);
    }

    [Fact]
    public async Task EnumValue_ComparesExactly_SoACaseVariantIsInvalidArgument()
    {
        // Strict (story 2.14's assumption): the SDK used to accept "Sections".
        var result = await Client.CallToolAsync("get_hierarchy", new Dictionary<string, object?> { ["scope"] = "Sections" }, cancellationToken: Ct);

        AssertInvalidArgument(result);
        Assert.Empty(gateway.Calls);
    }

    [Fact]
    public async Task NullForAnOptionalStringArgument_IsInvalidArgument()
    {
        // Strict (story 2.14's assumption): the tools used to take null as empty (?? string.Empty).
        var result = await Client.CallToolAsync("get_hierarchy", new Dictionary<string, object?> { ["startNodeId"] = null }, cancellationToken: Ct);

        var error = AssertInvalidArgument(result);
        Assert.Equal(
            "get_hierarchy's argument \"startNodeId\" must be a string (it got null). Its arguments are: startNodeId, scope.",
            error.GetProperty("message").GetString());
        Assert.Empty(gateway.Calls);
    }

    public static TheoryData<string, string, string, string> WrongKinds() => new()
    {
        // tool, argument, JSON value, the message sentence
        { "get_page_content", "pageId", "5", "get_page_content's argument \"pageId\" must be a string (it got a number)." },
        { "get_page_content", "pageId", "null", "get_page_content's argument \"pageId\" must be a string (it got null)." },
        { "get_page_content", "includeBinary", "\"true\"", "get_page_content's argument \"includeBinary\" must be true or false (it got a string)." },
        { "read_result", "page", "1.5", "read_result's argument \"page\" must be an integer or null (it got a number that isn't a 32-bit integer)." },
        { "read_result", "page", "\"5\"", "read_result's argument \"page\" must be an integer or null (it got a string)." },
        { "read_result", "page", "4294967296", "read_result's argument \"page\" must be an integer or null (it got a number that isn't a 32-bit integer)." },
        { "read_result", "run", "[1]", "read_result's argument \"run\" must be an array of strings or nulls, or null (it got an array with an item that is a number)." },
        { "read_result", "run", "\"a\"", "read_result's argument \"run\" must be an array of strings or nulls, or null (it got a string)." },
    };

    [Theory]
    [MemberData(nameof(WrongKinds))]
    public async Task WrongKindOfValue_IsInvalidArgument_NamingTheArgument_WhatItExpects_AndWhatItGot(string tool, string argument, string json, string sentence)
    {
        var arguments = RequiredArguments(tool);
        arguments[argument] = JsonDocument.Parse(json).RootElement.Clone();

        var result = await Client.CallToolAsync(tool, arguments, cancellationToken: Ct);

        var error = AssertInvalidArgument(result);
        Assert.StartsWith(sentence + " Its arguments are: ", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Empty(gateway.Calls);
    }

    [Fact]
    public async Task ValidCalls_AreNeverInvalidArgument_AndANullableArgumentTakesNull()
    {
        var hierarchy = await Client.CallToolAsync("get_hierarchy", new Dictionary<string, object?> { ["scope"] = "sections" }, cancellationToken: Ct);
        var page = await Client.CallToolAsync("get_page_content", new Dictionary<string, object?> { ["pageId"] = "x" }, cancellationToken: Ct);
        var read = await Client.CallToolAsync(
            "read_result",
            new Dictionary<string, object?> { ["resultId"] = "x", ["page"] = null, ["run"] = new string?[] { "a", null } },
            cancellationToken: Ct);

        Assert.NotEqual(true, hierarchy.IsError);
        foreach (var result in new[] { page, read })
        {
            Assert.NotNull(result.StructuredContent);
            if (result.IsError == true)
            {
                Assert.NotEqual("InvalidArgument", result.StructuredContent.Value.GetProperty("error").GetProperty("code").GetString());
            }
        }
    }

    [Fact]
    public async Task ToolsList_EveryInputSchema_DeclaresAdditionalPropertiesFalse()
    {
        var tools = await Client.ListToolsAsync(cancellationToken: Ct);

        Assert.Equal(13, tools.Count);
        foreach (var tool in tools)
        {
            Assert.True(
                tool.ProtocolTool.InputSchema.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind == JsonValueKind.False,
                $"{tool.ProtocolTool.Name}'s input schema doesn't declare additionalProperties: false.");
        }
    }

    [Fact]
    public async Task MisnamedOptionalArgument_ReadTool_IsInvalidArgument_InTheEnvelope_AndNothingRuns()
    {
        // Story 2.5: until then the SDK ignored scop and get_hierarchy ran with its default scope.
        var result = await Client.CallToolAsync("get_hierarchy", new Dictionary<string, object?> { ["scop"] = "sections" }, cancellationToken: Ct);

        var error = AssertInvalidArgument(result);
        Assert.Equal(
            "get_hierarchy doesn't declare the argument \"scop\". Its arguments are: startNodeId, scope.",
            error.GetProperty("message").GetString());
        Assert.Equal(InvalidArgumentUserAction, error.GetProperty("userAction").GetString());
        Assert.Empty(gateway.Calls);

        // One call line with the code; the caller's argument names and the message are never logged (AD-13).
        var correlationId = result.StructuredContent!.Value.GetProperty("meta").GetProperty("correlationId").GetString();
        var callLines = logs.Messages.Where(m => m.StartsWith("tool call correlationId=", StringComparison.Ordinal)).ToList();
        Assert.Equal(
            [$"tool call correlationId={correlationId} tool=get_hierarchy result=InvalidArgument hresult=-"],
            callLines.Select(l => l[..l.IndexOf(" ms=", StringComparison.Ordinal)]));
        Assert.DoesNotContain(logs.Messages, m => m.Replace("scope", string.Empty, StringComparison.Ordinal).Contains("scop", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Messages, m => m.Contains("Its arguments are", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MisnamedOptionalArgument_WriteTool_IsInvalidArgument_AndNoPageIsCreated()
    {
        var result = await Client.CallToolAsync(
            "create_page",
            new Dictionary<string, object?> { ["sectionId"] = "{11111111-2222-3333-4444-555555555555}{1}{B0}", ["Style"] = "blankPageNoTitle" },
            cancellationToken: Ct);

        var error = AssertInvalidArgument(result);
        Assert.Equal(
            "create_page doesn't declare the argument \"Style\". Its arguments are: sectionId, style.",
            error.GetProperty("message").GetString());
        Assert.False(error.TryGetProperty("outcomeUnknown", out _));
        Assert.Empty(gateway.CreateCalls);
        Assert.Empty(gateway.Calls);
    }

    [Fact]
    public async Task SeveralUnknownNames_AreNamedInOneError_SortedOrdinally()
    {
        var result = await Client.CallToolAsync(
            "find_pages",
            new Dictionary<string, object?> { ["query"] = "a", ["zeta"] = 1, ["alpha"] = 2 },
            cancellationToken: Ct);

        var error = AssertInvalidArgument(result);
        Assert.Equal(
            "find_pages doesn't declare the arguments \"alpha\", \"zeta\". Its arguments are: query, startNodeId, fromRoot.",
            error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task AToolWithoutArguments_RefusesAnyName()
    {
        var result = await Client.CallToolAsync("list_grants", new Dictionary<string, object?> { ["x"] = 1 }, cancellationToken: Ct);

        var error = AssertInvalidArgument(result);
        Assert.Equal("list_grants doesn't declare the argument \"x\". It takes no arguments.", error.GetProperty("message").GetString());
    }

    public static TheoryData<string> SnapshotToolNames()
    {
        var data = new TheoryData<string>();
        foreach (var tool in SnapshotTools())
        {
            data.Add(tool!["name"]!.GetValue<string>());
        }

        return data;
    }

    [Fact]
    public void SnapshotToolNames_AreAllThirteenTools()
    {
        Assert.Equal(13, SnapshotToolNames().Count);
    }

    [Theory]
    [MemberData(nameof(SnapshotToolNames))]
    public async Task EveryTool_WithItsRequiredArguments_RefusesAnUndeclaredOne(string name)
    {
        // Proves the call-tool filter checks every tool: without it the call would run, or fail on the "x" IDs.
        var schema = SnapshotTools().Single(t => t!["name"]!.GetValue<string>() == name)!["inputSchema"]!;
        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var required in schema["required"]?.AsArray().Select(n => n!.GetValue<string>()) ?? [])
        {
            Assert.Equal("string", schema["properties"]![required]!["type"]!.GetValue<string>());
            arguments[required] = (name, required) == ("get_special_location", "location") ? "backUpFolder" : "x";
        }

        arguments["bogusArgument"] = true;

        var result = await Client.CallToolAsync(name, arguments, cancellationToken: Ct);

        var error = AssertInvalidArgument(result);
        Assert.StartsWith($"{name} doesn't declare the argument \"bogusArgument\".", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Empty(gateway.Calls);
        Assert.Empty(gateway.CreateCalls);
        Assert.Empty(gateway.NavigateCalls);
    }

    [Fact]
    public async Task SeveralProblems_AreOneMessage_TheInvalidValueThenTheUnknownName()
    {
        // Story 2.14: until then the SDK's own error came first and hid the unknown name.
        var result = await Client.CallToolAsync(
            "get_hierarchy",
            new Dictionary<string, object?> { ["scope"] = "bogus", ["extra"] = 1 },
            cancellationToken: Ct);

        var error = AssertInvalidArgument(result);
        Assert.Equal(
            "get_hierarchy's argument \"scope\" must be one of: self, children, notebooks, sections, pages (it got a string that isn't one of them). "
            + "get_hierarchy doesn't declare the argument \"extra\". Its arguments are: startNodeId, scope.",
            error.GetProperty("message").GetString());
        Assert.Empty(gateway.Calls);
    }

    public static TheoryData<string, string> SnapshotToolProperties()
    {
        var data = new TheoryData<string, string>();
        foreach (var tool in SnapshotTools())
        {
            foreach (var property in tool!["inputSchema"]!["properties"]!.AsObject())
            {
                data.Add(tool["name"]!.GetValue<string>(), property.Key);
            }
        }

        return data;
    }

    [Fact]
    public void SnapshotToolProperties_CoverEveryToolWithArguments()
    {
        // Every tool but list_grants, which takes no arguments; 29 properties in all.
        Assert.Equal(12, SnapshotToolProperties().Select(row => row.Data.Item1).Distinct().Count());
        Assert.Equal(29, SnapshotToolProperties().Count);
    }

    [Theory]
    [MemberData(nameof(SnapshotToolProperties))]
    public async Task EveryTool_EveryProperty_AWrongKindOfValue_IsInvalidArgument_NeverTheSdksGenericError(string name, string property)
    {
        // The coverage proof: no value our schemas refuse reaches the SDK's binder, so its bare "An error occurred
        // invoking" result (no structuredContent) can't come back. A string where the schema takes one is replaced by a
        // number, anything else by a string (the coercions the SDK used to make), and each by an object too.
        var schema = SnapshotTools().Single(t => t!["name"]!.GetValue<string>() == name)!["inputSchema"]!["properties"]![property]!;
        var takesString = schema["type"] is JsonValue type ? type.GetValue<string>() == "string" : schema["type"]!.AsArray().Any(t => t!.GetValue<string>() == "string");
        foreach (var wrong in new[] { takesString ? "5" : "\"5\"", "{}" })
        {
            var arguments = RequiredArguments(name);
            arguments[property] = JsonDocument.Parse(wrong).RootElement.Clone();

            var result = await Client.CallToolAsync(name, arguments, cancellationToken: Ct);

            var error = AssertInvalidArgument(result);
            Assert.StartsWith($"{name}'s argument \"{property}\" must be ", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        }

        Assert.Empty(gateway.Calls);
        Assert.Empty(gateway.CreateCalls);
        Assert.Empty(gateway.NavigateCalls);
    }

    /// <summary>The tool's required arguments with valid values: an enum's first value, else <c>"x"</c>.</summary>
    private static Dictionary<string, object?> RequiredArguments(string tool)
    {
        var schema = SnapshotTools().Single(t => t!["name"]!.GetValue<string>() == tool)!["inputSchema"]!;
        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var required in schema["required"]?.AsArray().Select(n => n!.GetValue<string>()) ?? [])
        {
            arguments[required] = schema["properties"]![required]!["enum"]?.AsArray()[0]!.GetValue<string>() ?? "x";
        }

        return arguments;
    }

    /// <summary>The call failed inside the envelope with <c>InvalidArgument</c>, not retryable, with a correlationId; returns <c>error</c>.</summary>
    private static JsonElement AssertInvalidArgument(CallToolResult result)
    {
        Assert.True(result.IsError);
        Assert.NotNull(result.StructuredContent);
        var envelope = result.StructuredContent.Value;
        Assert.False(envelope.GetProperty("ok").GetBoolean());
        Assert.False(string.IsNullOrEmpty(envelope.GetProperty("meta").GetProperty("correlationId").GetString()));
        var error = envelope.GetProperty("error");
        Assert.Equal("InvalidArgument", error.GetProperty("code").GetString());
        Assert.False(error.GetProperty("retryable").GetBoolean());
        return error;
    }

    private static JsonArray SnapshotTools() =>
        JsonNode.Parse(File.ReadAllText(RepoPaths.Combine("tests", "OneNoteMcp.Tests", "Contract", "tool-contract.json")))!["tools"]!.AsArray();

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

    /// <summary>Every formatted log message from every category, so a test can check what the server logged.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> messages = new();

        public IReadOnlyList<string> Messages => [.. messages];

        public ILogger CreateLogger(string categoryName) => new Logger(messages);

        public void Dispose()
        {
        }

        private sealed class Logger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                messages.Enqueue(formatter(state, exception));
        }
    }
}
