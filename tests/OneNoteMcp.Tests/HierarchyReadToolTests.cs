using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using OneNoteMcp.Core;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Server;
using OneNoteMcp.Server.Tools;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>
/// Story 7's I/O matrix: every hierarchy read tool through tool, use case and the fixture-backed <see cref="FakeGateway"/>,
/// with XML and scalars compared byte for byte against the captured fixtures (AD-4).
/// </summary>
public sealed class HierarchyReadToolTests
{
    public const string NotebookId = "{23721455-FC92-4E64-B884-2F75D1649A2C}{1}{B0}";
    public const string SectionId = "{A20A4FC1-E5F2-4154-ADC8-8A513656DFEE}{1}{B0}";
    public const string PageId = "{A20A4FC1-E5F2-4154-ADC8-8A513656DFEE}{1}{E19499018700455935598720161835466737046045861}";
    public const string ProbePageId = "{A20A4FC1-E5F2-4154-ADC8-8A513656DFEE}{1}{E19516938799045219860520175612925891145658211}";
    public const string ProbeOeId = "{BF3D1BA7-E142-41A6-9B2B-F860B51A5615}{34}{B0}";
    public const string UnknownId = "{00000000-0000-0000-0000-000000000000}{1}{B0}";

    public const string LockedA = "{11111111-0000-0000-0000-000000000001}{1}{B0}";
    public const string LockedB = "{11111111-0000-0000-0000-000000000003}{1}{B0}";

    /// <summary>Hand-built: two locked sections (one inside a section group) and one that isn't.</summary>
    public const string LockedXml =
        "<?xml version=\"1.0\"?>\n<one:Notebook xmlns:one=\"http://schemas.microsoft.com/office/onenote/2013/onenote\" name=\"N\" ID=\"{11111111-0000-0000-0000-000000000000}{1}{B0}\">"
        + "<one:Section name=\"Secret\" ID=\"" + LockedA + "\" locked=\"true\" />"
        + "<one:Section name=\"Open\" ID=\"{11111111-0000-0000-0000-000000000002}{1}{B0}\"><one:Page ID=\"{p}\" name=\"Visible\" /></one:Section>"
        + "<one:SectionGroup name=\"G\" ID=\"{11111111-0000-0000-0000-00000000000A}{1}{B0}\">"
        + "<one:Section name=\"Also secret\" ID=\"" + LockedB + "\" locked=\"true\" />"
        + "</one:SectionGroup>"
        + "</one:Notebook>";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // --- get_hierarchy --------------------------------------------------------------------------------------------

    public static TheoryData<HierarchyScopeArgument, HierarchyScope, string> NotebookScopes() => new()
    {
        { HierarchyScopeArgument.Self, HierarchyScope.Self, "mcp-test/hierarchy/notebook-Self.xml" },
        { HierarchyScopeArgument.Children, HierarchyScope.Children, "mcp-test/hierarchy/notebook-Children.xml" },
        { HierarchyScopeArgument.Notebooks, HierarchyScope.Notebooks, "mcp-test/hierarchy/notebook-Notebooks.xml" },
        { HierarchyScopeArgument.Sections, HierarchyScope.Sections, "mcp-test/hierarchy/notebook-Sections.xml" },
        { HierarchyScopeArgument.Pages, HierarchyScope.Pages, "mcp-test/hierarchy/notebook-Pages.xml" },
    };

    [Theory]
    [MemberData(nameof(NotebookScopes))]
    public async Task GetHierarchy_EachScope_ReturnsTheFixtureUnchanged_WithNoWarnings(HierarchyScopeArgument scope, HierarchyScope coreScope, string file)
    {
        var gateway = new FakeGateway();

        var root = Ok(await new GetHierarchyTool(Runner, new GetHierarchyUseCase(gateway)).GetHierarchy(NotebookId, scope, Ct));

        Assert.Equal(FakeGateway.ReadFixture(file), root.GetProperty("xml").GetString());
        Assert.Equal(0, root.GetProperty("warnings").GetArrayLength());
        Assert.Equal([(NotebookId, coreScope)], gateway.Calls);
        AssertMetaHasOnly(root, "correlationId");
    }

    [Fact]
    public async Task GetHierarchy_WithoutScope_IsNotebooksScope_AsBeforeThisStory()
    {
        var gateway = new FakeGateway();

        var root = Ok(await new GetHierarchyTool(Runner, new GetHierarchyUseCase(gateway)).GetHierarchy(cancellationToken: Ct));

        Assert.Equal(FakeGateway.ReadFixture("mcp-test/hierarchy/root-notebooks.xml"), root.GetProperty("xml").GetString());
        Assert.Equal([(string.Empty, HierarchyScope.Notebooks)], gateway.Calls);
    }

