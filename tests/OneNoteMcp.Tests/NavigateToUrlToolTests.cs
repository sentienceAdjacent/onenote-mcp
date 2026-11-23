using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OneNoteMcp.Core;
using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.AppState;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Server;
using OneNoteMcp.Server.Tools;
using Xunit;
using K = OneNoteMcp.Tests.CloudLinkKit;

namespace OneNoteMcp.Tests;

/// <summary>
/// Story 4.7's <c>navigate_to_url</c> rows through tool, use case, the <c>AppState</c> pipeline and <see cref="FakeGateway"/>,
/// which records every <c>NavigateToUrl</c>: the I/O matrix, the landing check, and that no refusal sends anything. Nothing
/// here reaches real OneNote.
/// </summary>
public sealed class NavigateToUrlToolTests : IDisposable
{
    private readonly TempDirectory temp = new();
    private readonly List<NavigateToUrlLogEntry> logged = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => temp.Dispose();

    // --- The landing check --------------------------------------------------------------------------------------

    [Fact]
    public async Task ACloudPageLink_ThatLands_IsOk_Landed_WithTheWindow()
    {
        var gateway = Gateway();
        OnPage(gateway, K.PageId, K.PageLink());

        var root = Ok(await Tool(gateway).NavigateToUrl(K.PageLink(), cancellationToken: Ct));

        Assert.Equal([K.PageLink()], gateway.NavigateUrlCalls);
        var warning = Assert.Single(root.GetProperty("warnings").EnumerateArray());
        Assert.Equal(ToolWarnings.Landed, warning.GetProperty("code").GetString());
        var message = warning.GetProperty("message").GetString()!;
        Assert.Contains("OneNote's current page is " + K.PageId, message, StringComparison.Ordinal);
        Assert.DoesNotContain("OneNote shows", message, StringComparison.Ordinal);
        var window = Assert.Single(root.GetProperty("meta").GetProperty("windows").EnumerateArray());
        Assert.Equal(0x10, window.GetProperty("handle").GetInt32());
        Assert.Equal(K.NotebookId, window.GetProperty("notebookId").GetString());
        Assert.Equal(K.SectionId, window.GetProperty("sectionId").GetString());
        Assert.Equal(K.PageId, window.GetProperty("pageId").GetString());
        Assert.False(root.TryGetProperty("xml", out _));
        Assert.Equal(new NavigateToUrlLogEntry(CorrelationIdOf(root), "page", CloudLinks.Rules.None, ToolWarnings.Landed), Assert.Single(logged));
    }

    [Fact]
    public async Task TheLink_IsSentTrimmed_AndTheHierarchyIsReadOnceAtSections()
    {
        var gateway = Gateway();
        OnPage(gateway, K.PageId, K.PageLink());

        Ok(await Tool(gateway).NavigateToUrl("  " + K.PageLink() + "\n", cancellationToken: Ct));

        Assert.Equal([K.PageLink()], gateway.NavigateUrlCalls);
        Assert.Equal(1, gateway.Calls.Count(c => c == (string.Empty, HierarchyScope.Sections)));
    }

