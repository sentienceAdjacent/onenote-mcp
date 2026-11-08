using System.Xml.Linq;
using OneNoteMcp.Core;
using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.LargeResults;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Xml;
using Xunit;
using V = OneNoteMcp.Tests.VaultGateway;

namespace OneNoteMcp.Tests;

/// <summary>
/// Story 1.20's I/O matrix (AD-21): every read tool over a notebook with password-protected sections, through the real
/// runner, use cases, access check, settings writer and result store. The protected text never leaves the server
/// without a grant, and no tool can make one.
/// </summary>
public sealed class ProtectedSectionToolTests
{
    private static readonly XNamespace One = OneNoteXml.Namespace2013;

    // --- Target read, no grant ------------------------------------------------------------------------------------

    [Fact]
    public async Task PageContent_NoGrant_NoElicitation_IsReadNotGranted_AndThePageIsNeverRead()
    {
        using var h = new VaultHarness();

        var error = Grants.NotGranted(await h.PageContent(V.VaultPageId), V.VaultId);

        Assert.Contains("can't show the grant dialog", error.GetProperty("message").GetString()!, StringComparison.Ordinal);
        Assert.Empty(h.Gateway.ContentCalls);
    }

    [Fact]
    public async Task Binary_NoGrant_IsReadNotGranted_AndTheBinaryIsNeverRead()
    {
        using var h = new VaultHarness();

        Grants.NotGranted(await h.Binary(V.VaultPageId), V.VaultId);
        Assert.Empty(h.Gateway.ContentCalls);
    }

    [Fact]
    public async Task LockedVault_NoGrant_IsReadNotGranted_NotSectionLocked_TheCheckRunsFirst()
    {
        using var h = new VaultHarness(locked: true);

        Grants.NotGranted(await h.PageContent(V.VaultPageId), V.VaultId);
        Assert.Empty(h.Gateway.ContentCalls);
    }

    [Fact]
    public async Task OpenSectionPage_NeedsNoGrant_AndNeverAsks()
    {
        using var h = new VaultHarness();
        var prompt = new FakePrompt();

        var root = Grants.Ok(await h.PageContent(V.OpenPageId, prompt));

        Assert.Contains("Open text", Grants.Xml(root), StringComparison.Ordinal);
        Assert.Empty(prompt.Questions);
    }

    // --- Dialog -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Dialog_Once_ReturnsThePage_SavesNothing_CoversTheSectionForTheWindow_ThenAsksAgain()
    {
        using var h = new VaultHarness();
        var prompt = new FakePrompt(FakePrompt.Accept("  vault ", ReadGrantLifetime.Once), new GrantPromptResult(GrantPromptStatus.Declined));

        var root = Grants.Ok(await h.PageContent(V.VaultPageId, prompt));
        Assert.Contains(V.Secret, Grants.Xml(root), StringComparison.Ordinal);
        Assert.False(File.Exists(h.SettingsPath));

        // The question names the section and its notebook, for the user, and the window (default 5 minutes).
        var question = Assert.Single(prompt.Questions);
        Assert.Equal((V.VaultId, V.VaultName, "Notebook A", 5), (question.SectionId, question.SectionName, question.NotebookName, question.OnceMinutes));
        Assert.Empty(question.SectionGroups);

        // Inside the window: another page of the section, with no dialog.
        h.Clock.Advance(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(59));
        Grants.Ok(await h.PageContent(V.VaultPage2Id, prompt));
        Assert.Single(prompt.Questions);

        // After it: asked again (declined here).
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        Grants.NotGranted(await h.PageContent(V.VaultPageId, prompt), V.VaultId);
        Assert.Equal(2, prompt.Questions.Count);
    }

    [Fact]
    public async Task OnceWindow_CoversBroadReadsOfThatSectionOnly()
    {
        using var h = new VaultHarness();
        Grants.Ok(await h.PageContent(V.VaultPageId, new FakePrompt(FakePrompt.Accept(V.VaultName, ReadGrantLifetime.Once))));

        var hierarchy = Grants.Ok(await h.Hierarchy(V.NotebookId, HierarchyScope.Pages));
        Assert.Equal(2, SectionOf(XDocument.Parse(Grants.Xml(hierarchy)), V.VaultId)!.Elements(One + "Page").Count());
        Assert.Equal([V.GroupVaultId], Grants.Withheld(hierarchy));

        var search = Grants.Ok(await h.FindPages("SECRET", V.NotebookId));
        Assert.Equal([V.VaultPageId, V.VaultPage2Id], XDocument.Parse(Grants.Xml(search)).Descendants(One + "Page").Select(p => (string)p.Attribute("ID")!));
        Assert.Equal([V.GroupVaultId], Grants.Withheld(search));

        h.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal([V.VaultId, V.GroupVaultId], Grants.Withheld(Grants.Ok(await h.Hierarchy(V.NotebookId, HierarchyScope.Pages))));
    }