    [Fact]
    public async Task GetHierarchy_LockedSections_XmlUnchanged_OneWarningEach_NamingOnlyTheId()
    {
        var gateway = new FakeGateway { HierarchyXml = LockedXml };

        var root = Ok(await new GetHierarchyTool(Runner, new GetHierarchyUseCase(gateway)).GetHierarchy(NotebookId, HierarchyScopeArgument.Pages, Ct));

        Assert.Equal(LockedXml, root.GetProperty("xml").GetString());
        var warnings = root.GetProperty("warnings").EnumerateArray().ToList();
        Assert.Equal(2, warnings.Count);
        foreach (var (warning, id) in warnings.Zip([LockedA, LockedB]))
        {
            Assert.Equal(ToolWarnings.SectionLocked, warning.GetProperty("code").GetString());
            var message = warning.GetProperty("message").GetString()!;
            Assert.Contains(id, message, StringComparison.Ordinal);
            Assert.DoesNotContain("secret", message, StringComparison.OrdinalIgnoreCase); // no section names (AD-9)
            Assert.EndsWith(ToolErrors.AddInAccessOptionNote, warning.GetProperty("userAction").GetString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task GetHierarchy_MalformedXml_IsInvalidXml()
    {
        var gateway = new FakeGateway { HierarchyXml = "<one:Notebook" };

        var error = Error(await new GetHierarchyTool(Runner, new GetHierarchyUseCase(gateway)).GetHierarchy(cancellationToken: Ct));

        Assert.Equal("InvalidXml", error.GetProperty("code").GetString());
        Assert.False(error.GetProperty("retryable").GetBoolean());
    }

    // --- get_hierarchy_parent -------------------------------------------------------------------------------------

    [Theory]
    [InlineData(PageId, "mcp-test/parent/page.txt", SectionId)]
    [InlineData(SectionId, "mcp-test/parent/section.txt", NotebookId)]
    public async Task GetHierarchyParent_ReturnsTheParentId(string objectId, string file, string expectedParent)
    {
        var root = Ok(await ParentTool().GetHierarchyParent(objectId, Ct));

        Assert.Equal(FakeGateway.ReadFixture(file), Meta(root, "parentId"));
        Assert.Equal(expectedParent, Meta(root, "parentId"));
        Assert.False(root.TryGetProperty("xml", out _));
        Assert.Equal(0, root.GetProperty("warnings").GetArrayLength());
    }

    [Fact]
    public async Task GetHierarchyParent_OfNotebook_IsOk_WithEmptyParentIdEmitted()
    {
        var result = await ParentTool().GetHierarchyParent(NotebookId, Ct);
        var root = Ok(result);

        Assert.Equal(FakeGateway.ReadFixture("mcp-test/parent/notebook.txt"), Meta(root, "parentId"));
        Assert.Equal(string.Empty, Meta(root, "parentId"));
        Assert.Contains("\"parentId\":\"\"", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text, StringComparison.Ordinal);
    }

    // --- get_special_location -------------------------------------------------------------------------------------

    [Theory]
    [InlineData(SpecialLocationArgument.BackUpFolder, "mcp-test/special/BackUpFolder.txt")]
    [InlineData(SpecialLocationArgument.UnfiledNotesSection, "mcp-test/special/UnfiledNotesSection.txt")]
    [InlineData(SpecialLocationArgument.DefaultNotebookFolder, "mcp-test/special/DefaultNotebookFolder.txt")]
    public async Task GetSpecialLocation_ReturnsThePath(SpecialLocationArgument location, string file)
    {
        var root = Ok(await new GetSpecialLocationTool(Runner, new GetSpecialLocationUseCase(new FakeGateway())).GetSpecialLocation(location, Ct));

        Assert.Equal(FakeGateway.ReadFixture(file), Meta(root, "path"));
        AssertMetaHasOnly(root, "correlationId", "path");
    }

    // --- get_hyperlink_to_object / get_web_hyperlink_to_object ----------------------------------------------------

    [Fact]
    public async Task GetHyperlinkToObject_Section_PageContentObjectIdOmitted()
    {
        var root = Ok(await LinkTool().GetHyperlinkToObject(SectionId, cancellationToken: Ct));

        Assert.Equal(FakeGateway.ReadFixture("mcp-test/link/section.txt"), Meta(root, "link"));
        Assert.StartsWith("onenote:", Meta(root, "link"), StringComparison.Ordinal);
        AssertMetaHasOnly(root, "correlationId", "link");
    }

    [Fact]
    public async Task GetHyperlinkToObject_PageAndOe()
    {
        var root = Ok(await LinkTool().GetHyperlinkToObject(ProbePageId, ProbeOeId, Ct));

        Assert.Equal(FakeGateway.ReadFixture("mcp-test/link/page-object.txt"), Meta(root, "link"));
        Assert.Equal(0, root.GetProperty("warnings").GetArrayLength());
    }

    [Fact]
    public async Task GetWebHyperlinkToObject_LocalNotebook_IsOk_EmptyLink_WithWarning()
    {
        var root = Ok(await WebLinkTool().GetWebHyperlinkToObject(PageId, cancellationToken: Ct));

        Assert.Equal(FakeGateway.ReadFixture("mcp-test/link/web-page.txt"), Meta(root, "link"));
        Assert.Equal(string.Empty, Meta(root, "link"));
        var warning = Assert.Single(root.GetProperty("warnings").EnumerateArray());
        Assert.Equal(ToolWarnings.WebLinkUnavailable, warning.GetProperty("code").GetString());
    }

    [Fact]
    public async Task GetWebHyperlinkToObject_CloudLink_IsReturned_WithoutWarning()
    {
        var gateway = new ScalarGateway("https://example.invalid/notebook");

        var root = Ok(await new GetWebHyperlinkToObjectTool(Runner, new GetWebHyperlinkToObjectUseCase(gateway)).GetWebHyperlinkToObject(PageId, cancellationToken: Ct));

        Assert.Equal("https://example.invalid/notebook", Meta(root, "link"));
        Assert.Equal(0, root.GetProperty("warnings").GetArrayLength());
    }

    // --- Unknown ID and IDs passing through -----------------------------------------------------------------------

    public static TheoryData<string> Tools() => ["get_hierarchy", "get_hierarchy_parent", "get_hyperlink_to_object", "get_web_hyperlink_to_object"];

    [Theory]
    [MemberData(nameof(Tools))]
    public async Task UnknownId_IsObjectNotFound(string tool)
    {
        var error = Error(await CallWithId(tool, new FakeGateway(), UnknownId));

        Assert.Equal("ObjectNotFound", error.GetProperty("code").GetString());
        Assert.Equal("0x80042014", error.GetProperty("hresult").GetString());
    }

    [Fact]
    public async Task GetSpecialLocation_GatewayFailure_IsTheEnvelope()
    {
        var gateway = new FakeGateway { Failure = new OneNoteException(ErrorCode.OneNoteNotRunning, "m") };

        var error = Error(await new GetSpecialLocationTool(Runner, new GetSpecialLocationUseCase(gateway)).GetSpecialLocation(SpecialLocationArgument.BackUpFolder, Ct));

        Assert.Equal("OneNoteNotRunning", error.GetProperty("code").GetString());
    }

    [Theory]
    [MemberData(nameof(Tools))]
    public async Task Ids_ReachTheGateway_Unchanged(string tool)
    {
        const string odd = "  {weird}{1}{B0} ";
        var gateway = new ScalarGateway(string.Empty);

        await CallWithId(tool, gateway, odd, pageContentObjectId: " {oe} ");

        Assert.Equal(odd, gateway.LastId);
        if (tool.Contains("hyperlink", StringComparison.Ordinal))
        {
            Assert.Equal(" {oe} ", gateway.LastPageContentObjectId);
        }
    }

    [Fact]
    public void EveryUseCase_IsReadClass()
    {
        var gateway = new FakeGateway();
        IUseCase[] useCases =
        [
            new GetHierarchyUseCase(gateway),
            new GetHierarchyParentUseCase(gateway),
            new GetSpecialLocationUseCase(gateway),
            new GetHyperlinkToObjectUseCase(gateway),
            new GetWebHyperlinkToObjectUseCase(gateway),
        ];

        Assert.All(useCases, u => Assert.Equal(OperationClass.Read, u.Class));
    }

    [Theory]
    [InlineData("\"self\"", HierarchyScopeArgument.Self)]
    [InlineData("\"children\"", HierarchyScopeArgument.Children)]
    [InlineData("\"notebooks\"", HierarchyScopeArgument.Notebooks)]
    [InlineData("\"sections\"", HierarchyScopeArgument.Sections)]
    [InlineData("\"pages\"", HierarchyScopeArgument.Pages)]
    public void Scope_WireNames(string json, HierarchyScopeArgument expected)
    {
        Assert.Equal(expected, JsonSerializer.Deserialize<HierarchyScopeArgument>(json, EnvelopeJson.Options));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("\"3\"")]
    [InlineData("99")]
    public void WireEnums_RejectIntegerValues(string json)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<HierarchyScopeArgument>(json, EnvelopeJson.Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<SpecialLocationArgument>(json, EnvelopeJson.Options));
    }

    // --- Helpers --------------------------------------------------------------------------------------------------

    private static ToolRunner Runner => new(NullLogger<ToolRunner>.Instance);

    private static GetHierarchyParentTool ParentTool() => new(Runner, new GetHierarchyParentUseCase(new FakeGateway()));

    private static GetHyperlinkToObjectTool LinkTool() => new(Runner, new GetHyperlinkToObjectUseCase(new FakeGateway()));

    private static GetWebHyperlinkToObjectTool WebLinkTool() => new(Runner, new GetWebHyperlinkToObjectUseCase(new FakeGateway()));

    private static Task<CallToolResult> CallWithId(string tool, IOneNoteGateway gateway, string id, string pageContentObjectId = "") => tool switch
    {
        "get_hierarchy" => new GetHierarchyTool(Runner, new GetHierarchyUseCase(gateway)).GetHierarchy(id, HierarchyScopeArgument.Self, Ct),
        "get_hierarchy_parent" => new GetHierarchyParentTool(Runner, new GetHierarchyParentUseCase(gateway)).GetHierarchyParent(id, Ct),
        "get_hyperlink_to_object" => new GetHyperlinkToObjectTool(Runner, new GetHyperlinkToObjectUseCase(gateway)).GetHyperlinkToObject(id, pageContentObjectId, Ct),
        "get_web_hyperlink_to_object" => new GetWebHyperlinkToObjectTool(Runner, new GetWebHyperlinkToObjectUseCase(gateway)).GetWebHyperlinkToObject(id, pageContentObjectId, Ct),
        _ => throw new ArgumentOutOfRangeException(nameof(tool)),
    };

    private static JsonElement Ok(CallToolResult result)
    {
        Assert.False(result.IsError);
        Assert.NotNull(result.StructuredContent);
        var root = result.StructuredContent.Value;
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.False(root.TryGetProperty("error", out _));
        Assert.False(string.IsNullOrEmpty(Meta(root, "correlationId")));
        TextBlockAssert.Matches(result);
        return root;
    }

    private static JsonElement Error(CallToolResult result)
    {
        Assert.True(result.IsError);
        Assert.NotNull(result.StructuredContent);
        var root = result.StructuredContent.Value;
        Assert.False(root.GetProperty("ok").GetBoolean());
        AssertMetaHasOnly(root, "correlationId");
        return root.GetProperty("error");
    }

    private static string? Meta(JsonElement root, string name) => root.GetProperty("meta").GetProperty(name).GetString();

    private static void AssertMetaHasOnly(JsonElement root, params string[] names) =>
        Assert.Equal(names, root.GetProperty("meta").EnumerateObject().Select(p => p.Name));

    /// <summary>Answers every scalar call with one value and records the IDs it was given, for pass-through checks.</summary>
    private sealed class ScalarGateway(string value) : IOneNoteGateway
    {
        public string? LastId { get; private set; }

        public string? LastPageContentObjectId { get; private set; }

        public Task<string> GetHierarchyAsync(string startNodeId, HierarchyScope scope, CancellationToken ct)
        {
            LastId = startNodeId;
            return Task.FromResult("<one:Notebooks xmlns:one=\"http://schemas.microsoft.com/office/onenote/2013/onenote\" />");
        }

        public Task<string> GetHierarchyParentAsync(string objectId, CancellationToken ct)
        {
            LastId = objectId;
            return Task.FromResult(value);
        }

        public Task<string> GetHyperlinkToObjectAsync(string hierarchyId, string pageContentObjectId, CancellationToken ct)
        {
            (LastId, LastPageContentObjectId) = (hierarchyId, pageContentObjectId);
            return Task.FromResult(value);
        }

        public Task<string> GetWebHyperlinkToObjectAsync(string hierarchyId, string pageContentObjectId, CancellationToken ct)
        {
            (LastId, LastPageContentObjectId) = (hierarchyId, pageContentObjectId);
            return Task.FromResult(value);
        }

        public Task<string> GetSpecialLocationAsync(SpecialLocation location, CancellationToken ct) => Task.FromResult(value);

        public Task NavigateToAsync(string hierarchyId, string objectId, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> CreateNewPageAsync(string sectionId, NewPageStyle style, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> GetPageContentAsync(string pageId, PageInfo pageInfo, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> GetBinaryPageContentAsync(string pageId, string callbackId, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> FindPagesAsync(string startNodeId, string query, bool includeUnindexed, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> FindMetaAsync(string startNodeId, string name, bool includeUnindexed, CancellationToken ct) => throw new NotSupportedException();
    }
}