    [Fact]
    public async Task AMissingPage_LandsElsewhere_AndTheCallStaysOk()
    {
        // Spike 4.12 N3: a link to a missing page returned ok and landed on another page.
        var gateway = Gateway();
        OnPage(gateway, K.OtherPageId, K.PageLink(pageGuid: K.OtherPageGuid));

        var root = Ok(await Tool(gateway).NavigateToUrl(K.PageLink(), cancellationToken: Ct));

        var warning = Assert.Single(root.GetProperty("warnings").EnumerateArray());
        Assert.Equal(ToolWarnings.LandedElsewhere, warning.GetProperty("code").GetString());
        Assert.Contains("OneNote's current page is " + K.OtherPageId, warning.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(NavigateToUrlUseCase.LandedElsewhereUserAction, warning.GetProperty("userAction").GetString());
        Assert.Equal(ToolWarnings.LandedElsewhere, Assert.Single(logged).Outcome);
    }

    [Fact]
    public async Task ALockedSectionsPage_LandsOnTheSectionWithNoPage_AndSaysALockedSectionShowsNoPage()
    {
        // Spike 4.12 N6.
        var gateway = Gateway();
        gateway.Windows.Add(new OneNoteWindow(0x10, true, true, K.NotebookId, string.Empty, K.LockedSectionId, string.Empty, false, -1, false));

        var root = Ok(await Tool(gateway).NavigateToUrl(K.PageLink("Vault.one"), cancellationToken: Ct));

        var warning = Assert.Single(root.GetProperty("warnings").EnumerateArray());
        Assert.Equal(ToolWarnings.LandedElsewhere, warning.GetProperty("code").GetString());
        var message = warning.GetProperty("message").GetString()!;
        Assert.Contains("OneNote's current page is none", message, StringComparison.Ordinal);
        Assert.Contains("a locked section shows no page", message, StringComparison.Ordinal);
        Assert.Equal(NavigateToUrlUseCase.LockedUserAction, warning.GetProperty("userAction").GetString());
        var window = Assert.Single(root.GetProperty("meta").GetProperty("windows").EnumerateArray());
        Assert.False(window.TryGetProperty("pageId", out _));
        Assert.Equal(K.LockedSectionId, window.GetProperty("sectionId").GetString());
    }

    [Fact]
    public async Task APageLink_WithNoCurrentPage_LandsElsewhere()
    {
        var gateway = Gateway();
        gateway.Windows.Add(new OneNoteWindow(0x10, true, true, K.NotebookId, string.Empty, K.SectionId, string.Empty, false, -1, false));

        var root = Ok(await Tool(gateway).NavigateToUrl(K.PageLink(), cancellationToken: Ct));

        var warning = Assert.Single(root.GetProperty("warnings").EnumerateArray());
        Assert.Equal(ToolWarnings.LandedElsewhere, warning.GetProperty("code").GetString());
        Assert.Equal(NavigateToUrlUseCase.LandedElsewhereUserAction, warning.GetProperty("userAction").GetString());
    }

    [Theory]
    [InlineData(K.SectionId, ToolWarnings.Landed)]
    [InlineData(K.InnerSectionId, ToolWarnings.LandedElsewhere)]
    public async Task ASectionLink_IsComparedByTheSectionsId(string currentSection, string code)
    {
        var gateway = Gateway();
        gateway.Windows.Add(new OneNoteWindow(0x10, true, true, K.NotebookId, string.Empty, currentSection, string.Empty, false, -1, false));

        var root = Ok(await Tool(gateway).NavigateToUrl(K.SectionLink, cancellationToken: Ct));

        Assert.Equal(code, Assert.Single(root.GetProperty("warnings").EnumerateArray()).GetProperty("code").GetString());
        Assert.Equal("section", Assert.Single(logged).LinkKind);
        Assert.DoesNotContain(gateway.Calls, c => c.Scope == HierarchyScope.Self); // no page, so no AD-21 read
    }

    [Theory]
    [InlineData(K.NotebookId, ToolWarnings.Landed)]
    [InlineData(K.Notebook2Id, ToolWarnings.LandedElsewhere)]
    public async Task ANotebookLink_IsComparedByTheNotebooksId(string currentNotebook, string code)
    {
        var gateway = Gateway();
        gateway.Windows.Add(new OneNoteWindow(0x10, true, true, currentNotebook, string.Empty, string.Empty, string.Empty, false, -1, false));

        var root = Ok(await Tool(gateway).NavigateToUrl(K.NotebookLink, cancellationToken: Ct));

        Assert.Equal(code, Assert.Single(root.GetProperty("warnings").EnumerateArray()).GetProperty("code").GetString());
        Assert.Equal("notebook", Assert.Single(logged).LinkKind);
    }

    [Fact]
    public async Task OnlyTheCurrentWindow_IsChecked_AndListed()
    {
        var gateway = Gateway();
        gateway.Windows.Add(new OneNoteWindow(0x20, false, false, K.Notebook2Id, string.Empty, K.OtherSectionId, string.Empty, false, -1, false));
        OnPage(gateway, K.PageId, K.PageLink());

        var root = Ok(await Tool(gateway).NavigateToUrl(K.PageLink(), cancellationToken: Ct));

        Assert.Equal(0x10, Assert.Single(root.GetProperty("meta").GetProperty("windows").EnumerateArray()).GetProperty("handle").GetInt32());
        Assert.Equal(ToolWarnings.Landed, Assert.Single(root.GetProperty("warnings").EnumerateArray()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task AProtectedPage_WithoutAReadGrant_IsLeftOut_WithReadNotGranted_AndStillLands()
    {
        var gateway = Gateway();
        OnPage(gateway, K.PageId, K.PageLink());
        gateway.Hierarchies[(K.SectionId, HierarchyScope.Self)] = K.SectionSelf(K.SectionId, "Probes", encrypted: true);

        var root = Ok(await Tool(gateway).NavigateToUrl(K.PageLink(), cancellationToken: Ct));

        var warnings = root.GetProperty("warnings").EnumerateArray().ToList();
        Assert.Equal([ToolWarnings.Landed, ToolWarnings.ReadNotGranted], warnings.Select(w => w.GetProperty("code").GetString()));
        Assert.DoesNotContain(K.PageId, warnings[0].GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Contains("its ID is left out", warnings[0].GetProperty("message").GetString(), StringComparison.Ordinal);
        var window = Assert.Single(root.GetProperty("meta").GetProperty("windows").EnumerateArray());
        Assert.False(window.TryGetProperty("pageId", out _));
        Assert.Equal(K.SectionId, window.GetProperty("sectionId").GetString());
    }

    [Fact]
    public async Task TheWindowsReadFailing_IsLandingUnconfirmed_AndTheCallStaysOk()
    {
        var gateway = Gateway();
        gateway.WindowsFailure = new OneNoteException(ErrorCode.Timeout, "Windows timed out.");

        var root = Ok(await Tool(gateway).NavigateToUrl(K.PageLink(), cancellationToken: Ct));

        var warning = Assert.Single(root.GetProperty("warnings").EnumerateArray());
        Assert.Equal(ToolWarnings.LandingUnconfirmed, warning.GetProperty("code").GetString());
        Assert.Equal(NavigateToUrlUseCase.UnconfirmedUserAction, warning.GetProperty("userAction").GetString());
        Assert.False(root.GetProperty("meta").TryGetProperty("windows", out _));
        Assert.Single(gateway.NavigateUrlCalls);
        Assert.Equal(ToolWarnings.LandingUnconfirmed, Assert.Single(logged).Outcome);
    }

    [Fact]
    public async Task NoWindowAfterTheCall_IsLandingUnconfirmed()
    {
        // Spike 4.12 W6: Count 0 and CurrentWindow null.
        var gateway = Gateway();

        var root = Ok(await Tool(gateway).NavigateToUrl(K.PageLink(), cancellationToken: Ct));

        Assert.Equal(ToolWarnings.LandingUnconfirmed, Assert.Single(root.GetProperty("warnings").EnumerateArray()).GetProperty("code").GetString());
        Assert.Equal(0, root.GetProperty("meta").GetProperty("windows").GetArrayLength());
    }

    [Fact]
    public async Task TheHyperlinkReadFailing_IsLandingUnconfirmed_WithTheWindowListed()
    {
        var gateway = Gateway();
        OnPage(gateway, K.PageId, K.PageLink());
        gateway.HyperlinkFailure = new OneNoteException(ErrorCode.ComUnavailable, "gone");

        var root = Ok(await Tool(gateway).NavigateToUrl(K.PageLink(), cancellationToken: Ct));

        var warning = Assert.Single(root.GetProperty("warnings").EnumerateArray());
        Assert.Equal(ToolWarnings.LandingUnconfirmed, warning.GetProperty("code").GetString());
        Assert.Contains("OneNote's current page is " + K.PageId, warning.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Single(root.GetProperty("meta").GetProperty("windows").EnumerateArray());
    }

    // --- Refusals: nothing sent ---------------------------------------------------------------------------------

    public static TheoryData<string, string, string> Step1Refusals() => new()
    {
        { K.LocalLink, CloudLinks.Rules.LocalLink, "local" },
        { K.WebLink, CloudLinks.Rules.NotOneNote, "web" },
        { "mailto:x@example.com", CloudLinks.Rules.NotOneNote, "other" },
        { string.Empty, CloudLinks.Rules.Empty, "empty" },
        { "onenote:" + K.Root + "MCP%2520Test%2520Cloud/Probes.one#x&page-id=" + K.PageGuid, CloudLinks.Rules.Encoding, "cloud" },
        { "onenote:" + K.EncodedNotebook + "/Probes.one?a=b", CloudLinks.Rules.Query, "cloud" },
    };

    [Theory]
    [MemberData(nameof(Step1Refusals))]
    public async Task AStep1Refusal_IsInvalidArgument_BeforeAnyCom_AndItsLogLineHoldsTheKindAndRuleButNoPartOfTheLink(string url, string rule, string kind)
    {
        var gateway = Gateway();

        var error = Error(await Tool(gateway).NavigateToUrl(url, cancellationToken: Ct), "InvalidArgument");

        Assert.False(error.GetProperty("retryable").GetBoolean());
        Assert.False(error.TryGetProperty("outcomeUnknown", out _));
        Assert.Empty(gateway.Calls);
        Assert.Empty(gateway.NavigateUrlCalls);
        Assert.Equal(0, gateway.WindowsCalls);
        var entry = Assert.Single(logged);
        Assert.Equal((kind, rule, "refused"), (entry.LinkKind, entry.Rule, entry.Outcome));
        AssertNoPartOfTheLink(entry, url);
    }

    public static TheoryData<string, string> Step3Refusals() => new()
    {
        { "onenote:" + K.Root + "Closed%20Notebook/Probes.one#x&page-id=" + K.PageGuid, CloudLinks.Rules.NotInOpenNotebook },
        { "onenote:https://evil.example/0123456789abcdef/Documents/MCP%20Test%20Cloud/Probes.one#x&page-id=" + K.PageGuid, CloudLinks.Rules.NotInOpenNotebook },
        { K.PageLink("Missing.one"), CloudLinks.Rules.NotASection },
        { K.PageLink(basePath: K.Root + "MCP%20Test%20Cloud%202"), CloudLinks.Rules.BasePath },
        { "onenote:" + K.EncodedNotebook + "/Group/", CloudLinks.Rules.SectionGroupFolder },
    };

    [Theory]
    [MemberData(nameof(Step3Refusals))]
    public async Task AStep3Refusal_IsInvalidArgument_AfterOneHierarchyRead_AndNothingIsShown(string url, string rule)
    {
        var gateway = Gateway();

        Error(await Tool(gateway).NavigateToUrl(url, cancellationToken: Ct), "InvalidArgument");

        Assert.Equal([(string.Empty, HierarchyScope.Sections)], gateway.Calls);
        Assert.Empty(gateway.NavigateUrlCalls);
        Assert.Equal(0, gateway.WindowsCalls);
        var entry = Assert.Single(logged);
        Assert.Equal(rule, entry.Rule);
        Assert.Equal("refused", entry.Outcome);
        AssertNoPartOfTheLink(entry, url);
    }

    [Fact]
    public async Task WhileAnAbandonedWriteIsOutstanding_IsComBusy_WithNoCom()
    {
        var gateway = Gateway();
        gateway.AbandonedWrites = new TaskCompletionSource();

        var error = Error(await Tool(gateway).NavigateToUrl(K.PageLink(), cancellationToken: Ct), "ComBusy");

        Assert.True(error.GetProperty("retryable").GetBoolean());
        Assert.Empty(gateway.Calls);
        Assert.Empty(gateway.NavigateUrlCalls);
    }

    // --- Step 7 failures ----------------------------------------------------------------------------------------

    [Fact]
    public async Task ObjectDoesNotExist_GetsTheWaitASecondUserAction_AndNoLandingCheck()
    {
        // AD-9: a repeat inside about a second failed 0x80042014 (spike 4.12 R1 C); the server doesn't retry.
        var gateway = Gateway();
        gateway.NavigateUrlFailure = new OneNoteException(ErrorCode.ObjectNotFound, "NavigateToUrl failed: hrObjectDoesNotExist (0x80042014).", unchecked((int)0x80042014));

        var error = Error(await Tool(gateway).NavigateToUrl(K.PageLink(), cancellationToken: Ct), "ObjectNotFound");

        Assert.Equal("0x80042014", error.GetProperty("hresult").GetString());
        Assert.Equal(NavigateToUrlUseCase.ObjectNotFoundUserAction, error.GetProperty("userAction").GetString());
        Assert.Contains("wait a second and try once more", NavigateToUrlUseCase.ObjectNotFoundUserAction, StringComparison.Ordinal);
        Assert.Contains("ask the user whether OneNote showed a message", NavigateToUrlUseCase.ObjectNotFoundUserAction, StringComparison.Ordinal);
        Assert.Single(gateway.NavigateUrlCalls); // never retried
        Assert.Equal(0, gateway.WindowsCalls);
        Assert.Equal(("page", CloudLinks.Rules.None, "ObjectNotFound"), (logged[0].LinkKind, logged[0].Rule, logged[0].Outcome));
    }

    [Theory]
    [InlineData(ErrorCode.Timeout, null)]
    [InlineData(ErrorCode.ComUnavailable, 0x800706BAu)]
    [InlineData(ErrorCode.Internal, 0x800706BEu)]
    public async Task AnAmbiguousStep7Failure_IsOutcomeUnknown_AndNeverRetried(ErrorCode code, uint? hresult)
    {
        var gateway = Gateway();
        gateway.NavigateUrlFailure = new OneNoteException(code, "m", hresult is { } h ? unchecked((int)h) : null);

        var error = Error(await Tool(gateway).NavigateToUrl(K.PageLink(), cancellationToken: Ct), code.ToString());

        Assert.True(error.GetProperty("outcomeUnknown").GetBoolean());
        Assert.False(error.GetProperty("retryable").GetBoolean());
        Assert.Single(gateway.NavigateUrlCalls);
        Assert.Equal(0, gateway.WindowsCalls);
    }

    [Fact]
    public async Task ModalDialogOpen_PassesThrough_Retryable()
    {
        var gateway = Gateway();
        gateway.NavigateUrlFailure = new OneNoteException(ErrorCode.ModalDialogOpen, "m", unchecked((int)0x80042030));

        var error = Error(await Tool(gateway).NavigateToUrl(K.PageLink(), cancellationToken: Ct), "ModalDialogOpen");

        Assert.True(error.GetProperty("retryable").GetBoolean());
        Assert.False(error.TryGetProperty("outcomeUnknown", out _));
    }

    // --- The window argument (Decision OQ4: its own commit) -------------------------------------------------------

    [Fact]
    public async Task WithAWindow_OnlyThatWindowMoves_AndItsLandingIsChecked()
    {
        var gateway = Gateway();
        gateway.Windows.Add(new OneNoteWindow(0x10, true, true, K.Notebook2Id, string.Empty, K.OtherSectionId, string.Empty, false, -1, false));
        gateway.Windows.Add(new OneNoteWindow(0x20, false, false, K.NotebookId, string.Empty, K.SectionId, K.PageId, false, -1, false));
        gateway.Hyperlinks[K.PageId] = K.PageLink();

        var root = Ok(await Tool(gateway).NavigateToUrl(K.PageLink(), window: 0x20, cancellationToken: Ct));

        Assert.Equal([K.PageLink()], gateway.NavigateUrlCalls);
        Assert.Equal([(int?)0x20], gateway.NavigateUrlWindows);
        Assert.Equal(ToolWarnings.Landed, Assert.Single(root.GetProperty("warnings").EnumerateArray()).GetProperty("code").GetString());
        var window = Assert.Single(root.GetProperty("meta").GetProperty("windows").EnumerateArray());
        Assert.Equal(0x20, window.GetProperty("handle").GetInt32());
        Assert.False(window.GetProperty("current").GetBoolean());
    }

    [Fact]
    public async Task WithoutAWindow_TheCallIsAppLevel()
    {
        var gateway = Gateway();
        OnPage(gateway, K.PageId, K.PageLink());

        Ok(await Tool(gateway).NavigateToUrl(K.PageLink(), cancellationToken: Ct));

        Assert.Equal([(int?)null], gateway.NavigateUrlWindows);
    }

    [Fact]
    public async Task WithAHandleNoWindowHas_IsInvalidArgument_AndNothingIsSent()
    {
        var gateway = Gateway();
        OnPage(gateway, K.PageId, K.PageLink());

        var error = Error(await Tool(gateway).NavigateToUrl(K.PageLink(), window: 0x99, cancellationToken: Ct), "InvalidArgument");

        Assert.False(error.GetProperty("retryable").GetBoolean());
        Assert.False(error.TryGetProperty("outcomeUnknown", out _));
        Assert.Equal(OneNoteWindow.UnknownHandleUserAction, error.GetProperty("userAction").GetString());
        Assert.Equal(OneNoteWindow.UnknownHandleMessage(0x99), error.GetProperty("message").GetString());
        Assert.Empty(gateway.NavigateUrlCalls);
        Assert.Equal(0, gateway.WindowsCalls);
        Assert.Equal((CloudLinks.Rules.None, "InvalidArgument"), (logged[0].Rule, logged[0].Outcome));
    }

    [Fact]
    public async Task TheWindowGoneAfterTheCall_IsLandingUnconfirmed()
    {
        var gateway = Gateway();
        gateway.Windows.Add(new OneNoteWindow(0x20, true, true, K.NotebookId, string.Empty, K.SectionId, K.PageId, false, -1, false));
        gateway.OnNavigateUrlCall = _ =>
        {
            gateway.Windows.Clear();
            gateway.Windows.Add(new OneNoteWindow(0x10, true, true, K.NotebookId, string.Empty, K.SectionId, K.PageId, false, -1, false));
            return Task.CompletedTask;
        };

        var root = Ok(await Tool(gateway).NavigateToUrl(K.PageLink(), window: 0x20, cancellationToken: Ct));

        var warning = Assert.Single(root.GetProperty("warnings").EnumerateArray());
        Assert.Equal(ToolWarnings.LandingUnconfirmed, warning.GetProperty("code").GetString());
        Assert.Contains("handle 32", warning.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(0, root.GetProperty("meta").GetProperty("windows").GetArrayLength());
    }

    [Fact]
    public async Task WithAWindow_WhileAnAbandonedWriteIsOutstanding_IsComBusy_AndNothingMoves()
    {
        var gateway = Gateway();
        gateway.AbandonedWrites = new TaskCompletionSource();
        OnPage(gateway, K.PageId, K.PageLink());

        Error(await Tool(gateway).NavigateToUrl(K.PageLink(), window: 0x10, cancellationToken: Ct), "ComBusy");

        Assert.Empty(gateway.NavigateUrlCalls);
    }

    [Fact]
    public void Tool_TakesAnOptionalIntegerWindow_AndDescribesIt()
    {
        using var services = BuildServer();
        var tool = Assert.Single(services.GetServices<McpServerTool>().Select(t => t.ProtocolTool), t => t.Name == NavigateToUrlTool.Name);

        var window = tool.InputSchema.GetProperty("properties").GetProperty("window");
        Assert.Equal(["integer", "null"], window.GetProperty("type").EnumerateArray().Select(t => t.GetString()));
        Assert.Equal(["url"], tool.InputSchema.GetProperty("required").EnumerateArray().Select(r => r.GetString()));
        Assert.Contains("With window, a handle from get_windows, only that window moves", tool.Description, StringComparison.Ordinal);
    }

    // --- The tool -----------------------------------------------------------------------------------------------

    [Fact]
    public void Tool_IsUserVisible_AnnotatedAsNavigateTo_AndTakesOneRequiredUrl()
    {
        using var services = BuildServer();
        var tools = services.GetServices<McpServerTool>().Select(t => t.ProtocolTool).ToList();
        var tool = Assert.Single(tools, t => t.Name == NavigateToUrlTool.Name);
        var navigate = Assert.Single(tools, t => t.Name == NavigateToTool.Name);

        Assert.StartsWith("User-visible: changes what OneNote shows, and may bring OneNote to the front. Call only when the user explicitly asks", tool.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("takes keyboard focus", tool.Description, StringComparison.Ordinal); // no focus promise (AD-11)
        Assert.Contains("OneNote's current page is", tool.Description, StringComparison.Ordinal);
        Assert.Contains("landedElsewhere", tool.Description, StringComparison.Ordinal);
        Assert.Contains("landingUnconfirmed", tool.Description, StringComparison.Ordinal);
        Assert.Contains("call navigate_to with the object's ID instead", tool.Description, StringComparison.Ordinal);
        Assert.Equal(
            (navigate.Annotations!.ReadOnlyHint, navigate.Annotations.DestructiveHint, navigate.Annotations.IdempotentHint, navigate.Annotations.OpenWorldHint),
            (tool.Annotations!.ReadOnlyHint, tool.Annotations.DestructiveHint, tool.Annotations.IdempotentHint, tool.Annotations.OpenWorldHint));
        Assert.Equal(["url"], tool.InputSchema.GetProperty("required").EnumerateArray().Select(r => r.GetString()));
    }

    [Fact]
    public void UseCase_IsAppState()
    {
        var gateway = Gateway();
        Assert.Equal(OperationClass.AppState, UseCase(gateway).Class);
    }

    private static void AssertNoPartOfTheLink(NavigateToUrlLogEntry entry, string url)
    {
        var line = string.Join(" ", entry.LinkKind, entry.Rule, entry.Outcome);
        foreach (var part in new[] { "docs.live.net", "Probes", "MCP", "Closed", "Group/", "0123456789abcdef", "onedrive", "Fixtures", "mcp-test", "66666666" })
        {
            if (url.Contains(part, StringComparison.OrdinalIgnoreCase))
            {
                Assert.DoesNotContain(part, line, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    private static FakeGateway Gateway()
    {
        var gateway = new FakeGateway();
        gateway.Hierarchies[(string.Empty, HierarchyScope.Sections)] = K.Hierarchy();
        gateway.Hierarchies[(K.SectionId, HierarchyScope.Self)] = K.SectionSelf(K.SectionId, "Probes");
        return gateway;
    }

    /// <summary>The current window, 0x10, on <paramref name="pageId"/> in Probes, whose link is <paramref name="link"/>.</summary>
    private static void OnPage(FakeGateway gateway, string pageId, string link)
    {
        gateway.Windows.Add(new OneNoteWindow(0x10, true, true, K.NotebookId, string.Empty, K.SectionId, pageId, false, -1, false));
        gateway.Hyperlinks[pageId] = link;
    }

    private NavigateToUrlTool Tool(FakeGateway gateway) =>
        new(new ToolRunner(NullLogger<ToolRunner>.Instance), UseCase(gateway));

    private NavigateToUrlUseCase UseCase(FakeGateway gateway)
    {
        var store = new SettingsStore(temp.Combine("settings.json"), temp.Combine("journal"));
        var clock = new FakeClock();
        return new NavigateToUrlUseCase(gateway, new AppStatePipeline(gateway), new ReadAccess(gateway, store.Read, new SettingsWriter(store, clock), clock), logged.Add);
    }

    private static ServiceProvider BuildServer()
    {
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddOneNoteMcp();
        return collection.BuildServiceProvider();
    }

    private static string CorrelationIdOf(JsonElement root) => root.GetProperty("meta").GetProperty("correlationId").GetString()!;

    private static JsonElement Ok(CallToolResult result)
    {
        Assert.NotNull(result.StructuredContent);
        var root = result.StructuredContent.Value;
        Assert.False(result.IsError, root.GetRawText());
        Assert.True(root.GetProperty("ok").GetBoolean());
        TextBlockAssert.Matches(result);
        return root;
    }

    private static JsonElement Error(CallToolResult result, string code)
    {
        Assert.True(result.IsError);
        var error = result.StructuredContent!.Value.GetProperty("error");
        Assert.Equal(code, error.GetProperty("code").GetString());
        return error;
    }
}
