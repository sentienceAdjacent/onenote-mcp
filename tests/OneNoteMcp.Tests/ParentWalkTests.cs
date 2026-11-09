using OneNoteMcp.Core;
using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Xml;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>
/// Story 2.1 (S2/D3): the AD-21 check's two parent walks share one lazy iterator, but each keeps its own gateway calls,
/// stop rule and depth-overflow handling. These pin the exact call sequences, so a refactor can't fetch the chain eagerly.
/// </summary>
public sealed class ParentWalkTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OwningSection_WalksUpThroughSubpages_AndStopsAtTheFirstSection()
    {
        var gateway = new ChainGateway()
            .Node("N", string.Empty, Xml("Notebook", "N"))
            .Node("G", "N", Xml("SectionGroup", "G"))
            .Node("S", "G", Xml("Section", "S"))
            .Node("P1", "S", Xml("Page", "P1"))
            .Node("P2", "P1", Xml("Page", "P2"));

        var section = await ReadAccess.WithoutStoredGrants(gateway).OwningSectionAsync("P2", Ct);

        Assert.Equal("S", section?.Id);
        Assert.Equal(["Parent:P2", "Self:P1", "Parent:P1", "Self:S"], gateway.Calls);
    }

    [Fact]
    public async Task OwningSection_StopsAtASectionGroupOrNotebook_WithoutGoingHigher()
    {
        var gateway = new ChainGateway()
            .Node("N", string.Empty, Xml("Notebook", "N"))
            .Node("G", "N", Xml("SectionGroup", "G"))
            .Node("S", "G", Xml("Section", "S"));
        gateway.FailParentOf.Add("N");

        var section = await ReadAccess.WithoutStoredGrants(gateway).OwningSectionAsync("S", Ct);

        Assert.Null(section);
        Assert.Equal(["Parent:S", "Self:G"], gateway.Calls);
    }

    [Fact]
    public async Task OwningSection_AnEmptyParent_IsNoSection()
    {
        var gateway = new ChainGateway().Node("P", string.Empty, Xml("Page", "P"));

        Assert.Null(await ReadAccess.WithoutStoredGrants(gateway).OwningSectionAsync("P", Ct));
        Assert.Equal(["Parent:P"], gateway.Calls);
    }

    [Fact]
    public async Task OwningSection_MoreThan64PagesDeep_IsInternal_After64Ancestors()
    {
        var gateway = new ChainGateway();
        for (var i = 0; i <= 100; i++)
        {
            gateway.Node($"P{i}", $"P{i + 1}", Xml("Page", $"P{i}"));
        }

        var ex = await Assert.ThrowsAsync<OneNoteException>(() => ReadAccess.WithoutStoredGrants(gateway).OwningSectionAsync("P0", Ct));

        Assert.Equal(ErrorCode.Internal, ex.Code);
        Assert.Equal("The parent chain of P0 is more than 64 levels deep.", ex.Message);
        Assert.Equal(64, gateway.Calls.Count(c => c.StartsWith("Parent:", StringComparison.Ordinal)));
        Assert.Equal(64, gateway.Calls.Count(c => c.StartsWith("Self:", StringComparison.Ordinal)));
        Assert.Equal("Self:P64", gateway.Calls[^1]);
    }

    [Fact]
    public async Task DialogLocation_WalksFromTheSection_ToTheNotebook_BeforeTheDialog()
    {
        var gateway = new ChainGateway()
            .Node("N", string.Empty, Xml("Notebook", "N", nickname: "Nick"))
            .Node("G1", "N", Xml("SectionGroup", "G1"))
            .Node("G2", "G1", Xml("SectionGroup", "G2"))
            .Node("S", "G2", Xml("Section", "S", encrypted: true))
            .Node("P", "S", Xml("Page", "P"));
        gateway.FailParentOf.Add("N");
        var prompt = new DecliningPrompt(gateway);

        var ex = await Assert.ThrowsAsync<OneNoteException>(() =>
            ReadAccess.WithoutStoredGrants(gateway).CheckObjectAsync(new ToolCallContext("c", Prompt: prompt), "P", Ct));

        Assert.Equal(ErrorCode.ReadNotGranted, ex.Code);
        Assert.Equal(["Parent:P", "Self:S", "Parent:S", "Self:G2", "Parent:G2", "Self:G1", "Parent:G1", "Self:N"], gateway.Calls);
        var question = Assert.Single(prompt.Questions);
        Assert.Equal("Nick", question.NotebookName);
        Assert.Equal(["G1", "G2"], question.SectionGroups);
        Assert.Equal(8, prompt.CallsWhenAsked);
    }

    [Fact]
    public async Task DialogLocation_NoNotebookWithin64Ancestors_ShowsTheGroupsSeen_AndNoNotebook()
    {
        var gateway = new ChainGateway()
            .Node("S", "G0", Xml("Section", "S", encrypted: true))
            .Node("P", "S", Xml("Page", "P"));
        for (var i = 0; i <= 100; i++)
        {
            gateway.Node($"G{i}", $"G{i + 1}", Xml("SectionGroup", $"G{i}"));
        }

        var prompt = new DecliningPrompt(gateway);

        await Assert.ThrowsAsync<OneNoteException>(() =>
            ReadAccess.WithoutStoredGrants(gateway).CheckObjectAsync(new ToolCallContext("c", Prompt: prompt), "P", Ct));

        var question = Assert.Single(prompt.Questions);
        Assert.Equal(string.Empty, question.NotebookName);
        Assert.Equal(64, question.SectionGroups.Count);
        Assert.Equal("G63", question.SectionGroups[0]);
        Assert.Equal("G0", question.SectionGroups[^1]);
        Assert.Equal(2 + 128, gateway.Calls.Count);
        Assert.Equal("Self:G63", gateway.Calls[^1]);
    }

    private static string Xml(string kind, string id, bool encrypted = false, string? nickname = null) =>
        $"<one:{kind} xmlns:one=\"{OneNoteXml.Namespace2013}\" ID=\"{id}\" name=\"{id}\" path=\"C:\\{id}\""
        + (encrypted ? " encrypted=\"true\"" : string.Empty)
        + (nickname is null ? string.Empty : $" nickname=\"{nickname}\"")
        + " />";

    /// <summary>Records what the dialog was asked and how many gateway calls had been made by then; always declines.</summary>
    private sealed class DecliningPrompt(ChainGateway gateway) : IGrantPrompt
    {
        public List<GrantQuestion> Questions { get; } = [];

        public int CallsWhenAsked { get; private set; }

        public Task<GrantPromptResult> AskAsync(GrantQuestion question, CancellationToken ct)
        {
            Questions.Add(question);
            CallsWhenAsked = gateway.Calls.Count;
            return Task.FromResult(new GrantPromptResult(GrantPromptStatus.Declined));
        }
    }

    /// <summary>A hierarchy given as parent links and each object's <c>self</c> XML; records every call in order.</summary>
    private sealed class ChainGateway : IOneNoteGateway
    {
        private readonly Dictionary<string, (string Parent, string Xml)> nodes = new(StringComparer.Ordinal);

        public List<string> Calls { get; } = [];

        /// <summary>IDs whose <c>GetHierarchyParent</c> throws, to show a walk never asks for them.</summary>
        public HashSet<string> FailParentOf { get; } = new(StringComparer.Ordinal);

        public ChainGateway Node(string id, string parent, string xml)
        {
            nodes[id] = (parent, xml);
            return this;
        }

        public Task<string> GetHierarchyParentAsync(string objectId, CancellationToken ct)
        {
            Calls.Add($"Parent:{objectId}");
            return FailParentOf.Contains(objectId)
                ? Task.FromException<string>(new OneNoteException(ErrorCode.ComBusy, "should not be asked"))
                : Task.FromResult(nodes[objectId].Parent);
        }

        public Task<string> GetHierarchyAsync(string startNodeId, HierarchyScope scope, CancellationToken ct)
        {
            Assert.Equal(HierarchyScope.Self, scope);
            Calls.Add($"Self:{startNodeId}");
            return Task.FromResult(nodes[startNodeId].Xml);
        }

        public Task<string> GetPageContentAsync(string pageId, PageInfo pageInfo, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> GetBinaryPageContentAsync(string pageId, string callbackId, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> FindPagesAsync(string startNodeId, string query, bool includeUnindexed, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> FindMetaAsync(string startNodeId, string name, bool includeUnindexed, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> GetHyperlinkToObjectAsync(string hierarchyId, string pageContentObjectId, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> GetWebHyperlinkToObjectAsync(string hierarchyId, string pageContentObjectId, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> GetSpecialLocationAsync(SpecialLocation location, CancellationToken ct) => throw new NotSupportedException();

        public Task NavigateToAsync(string hierarchyId, string objectId, CancellationToken ct) => throw new NotSupportedException();
    }
}