    [Fact]
    public async Task OnceWindow_FollowsReadGrantsOnceMinutes()
    {
        using var h = new VaultHarness();
        File.WriteAllText(h.SettingsPath, """{"schemaVersion": 1, "readGrants": {"onceMinutes": 1}}""");
        var prompt = new FakePrompt(FakePrompt.Accept(V.VaultName, ReadGrantLifetime.Once));

        Grants.Ok(await h.PageContent(V.VaultPageId, prompt));
        Assert.Equal(1, Assert.Single(prompt.Questions).OnceMinutes);

        h.Clock.Advance(TimeSpan.FromSeconds(59));
        Grants.Ok(await h.PageContent(V.VaultPageId, prompt));
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        Grants.NotGranted(await h.PageContent(V.VaultPageId, prompt), V.VaultId);
    }

    [Fact]
    public async Task FindPages_NeverCachesProtectedPages_EvenUnderAGrant()
    {
        using var h = new VaultHarness();
        h.Grant(V.VaultPath, V.VaultId);
        var cache = PageTextCache.WithDefaults();

        Grants.Ok(await h.FindPages("SECRET", V.NotebookId, cache));
        var firstReads = h.Gateway.ContentCalls.Count(c => c.Contains("{SV}", StringComparison.Ordinal));
        Grants.Ok(await h.FindPages("SECRET", V.NotebookId, cache));

        // Only the open page is cached; the Vault's pages are read again on every search.
        Assert.Equal(1, cache.Count);
        Assert.Equal(2, firstReads);
        Assert.Equal(4, h.Gateway.ContentCalls.Count(c => c.Contains("{SV}", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Dialog_InASectionGroup_NamesTheGroup()
    {
        using var h = new VaultHarness();
        var prompt = new FakePrompt(FakePrompt.Accept(V.VaultName, ReadGrantLifetime.Once));

        Grants.Ok(await h.PageContent(V.GroupVaultPageId, prompt));

        Assert.Equal(["Group"], Assert.Single(prompt.Questions).SectionGroups);
    }

    public static TheoryData<GrantPromptResult, string> NoGrantAnswers() => new()
    {
        { new GrantPromptResult(GrantPromptStatus.Declined), "declined" },
        { new GrantPromptResult(GrantPromptStatus.Cancelled), "cancelled" },
        { GrantPromptResult.Unsupported, "can't show" },
        { FakePrompt.Accept("Vault2", ReadGrantLifetime.UntilRevoked), "didn't match" },
        { FakePrompt.Accept(string.Empty, ReadGrantLifetime.UntilRevoked), "didn't match" },
        { FakePrompt.Accept(V.VaultName, ReadGrantLifetime.Hours, 0), "hours" },
        { FakePrompt.Accept(V.VaultName, ReadGrantLifetime.Hours, 169), "hours" },
    };

    [Theory]
    [MemberData(nameof(NoGrantAnswers))]
    public async Task Dialog_DeclineCancelMismatchOrBadHours_IsReadNotGranted_AndSavesNothing(GrantPromptResult answer, string why)
    {
        using var h = new VaultHarness();

        var error = Grants.NotGranted(await h.PageContent(V.VaultPageId, new FakePrompt(answer)), V.VaultId);

        Assert.Contains(why, error.GetProperty("message").GetString()!, StringComparison.Ordinal);
        Assert.False(File.Exists(h.SettingsPath));
        Assert.Empty(h.Gateway.ContentCalls);
    }

    [Fact]
    public async Task Dialog_Hours_IsSavedWithItsExpiry_LaterCallsPass_UntilItExpires()
    {
        using var h = new VaultHarness();
        var prompt = new FakePrompt(FakePrompt.Accept(V.VaultName, ReadGrantLifetime.Hours, 2));

        Grants.Ok(await h.PageContent(V.VaultPageId, prompt));
        var grant = Assert.Single(h.Settings.Read().ProtectedSections.Values);
        Assert.Equal((GrantKeys.SectionKey(V.VaultPath), V.VaultId, GrantedVia.Elicitation, V.NotebookPath), (grant.Key, grant.IdAtGrant, grant.GrantedVia, grant.NotebookKey));
        Assert.Equal(h.Clock.GetUtcNow().AddHours(2), grant.ExpiresAt);

        // Without any dialog now.
        h.Clock.Advance(TimeSpan.FromMinutes(119));
        Grants.Ok(await h.PageContent(V.VaultPage2Id));
        Grants.Ok(await h.Hierarchy(V.VaultId, HierarchyScope.Pages));

        h.Clock.Advance(TimeSpan.FromMinutes(1));
        Grants.NotGranted(await h.PageContent(V.VaultPageId), V.VaultId);
        Assert.Single(prompt.Questions);
    }

    [Fact]
    public async Task Dialog_UntilRevoked_IsSaved_AndARevokeWithholdsAgain()
    {
        using var h = new VaultHarness();

        Grants.Ok(await h.PageContent(V.VaultPageId, new FakePrompt(FakePrompt.Accept(V.VaultName, ReadGrantLifetime.UntilRevoked))));
        Assert.Null(Assert.Single(h.Settings.Read().ProtectedSections.Values).ExpiresAt);
        Grants.Ok(await h.PageContent(V.VaultPageId));

        Assert.Equal(1, h.Writer.RevokeReadGrant(GrantKeys.SectionKey(V.VaultPath).ToUpperInvariant()));

        Grants.NotGranted(await h.PageContent(V.VaultPageId), V.VaultId);
    }

    [Theory]
    [InlineData("""{"schemaVersion": 1, "results": {"ttlMinutes": 0}}""")]
    [InlineData("""{"schemaVersion": 2}""")]
    public async Task Dialog_SettingsInvalidOrNewer_GrantNotSaved_WorksLikeOnce(string settings)
    {
        using var h = new VaultHarness();
        File.WriteAllText(h.SettingsPath, settings);
        var prompt = new FakePrompt(FakePrompt.Accept(V.VaultName, ReadGrantLifetime.UntilRevoked), FakePrompt.Accept(V.VaultName, ReadGrantLifetime.Hours, 8));

        var root = Grants.Ok(await h.PageContent(V.VaultPageId, prompt));

        Assert.Contains(V.Secret, Grants.Xml(root), StringComparison.Ordinal);
        Assert.Equal([ToolWarnings.GrantNotSaved], Grants.WarningCodes(root));
        Assert.Equal(settings, File.ReadAllText(h.SettingsPath));

        // Not saved, so a once window: covered without a dialog until it ends, then asked again.
        Grants.Ok(await h.PageContent(V.VaultPage2Id, prompt));
        Assert.Single(prompt.Questions);
        h.Clock.Advance(TimeSpan.FromMinutes(5));
        Grants.Ok(await h.PageContent(V.VaultPageId, prompt));
        Assert.Equal(2, prompt.Questions.Count);
    }

    [Fact]
    public async Task StoredGrants_InAnInvalidFile_CountAsAbsent()
    {
        using var h = new VaultHarness();
        h.Grant(V.VaultPath, V.VaultId);
        var json = File.ReadAllText(h.SettingsPath);
        File.WriteAllText(h.SettingsPath, json.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 1, \"allowSilentActivation\": 3", StringComparison.Ordinal));

        Grants.NotGranted(await h.PageContent(V.VaultPageId), V.VaultId);
    }

    [Fact]
    public async Task RenamedOrMovedSection_TheOldGrantNoLongerMatches_AndItAsksAgain()
    {
        using var h = new VaultHarness();
        h.Grant(V.VaultPath, V.VaultId);
        Grants.Ok(await h.PageContent(V.VaultPageId));

        h.Gateway.MoveVault(@"C:\notebooks\A\Renamed.one");
        var prompt = new FakePrompt();

        Grants.NotGranted(await h.PageContent(V.VaultPageId, prompt), V.VaultId);
        Assert.Single(prompt.Questions);
    }

    [Fact]
    public async Task GrantKey_IgnoresCaseAndATrailingSeparator()
    {
        using var h = new VaultHarness();
        h.Grant(V.VaultPath.ToUpperInvariant() + "\\", V.VaultId);

        Grants.Ok(await h.PageContent(V.VaultPageId));
    }

    [Fact]
    public async Task AGrantForOneVault_DoesNotCoverTheOtherVaultWithTheSameName()
    {
        using var h = new VaultHarness();
        h.Grant(V.VaultPath, V.VaultId);

        Grants.Ok(await h.PageContent(V.VaultPageId));
        Grants.NotGranted(await h.PageContent(V.GroupVaultPageId), V.GroupVaultId);
    }

    // --- get_hierarchy -----------------------------------------------------------------------------------------

    [Fact]
    public async Task BroadHierarchy_KeepsTheSections_RemovesTheirPages_ListsThemWithheld_WithOneWarningEach()
    {
        using var h = new VaultHarness();
        var prompt = new FakePrompt();

        var root = Grants.Ok(await h.Hierarchy(V.NotebookId, HierarchyScope.Pages, prompt));

        var xml = XDocument.Parse(Grants.Xml(root));
        Assert.NotNull(SectionOf(xml, V.VaultId));
        Assert.Empty(SectionOf(xml, V.VaultId)!.Elements(One + "Page"));
        Assert.Empty(SectionOf(xml, V.GroupVaultId)!.Elements(One + "Page"));
        Assert.Single(SectionOf(xml, V.OpenSectionId)!.Elements(One + "Page"));
        Assert.Equal([V.VaultId, V.GroupVaultId], Grants.Withheld(root));
        Assert.Equal([ToolWarnings.ReadNotGranted, ToolWarnings.ReadNotGranted], Grants.WarningCodes(root));
        Assert.All(root.GetProperty("warnings").EnumerateArray(), w => Assert.Contains("OneNoteMcp grant --read-section", w.GetProperty("userAction").GetString()!, StringComparison.Ordinal));
        Assert.DoesNotContain("Vault page", Grants.Xml(root), StringComparison.Ordinal);
        Assert.Empty(prompt.Questions);

        // Every other byte is OneNote's (AD-4): only the withheld page spans are gone.
        var raw = await h.Gateway.GetHierarchyAsync(V.NotebookId, HierarchyScope.Pages, TestContext.Current.CancellationToken);
        Assert.Equal(SearchResultFilter.RemovePagesInSections(raw, new HashSet<string> { V.VaultId, V.GroupVaultId }).Xml, Grants.Xml(root));
    }

    [Fact]
    public async Task BroadHierarchy_FromTheRoot_WithOneGrant_KeepsThatSectionsPages()
    {
        using var h = new VaultHarness();
        h.Grant(V.VaultPath, V.VaultId);

        var root = Grants.Ok(await h.Hierarchy(string.Empty, HierarchyScope.Pages));

        var xml = XDocument.Parse(Grants.Xml(root));
        Assert.Equal(2, SectionOf(xml, V.VaultId)!.Elements(One + "Page").Count());
        Assert.Equal([V.GroupVaultId], Grants.Withheld(root));
    }

    [Theory]
    [InlineData(HierarchyScope.Notebooks)]
    [InlineData(HierarchyScope.Sections)]
    [InlineData(HierarchyScope.Children)]
    [InlineData(HierarchyScope.Self)]
    public async Task BroadHierarchy_AboveThePages_WithholdsNothing(HierarchyScope scope)
    {
        using var h = new VaultHarness();

        var root = Grants.Ok(await h.Hierarchy(V.NotebookId, scope));

        Assert.Empty(Grants.Withheld(root));
        Assert.Empty(Grants.WarningCodes(root));
    }

    [Theory]
    [InlineData(HierarchyScope.Self)]
    [InlineData(HierarchyScope.Notebooks)]
    [InlineData(HierarchyScope.Sections)]
    public async Task VaultItself_AtAScopeWithNoPages_IsAllowed(HierarchyScope scope)
    {
        using var h = new VaultHarness();

        var root = Grants.Ok(await h.Hierarchy(V.VaultId, scope, new FakePrompt()));

        Assert.Equal("true", (string?)XDocument.Parse(Grants.Xml(root)).Root!.Attribute("encrypted"));
        Assert.Empty(Grants.WarningCodes(root));
    }

    [Theory]
    [InlineData(HierarchyScope.Children)]
    [InlineData(HierarchyScope.Pages)]
    public async Task VaultItself_AtChildrenOrPages_IsTargetScoped(HierarchyScope scope)
    {
        using var h = new VaultHarness();

        Grants.NotGranted(await h.Hierarchy(V.VaultId, scope), V.VaultId);

        var prompt = new FakePrompt(FakePrompt.Accept(V.VaultName, ReadGrantLifetime.Once));
        var root = Grants.Ok(await h.Hierarchy(V.VaultId, scope, prompt));
        Assert.Contains("Vault page one", Grants.Xml(root), StringComparison.Ordinal);
        Assert.Single(prompt.Questions);
    }

    [Fact]
    public async Task HierarchyOfAVaultPage_IsTargetScoped()
    {
        using var h = new VaultHarness();

        Grants.NotGranted(await h.Hierarchy(V.VaultPageId, HierarchyScope.Self), V.VaultId);
    }

    [Fact]
    public async Task LockedVault_BroadHierarchy_WithholdsIt_AndStillWarnsLocked()
    {
        using var h = new VaultHarness(locked: true);

        var root = Grants.Ok(await h.Hierarchy(V.NotebookId, HierarchyScope.Pages));

        Assert.Equal([V.VaultId, V.GroupVaultId], Grants.Withheld(root));
        Assert.Equal([ToolWarnings.ReadNotGranted, ToolWarnings.ReadNotGranted, ToolWarnings.SectionLocked, ToolWarnings.SectionLocked], Grants.WarningCodes(root));
    }

    // --- Allowed and target-scoped scalar tools --------------------------------------------------------------------

    [Fact]
    public async Task ParentAndLinks_OnTheVaultItself_AreAllowed()
    {
        using var h = new VaultHarness();

        Assert.Equal(V.NotebookId, Grants.Ok(await h.Parent(V.VaultId)).GetProperty("meta").GetProperty("parentId").GetString());
        Grants.Ok(await h.Link(V.VaultId));
        Grants.Ok(await h.WebLink(V.VaultId));
        Grants.Ok(await h.Link(V.NotebookId));
        Grants.Ok(await h.Parent(V.NotebookId));
    }

    [Fact]
    public async Task ParentAndLinks_OnAVaultPage_AreReadNotGranted_AndTheLinkIsNeverAskedFor()
    {
        using var h = new VaultHarness();

        Grants.NotGranted(await h.Parent(V.VaultPageId), V.VaultId);
        Grants.NotGranted(await h.Link(V.VaultPageId), V.VaultId);
        Grants.NotGranted(await h.WebLink(V.VaultPageId), V.VaultId);
        Assert.DoesNotContain(h.Gateway.Calls, c => c.StartsWith("GetHyperlinkToObject", StringComparison.Ordinal) || c.StartsWith("GetWebHyperlinkToObject", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ParentAndLinks_OnAVaultPage_WithAGrant_Pass()
    {
        using var h = new VaultHarness();
        h.Grant(V.VaultPath, V.VaultId);

        Assert.Equal(V.VaultId, Grants.Ok(await h.Parent(V.VaultPageId)).GetProperty("meta").GetProperty("parentId").GetString());
        Grants.Ok(await h.Link(V.VaultPageId));
    }

    // --- Search -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task FindPages_DropsVaultCandidates_BeforeReadingOrCaching_NeverInFiltered()
    {
        using var h = new VaultHarness();
        var cache = PageTextCache.WithDefaults();

        // "SECRET" is in every vault page's text: a leak would show as a match.
        var root = Grants.Ok(await h.FindPages("SECRET", V.NotebookId, cache));

        Assert.Empty(XDocument.Parse(Grants.Xml(root)).Descendants(One + "Page"));
        Assert.Equal([V.VaultId, V.GroupVaultId], Grants.Withheld(root));
        Assert.Equal([ToolWarnings.ReadNotGranted, ToolWarnings.ReadNotGranted], Grants.WarningCodes(root));
        var filtered = root.GetProperty("meta").GetProperty("filtered").EnumerateArray().Select(f => f.GetProperty("pageId").GetString()).ToList();
        Assert.Equal([V.OpenPageId, V.DeletedPageId], filtered);
        Assert.DoesNotContain(h.Gateway.ContentCalls, c => c.Contains("{SV", StringComparison.Ordinal));
        Assert.Equal(1, cache.Count);

        // The scope is read at sections level, before the search.
        var scope = h.Gateway.Calls.IndexOf($"GetHierarchy:{V.NotebookId}:{HierarchyScope.Sections}");
        Assert.True(scope >= 0 && scope < h.Gateway.Calls.IndexOf($"FindPages:{V.NotebookId}"));
    }

    [Fact]
    public async Task FindPages_ListsEveryProtectedSectionInScope_WhetherOrNotItHadAHit()
    {
        using var h = new VaultHarness();

        var root = Grants.Ok(await h.FindPages("Open text", V.NotebookId));

        Assert.Equal([V.OpenPageId], XDocument.Parse(Grants.Xml(root)).Descendants(One + "Page").Select(p => (string)p.Attribute("ID")!));
        Assert.Equal([V.VaultId, V.GroupVaultId], Grants.Withheld(root));
    }

    [Fact]
    public async Task FindPages_WithAGrant_SearchesThatSection()
    {
        using var h = new VaultHarness();
        h.Grant(V.VaultPath, V.VaultId);

        var root = Grants.Ok(await h.FindPages("SECRET", string.Empty));

        Assert.Equal([V.VaultPageId, V.VaultPage2Id], XDocument.Parse(Grants.Xml(root)).Descendants(One + "Page").Select(p => (string)p.Attribute("ID")!));
        Assert.Equal([V.GroupVaultId], Grants.Withheld(root));
    }

    [Fact]
    public async Task FindPages_FromTheVaultItself_WithholdsEverything()
    {
        using var h = new VaultHarness();

        var root = Grants.Ok(await h.FindPages("SECRET", V.VaultId));

        Assert.Empty(XDocument.Parse(Grants.Xml(root)).Descendants(One + "Page"));
        Assert.Equal([V.VaultId], Grants.Withheld(root));
        Assert.Empty(h.Gateway.ContentCalls);
    }

    [Fact]
    public async Task FindPages_FromTheVaultItself_SearchXmlWithoutTheSectionWrapper_StillWithholdsEverything()
    {
        using var h = new VaultHarness();
        h.Gateway.SearchXml =
            "<?xml version=\"1.0\"?>\n<one:Notebooks xmlns:one=\"" + OneNoteXml.Namespace2013 + "\">"
            + "<one:Page ID=\"" + V.VaultPageId + "\" name=\"Vault page one\" lastModifiedTime=\"2026-11-07T00:00:00.000Z\" />"
            + "<one:Page ID=\"" + V.VaultPage2Id + "\" name=\"Vault page two\" lastModifiedTime=\"2026-11-07T00:00:00.000Z\" /></one:Notebooks>";

        var root = Grants.Ok(await h.FindPages("SECRET", V.VaultId));

        Assert.Empty(XDocument.Parse(Grants.Xml(root)).Descendants(One + "Page"));
        Assert.Equal([V.VaultId], Grants.Withheld(root));
        Assert.Empty(root.GetProperty("meta").GetProperty("filtered").EnumerateArray());
        Assert.Empty(h.Gateway.ContentCalls);
    }

    [Fact]
    public async Task FindPages_APageOutsideAnySectionWrapper_IsWithheld_WhenTheScopeWithholdsASection()
    {
        // Fail closed: the protected page's section can't be told from the XML, and the scope has a withheld Vault.
        using var h = new VaultHarness();
        h.Gateway.SearchXml =
            "<?xml version=\"1.0\"?>\n<one:Notebook xmlns:one=\"" + OneNoteXml.Namespace2013 + "\" ID=\"" + V.NotebookId + "\" name=\"Notebook A\">"
            + "<one:Section ID=\"" + V.OpenSectionId + "\" name=\"Open\"><one:Page ID=\"" + V.OpenPageId + "\" name=\"Open page\" lastModifiedTime=\"2026-11-07T00:00:00.000Z\" /></one:Section>"
            + "<one:Page ID=\"" + V.VaultPageId + "\" name=\"Vault page one\" lastModifiedTime=\"2026-11-07T00:00:00.000Z\" /></one:Notebook>";

        var root = Grants.Ok(await h.FindPages("SECRET", V.NotebookId));

        Assert.Empty(XDocument.Parse(Grants.Xml(root)).Descendants(One + "Page"));
        Assert.DoesNotContain(V.VaultPageId, root.GetProperty("meta").GetProperty("filtered").EnumerateArray().Select(f => f.GetProperty("pageId").GetString()));
        Assert.Equal([V.VaultId, V.GroupVaultId], Grants.Withheld(root));
        Assert.DoesNotContain(h.Gateway.ContentCalls, c => c.Contains("{SV", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Dialog_SectionWithNoPath_IsReadNotGranted_WithoutAsking_AndSavesNothing()
    {
        using var h = new VaultHarness();
        h.Gateway.MoveVault(string.Empty);
        var prompt = new FakePrompt(FakePrompt.Accept(V.VaultName, ReadGrantLifetime.UntilRevoked));

        var error = Grants.NotGranted(await h.PageContent(V.VaultPageId, prompt), V.VaultId);

        Assert.Contains("no path", error.GetProperty("message").GetString()!, StringComparison.Ordinal);
        Assert.Empty(prompt.Questions);
        Assert.False(File.Exists(h.SettingsPath));
    }

    [Fact]
    public async Task FindMeta_DropsVaultCandidates_NeverInFiltered()
    {
        using var h = new VaultHarness();

        var root = Grants.Ok(await h.FindMeta(V.NotebookId));

        Assert.Equal([V.OpenPageId], XDocument.Parse(Grants.Xml(root)).Descendants(One + "Page").Select(p => (string)p.Attribute("ID")!));
        Assert.Equal([V.VaultId, V.GroupVaultId], Grants.Withheld(root));
        Assert.Equal([V.DeletedPageId], root.GetProperty("meta").GetProperty("filtered").EnumerateArray().Select(f => f.GetProperty("pageId").GetString()));
    }

    [Fact]
    public async Task OpenNotebookWithNoProtectedSection_HasNoWithheldField()
    {
        using var h = new VaultHarness();

        var root = Grants.Ok(await h.FindPages("Open", V.OpenSectionId));

        Assert.False(root.GetProperty("meta").TryGetProperty("withheld", out _));
    }

    // --- read_result -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ReadResult_UnderAOnceGrant_ServesParts_UntilTtlAfterTheOriginalCall_ReadsDontExtendIt()
    {
        using var h = new VaultHarness(inlineMaxChars: 20_000);
        h.Gateway.PageXml[V.VaultPageId] = BigXml.Page(outlines: 30, oes: 10);

        var index = Grants.Ok(await h.PageContent(V.VaultPageId, new FakePrompt(FakePrompt.Accept(V.VaultName, ReadGrantLifetime.Once))));
        var id = Envelope.ResultId(index);

        h.Clock.Advance(TimeSpan.FromMinutes(20));
        Grants.Ok(await h.Read(id, @ref: "{O0}{1}{B0}"));

        // A read at 20 minutes would extend a normal entry to 50; a once entry still ends at 30.
        h.Clock.Advance(TimeSpan.FromMinutes(10));
        var error = Envelope.Error(await h.Read(id, @ref: "{O1}{1}{B0}"));
        Assert.Equal("ResultExpired", error.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ReadResult_PartFromAResultMadeUnderALapsedGrant_IsReadNotGranted_WithNoDialog()
    {
        using var h = new VaultHarness(inlineMaxChars: 20_000);
        h.Gateway.PageXml[V.VaultPageId] = BigXml.Page(outlines: 30, oes: 10);
        h.Grant(V.VaultPath, V.VaultId, h.Clock.GetUtcNow().AddHours(1));

        var id = Envelope.ResultId(Grants.Ok(await h.PageContent(V.VaultPageId)));
        Grants.Ok(await h.Read(id, @ref: "{O0}{1}{B0}"));

        // Reads every 20 minutes keep the entry alive (30-minute TTL) past the grant's hour.
        h.Clock.Advance(TimeSpan.FromMinutes(20));
        Grants.Ok(await h.Read(id, @ref: "{O0}{1}{B0}"));
        h.Clock.Advance(TimeSpan.FromMinutes(20));
        Grants.Ok(await h.Read(id, @ref: "{O0}{1}{B0}"));
        h.Clock.Advance(TimeSpan.FromMinutes(21));
        Grants.NotGranted(await h.Read(id, @ref: "{O0}{1}{B0}"), V.VaultId);
        Grants.NotGranted(await h.Read(id, expand: "/"), V.VaultId);
    }

    [Fact]
    public async Task ReadResult_AfterARevoke_IsReadNotGranted()
    {
        using var h = new VaultHarness(inlineMaxChars: 20_000);
        h.Gateway.PageXml[V.VaultPageId] = BigXml.Page(outlines: 30, oes: 10);
        h.Grant(V.VaultPath, V.VaultId);

        var id = Envelope.ResultId(Grants.Ok(await h.PageContent(V.VaultPageId)));
        h.Writer.RevokeReadGrant(GrantKeys.SectionKey(V.VaultPath));

        Grants.NotGranted(await h.Read(id, @ref: "/", offset: 0), V.VaultId);
    }

    [Fact]
    public void ResultGrantCheck_ABroadResult_RefusesOnlyPartsThatOverlapTheLapsedSection()
    {
        // A pages-scope hierarchy with one granted protected section: once revoked, the open section's part is still served.
        var xml = BigXml.Hierarchy(sections: 3, pages: 2);
        var tree = ElementSpans.Hierarchy(xml);
        var entry = new StoredResult(
            "r1",
            ResultKind.HierarchyXml,
            tree,
            null,
            ToolSuccess.WithXml(xml, new ToolMeta("c")),
            new int[tree.Nodes.Count],
            new ResultGrants([new ResultGrant("k", "{S1}{1}{B0}", ReadGrantLifetime.UntilRevoked, null, WholeResult: false)]));
        var check = new ResultGrantCheck(ReadAccess_NoSettings, TimeProvider.System);

        var open = tree.Find("{S0}{1}{B0}")!;
        var protectedSection = tree.Find("{S1}{1}{B0}")!;
        check.Check(entry, open.Start, open.End);
        var ex = Assert.Throws<OneNoteException>(() => { check.Check(entry, protectedSection.Start, protectedSection.End); });
        Assert.Equal(ErrorCode.ReadNotGranted, ex.Code);
        Assert.Throws<OneNoteException>(() => { check.Check(entry, 0, xml.Length); });
    }

    // --- list_grants -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ListGrants_ShowsStoredGrants_WithStatus()
    {
        using var h = new VaultHarness();
        h.Grant(V.VaultPath, V.VaultId, h.Clock.GetUtcNow().AddHours(1));
        h.Grant(V.GroupVaultPath, V.GroupVaultId);
        h.Grant(@"C:\gone\Closed.one", "{X}{1}{B0}");

        var root = Grants.Ok(await h.ListGrants());
        var grants = root.GetProperty("meta").GetProperty("grants").EnumerateArray().ToList();
        Assert.Equal(3, grants.Count);
        Assert.All(grants, g => Assert.Equal("readSection", g.GetProperty("kind").GetString()));
        var byKey = grants.ToDictionary(g => g.GetProperty("key").GetString()!, g => g, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("active", byKey[V.VaultPath].GetProperty("status").GetString());
        Assert.Equal("hours", byKey[V.VaultPath].GetProperty("lifetime").GetString());
        Assert.Equal("untilRevoked", byKey[V.GroupVaultPath].GetProperty("lifetime").GetString());
        Assert.Equal("sectionNotOpen", byKey[@"C:\gone\Closed.one"].GetProperty("status").GetString());

        h.Clock.Advance(TimeSpan.FromHours(1));
        var later = Grants.Ok(await h.ListGrants()).GetProperty("meta").GetProperty("grants").EnumerateArray();
        Assert.Contains(later, g => g.GetProperty("status").GetString() == "expired");
    }

    [Fact]
    public async Task ListGrants_ShowsALiveOnceGrant_UntilItsWindowEnds()
    {
        using var h = new VaultHarness();
        var granted = h.Clock.GetUtcNow();
        Grants.Ok(await h.PageContent(V.VaultPageId, new FakePrompt(FakePrompt.Accept(V.VaultName, ReadGrantLifetime.Once))));

        var grant = Assert.Single(Grants.Ok(await h.ListGrants()).GetProperty("meta").GetProperty("grants").EnumerateArray());
        Assert.Equal(
            ("readSection", V.VaultPath, "once", "elicitation", "active", V.VaultId),
            (grant.GetProperty("kind").GetString(), grant.GetProperty("key").GetString(), grant.GetProperty("lifetime").GetString(), grant.GetProperty("grantedVia").GetString(), grant.GetProperty("status").GetString(), grant.GetProperty("idAtGrant").GetString()));
        Assert.Equal(SettingsWriter.Format(granted.AddMinutes(5)), grant.GetProperty("expiresAt").GetString());

        h.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(0, Grants.Ok(await h.ListGrants()).GetProperty("meta").GetProperty("grants").GetArrayLength());
    }

    [Fact]
    public async Task ListGrants_NoGrants_MakesNoComCall()
    {
        using var h = new VaultHarness();

        var root = Grants.Ok(await h.ListGrants());

        Assert.Equal(0, root.GetProperty("meta").GetProperty("grants").GetArrayLength());
        Assert.Empty(h.Gateway.Calls);
    }

    [Fact]
    public async Task ListGrants_InvalidSettings_StillLists_WithOneSettingsInvalidWarning()
    {
        using var h = new VaultHarness();
        File.WriteAllText(h.SettingsPath, "{ not json");

        var root = Grants.Ok(await h.ListGrants());

        Assert.Equal(0, root.GetProperty("meta").GetProperty("grants").GetArrayLength());
        Assert.Equal([ToolWarnings.SettingsInvalid], Grants.WarningCodes(root));
    }

    [Fact]
    public async Task ListGrants_KeysDifferingOnlyInCase_ListsBoth_SaysTheyCountAsAbsent_AndHowToRevoke()
    {
        using var h = new VaultHarness();
        var entry = """{"name": "Vault", "notebookKey": "", "idAtGrant": "i", "grantedAt": "2026-11-06T12:00:00Z", "grantedVia": "cli"}""";
        File.WriteAllText(h.SettingsPath, """{"schemaVersion": 1, "protectedSections": {""" + System.Text.Json.JsonSerializer.Serialize(V.VaultPath) + ": " + entry + ", " + System.Text.Json.JsonSerializer.Serialize(V.VaultPath.ToUpperInvariant()) + ": " + entry + "}}");

        var root = Grants.Ok(await h.ListGrants());

        Assert.Equal(2, root.GetProperty("meta").GetProperty("grants").GetArrayLength());
        var warning = Assert.Single(root.GetProperty("warnings").EnumerateArray());
        Assert.Equal(ToolWarnings.SettingsInvalid, warning.GetProperty("code").GetString());
        Assert.Contains("grant --revoke --read-section", warning.GetProperty("userAction").GetString()!, StringComparison.Ordinal);

        // They still count as absent for reads.
        Grants.NotGranted(await h.PageContent(V.VaultPageId), V.VaultId);
    }

    private static SettingsSnapshot ReadAccess_NoSettings() => new SettingsStore(Path.Combine(Path.GetTempPath(), "onenote-mcp-tests", "absent", "settings.json")).Read();

    private static XElement? SectionOf(XDocument xml, string id) =>
        xml.Descendants(One + "Section").FirstOrDefault(s => (string?)s.Attribute("ID") == id);
}
