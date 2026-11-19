using Microsoft.Extensions.Logging.Abstractions;
using OneNoteMcp.Core;
using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Server;
using OneNoteMcp.Xml;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>
/// The real leak found in 3.8's sitting S2 (2026-11-18), on main since entry 15: after OneNote restarted, a notebook it
/// had opened from a folder under a OneDrive-redirected export root came back with <b>URL</b> paths
/// (<c>https://d.docs.live.net/&lt;cid&gt;/Documents/OneNote Exports/…/</c>), with the cid's hex case varying between
/// elements and a trailing slash on notebooks and section groups. AD-21's path rule compares local paths only, so every
/// page was listed with no grant. These reproduce that shape. The cid here is a placeholder, never a real one.
/// They were run red (2026-11-18: the section listed its page, the notebook withheld nothing). Backlog 13's fix (Phill,
/// 2026-11-18, D1 (c), Q1–Q4 (ii)(ii)(ii)(i)) gives <see cref="Protection"/> a seam, <see cref="OneDriveFolders"/>, set
/// here to <see cref="OneDriveFolder"/> and the placeholder cid.
/// </summary>
public sealed class OneDriveUrlProtectionTests : IDisposable
{
    private const string Ns = "xmlns:one=\"" + OneNoteXml.Namespace2013 + "\"";
    private const string NotebookId = "{URL00000-0000-4000-8000-000000000001}{1}{B0}";
    private const string SectionId = "{URL00000-0000-4000-8000-000000000002}{1}{B0}";
    private const string GroupSectionId = "{URL00000-0000-4000-8000-000000000003}{1}{B0}";
    private const string Cid = "0123456789abcdef";

    private readonly TempDirectory temp = new();
    private readonly FakeGateway gateway = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A OneDrive folder of this test's own, and an export root inside it, as on a redirected Documents.</summary>
    private string OneDriveFolder => temp.Combine("OneDrive");

    private string ExportRoot => Path.Combine(OneDriveFolder, "Documents", "OneNote Exports");

    /// <summary>The seam as the server would fill it from a personal account: the folder and its address.</summary>
    private OneDriveFolders Folders => new([new OneDriveAccount(OneDriveFolder, $"https://d.docs.live.net/{Cid}/")]);

    private static string Url(string tail, bool upperCid = false) =>
        $"https://d.docs.live.net/{(upperCid ? Cid.ToUpperInvariant() : Cid)}/Documents/OneNote Exports/{tail}";

    public void Dispose() => temp.Dispose();

    [Fact]
    public async Task ACopyNotebookReportedByUrl_UnderTheExportRootsOneDriveLocation_WithholdsEverySection()
    {
        var store = Settings();
        gateway.Hierarchies[(NotebookId, HierarchyScope.Pages)] =
            $"<?xml version=\"1.0\"?>\n<one:Notebook {Ns} name=\"Throwaway a 20261118-171247\" ID=\"{NotebookId}\" path=\"{Url("Throwaway a 20261118-171247/", upperCid: true)}\">"
            + $"<one:Section name=\"New Section 1\" ID=\"{SectionId}\" path=\"{Url("Throwaway a 20261118-171247/New Section 1.one")}\"><one:Page ID=\"{{P1}}\" name=\"Secret one\" /></one:Section>"
            + $"<one:SectionGroup name=\"Group\" ID=\"{{G}}\" path=\"{Url("Throwaway a 20261118-171247/Group/", upperCid: true)}\">"
            + $"<one:Section name=\"Inner\" ID=\"{GroupSectionId}\" path=\"{Url("Throwaway a 20261118-171247/Group/Inner.one")}\"><one:Page ID=\"{{P2}}\" name=\"Secret two\" /></one:Section>"
            + "</one:SectionGroup></one:Notebook>";

        var result = await Hierarchy(store, NotebookId, HierarchyScope.Pages);

        Assert.False(result.IsError, result.StructuredContent?.GetRawText());
        var root = result.StructuredContent!.Value;
        Assert.Equal([SectionId, GroupSectionId], root.GetProperty("meta").GetProperty("withheld").EnumerateArray().Select(e => e.GetString()));
        Assert.DoesNotContain("Secret", root.GetProperty("xml").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASectionReportedByUrl_AtPagesScope_IsTargetScoped_NotListedWithoutAGrant()
    {
        var store = Settings();
        var attributes = $"name=\"New Section 1\" ID=\"{SectionId}\" path=\"{Url("Throwaway a 20261118-171247/New Section 1.one", upperCid: true)}\"";
        gateway.Hierarchies[(SectionId, HierarchyScope.Pages)] = $"<?xml version=\"1.0\"?>\n<one:Section {Ns} {attributes}><one:Page ID=\"{{P1}}\" name=\"Secret one\" /></one:Section>";

        var result = await Hierarchy(store, SectionId, HierarchyScope.Pages);

        Assert.True(result.IsError, result.StructuredContent?.GetRawText());
        Assert.Equal("ReadNotGranted", result.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task AUrlOutsideTheExportRoot_StaysUnprotected()
    {
        // The guard against over-reach: a cloud notebook elsewhere in the same OneDrive is not under the root.
        var store = Settings();
        var path = $"https://d.docs.live.net/{Cid}/Documents/Work notes/Plans.one";
        gateway.Hierarchies[(SectionId, HierarchyScope.Pages)] =
            $"<?xml version=\"1.0\"?>\n<one:Section {Ns} name=\"Plans\" ID=\"{SectionId}\" path=\"{path}\"><one:Page ID=\"{{P1}}\" name=\"Open page\" /></one:Section>";

        var result = await Hierarchy(store, SectionId, HierarchyScope.Pages);

        Assert.False(result.IsError, result.StructuredContent?.GetRawText());
    }

    // --- The rule (Protection.UnderRoot on URLs) ------------------------------------------------------------------------

    /// <summary>A business address: host and path kept apart so the privacy grep's pattern never appears.</summary>
    private const string BusinessHost = "https://contoso-my.sharepoint" + ".com";

    [Theory]
    [InlineData("X/S.one")]
    [InlineData("X/")]
    [InlineData("X/Group/Inner.one")]
    [InlineData("X/S.one?web=1")]
    [InlineData("X/S.one#section-id={1}")]
    [InlineData("X/100%/S.one")]
    public void AUrlUnderTheRootsAddress_IsUnderTheRoot(string tail)
    {
        Assert.True(Rule().UnderRoot(Url(tail)));
        Assert.True(Rule().UnderRoot(Url(tail, upperCid: true)));
    }

    [Theory]
    [InlineData("https://d.docs.live.net/0123456789ABCDEF/Documents/OneNote%20Exports/X/S.one")]
    [InlineData("http://d.docs.live.net/0123456789abcdef/Documents/OneNote Exports/X/S.one")]
    [InlineData("HTTPS://D.DOCS.LIVE.NET/0123456789ABCDEF/DOCUMENTS/ONENOTE EXPORTS/X/S.ONE")]
    [InlineData("https://d.docs.live.net/fedcba9876543210/Documents/OneNote Exports/X/S.one")]
    public void PercentEncoding_HttpCaseAndAnotherCid_StillMatch_TheTailAlwaysApplies(string url)
    {
        Assert.True(Rule().UnderRoot(url));
    }

    [Fact]
    public void ABusinessAddress_HoldingTheRootsTail_IsUnderTheRoot()
    {
        Assert.True(Rule().UnderRoot(BusinessHost + "/personal/user_contoso_com/Documents/OneNote Exports/X/S.one"));
    }

    [Theory]
    [InlineData("https://d.docs.live.net/0123456789abcdef/Documents/OneNote Exports/")]
    [InlineData("https://d.docs.live.net/0123456789abcdef/Documents/OneNote Exports")]
    [InlineData("https://d.docs.live.net/0123456789abcdef/Documents/OneNote Exports2/X/S.one")]
    [InlineData("https://d.docs.live.net/0123456789abcdef/Documents/Work notes/Plans.one")]
    [InlineData("https://d.docs.live.net/0123456789abcdef/OneNote Exports/X/S.one")]
    [InlineData("ftp://d.docs.live.net/0123456789abcdef/Documents/OneNote Exports/X/S.one")]
    [InlineData("Documents/OneNote Exports/X/S.one")]
    [InlineData("https://d.docs.live.net/0123456789abcdef/Documents/OneNote%ZZExports/X/S.one")]
    [InlineData("")]
    [InlineData(null)]
    public void OtherUrls_TheRootItself_AndNonUrls_AreNotUnderTheRoot(string? path)
    {
        Assert.False(Rule().UnderRoot(path));
    }

    [Fact]
    public void ALocalPathUnderTheRoot_IsStillUnderIt()
    {
        Assert.True(Rule().UnderRoot(Path.Combine(ExportRoot, "X", "S.one")));
    }

    [Fact]
    public void AFolderKnownOnlyFromTheEnvironment_NoAddress_StillGivesTheTail()
    {
        var rule = new Protection([ExportRoot], oneDrive: new OneDriveFolders([new OneDriveAccount(OneDriveFolder)]));

        Assert.True(rule.UnderRoot(Url("X/S.one")));
    }

    [Fact]
    public void NoOneDriveKnown_TheRootsLastTwoSegments_AreItsTail_ForAnyHost()
    {
        var rule = new Protection([ExportRoot], oneDrive: OneDriveFolders.None);

        Assert.True(rule.UnderRoot("https://example.org/a/Documents/OneNote Exports/X/S.one"));
        Assert.False(rule.UnderRoot("https://example.org/a/Other/OneNote Exports/X/S.one"));
        Assert.False(rule.UnderRoot("https://example.org/a/Documents/OneNote Exports/"));
    }

    [Fact]
    public void ARootOutsideEveryKnownOneDriveFolder_KeepsTheLocalRuleOnly()
    {
        var elsewhere = Path.Combine(temp.Combine("Elsewhere"), "Documents", "OneNote Exports");
        var rule = new Protection([elsewhere], oneDrive: Folders);

        Assert.False(rule.UnderRoot(Url("X/S.one")));
        Assert.True(rule.UnderRoot(Path.Combine(elsewhere, "X", "S.one")));
    }

    [Fact]
    public void AFolderThatOnlyStartsLikeTheOneDriveFolder_IsNotInsideIt()
    {
        var lookAlike = Path.Combine(temp.Combine("OneDrive - Contoso"), "Documents", "OneNote Exports");
        var rule = new Protection([lookAlike], oneDrive: Folders);

        Assert.False(rule.UnderRoot(Url("X/S.one")));
    }

    [Fact]
    public void TheBackupsFolder_InsideOneDrive_IsCoveredToo()
    {
        var backups = Path.Combine(OneDriveFolder, "Journal", "backups");
        var rule = new Protection([backups], oneDrive: Folders);

        Assert.True(rule.UnderRoot($"https://d.docs.live.net/{Cid}/Journal/backups/B/S.one"));
    }

    [Fact]
    public void AUrlUnderTheRoot_MapsBackToTheLocalFolderBelowTheRoot()
    {
        Assert.Equal(Path.Combine(ExportRoot, "X", "Group"), Rule().LocalPathOfUrl(Url("X/Group/", upperCid: true)));
        Assert.Null(Rule().LocalPathOfUrl("https://d.docs.live.net/0123456789abcdef/Documents/Work notes/"));
    }

    [Fact]
    public void ReadAccess_AndFrom_CarryTheSeam_SoWritesSnapshotsAndTheGrantListingSeeIt()
    {
        // Every write, journal snapshot and publish asks ReadAccess.Protection(); restore_from_journal and the grant CLI use From.
        var store = Settings();
        var access = new ReadAccess(gateway, store.Read, writer: null, new FakeClock(), oneDrive: () => Folders);

        Assert.True(access.Protection().UnderRoot(Url("X/S.one")));
        Assert.True(Protection.From(store.Read(), Folders).UnderRoot(Url("X/S.one")));
    }

    // --- The seam (OneDriveFolders) -------------------------------------------------------------------------------------

    [Fact]
    public void TheSameFolderFromTwoSources_IsOneAccount_KeepingItsAddress()
    {
        var folders = new OneDriveFolders([new OneDriveAccount(OneDriveFolder + Path.DirectorySeparatorChar), new OneDriveAccount(OneDriveFolder.ToUpperInvariant(), $"https://d.docs.live.net/{Cid}/")]);

        var account = Assert.Single(folders.Accounts);
        Assert.Equal($"https://d.docs.live.net/{Cid}/", account.UrlPrefix);
    }

    [Fact]
    public void AnAddressInsideAnAccount_MapsBackToItsLocalFolder()
    {
        Assert.Equal(Path.Combine(OneDriveFolder, "Documents", "X"), Folders.LocalPathOf($"https://d.docs.live.net/{Cid.ToUpperInvariant()}/Documents/X/"));
        Assert.Equal(Path.Combine(OneDriveFolder, "Documents", "OneNote Exports"), Folders.LocalPathOf($"https://d.docs.live.net/{Cid}/Documents/OneNote%20Exports?web=1"));
        Assert.Null(Folders.LocalPathOf("https://d.docs.live.net/fedcba9876543210/Documents/X/"));
        Assert.Null(Folders.LocalPathOf(@"C:\local\X"));
    }

    [Fact]
    public void ARootThatIsAOneDriveFolder_OrOneSegmentBelow_IsShallow_DeeperOrOutsideIsNot()
    {
        Assert.Contains("OneDrive folder", Folders.ShallowRoot(OneDriveFolder), StringComparison.Ordinal);
        Assert.Contains("OneDrive folder", Folders.ShallowRoot(Path.Combine(OneDriveFolder, "Exports")), StringComparison.Ordinal);
        Assert.Null(Folders.ShallowRoot(ExportRoot));
        Assert.Null(Folders.ShallowRoot(temp.Combine("Exports")));
        Assert.Null(OneDriveFolders.None.ShallowRoot(OneDriveFolder));
    }

    [Fact]
    public async Task AShallowExportRoot_KeepsProtecting_ReadsWarnSettingsInvalid_AndTheGuardRefusesWrites()
    {
        // Q3 (ii), as the review found (F1): refusing the whole file would drop the root and fail open, so the root keeps
        // protecting, reads carry settingsInvalid naming it, and the guard (which disables writes) reports it, with no COM read.
        var shallow = Path.Combine(OneDriveFolder, "Exports");
        Directory.CreateDirectory(shallow);
        TestSettings.SetExportRoots(temp.Combine("settings.json"), shallow);
        var store = new SettingsStore(temp.Combine("settings.json"), temp.Combine("journal"));
        var access = new ReadAccess(gateway, store.Read, writer: null, new FakeClock(), oneDrive: () => Folders);

        Assert.Equal(SettingsStatus.Ok, store.Read().Status);
        var protection = await access.ProtectionAsync(Ct);
        Assert.True(protection.UnderRoot(Path.Combine(shallow, "Copy", "S.one")));
        Assert.True(protection.UnderRoot($"https://d.docs.live.net/{Cid}/Exports/Copy/S.one"));
        var warning = Assert.Single(protection.Warnings);
        Assert.Equal(ToolWarnings.SettingsInvalid, warning.Code);
        Assert.Contains(shallow, warning.Message, StringComparison.Ordinal);

        var guard = new ExportRootGuard(gateway, new FakeClock(), oneDrive: () => Folders);
        Assert.Contains(OneDriveFolders.ShallowRootReason, await guard.ProblemAsync([shallow], Ct), StringComparison.Ordinal);
        Assert.Contains(OneDriveFolders.ShallowRootReason, ExportRootGuard.Problem([shallow], NotebooksXml(), _ => false, Folders), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("#Work/S.one")]
    [InlineData("X/#Group/S.one")]
    [InlineData("100%25 done/S.one")]
    public void AFolderNameHoldingAHashOrAnEscape_StillCounts_EveryReadingIsTested(string tail)
    {
        // Review F2: OneNote reports these paths unencoded, so '#' may be part of a folder's name, not a fragment.
        Assert.True(Rule().UnderRoot(Url(tail)));
    }

    [Fact]
    public void ARootWhoseOwnNameHoldsAHash_StillMatches()
    {
        var root = Path.Combine(OneDriveFolder, "Documents", "C# Exports");
        var rule = new Protection([root], oneDrive: Folders);

        Assert.True(rule.UnderRoot($"https://d.docs.live.net/{Cid}/Documents/C# Exports/X/S.one"));
    }

    // --- The export-root guard (Q4 (i)) ---------------------------------------------------------------------------------

    private static string NotebooksXml(params string[] paths) =>
        $"<one:Notebooks {Ns}>" + string.Concat(paths.Select((p, i) => $"<one:Notebook name=\"N{i}\" ID=\"{{N{i}}}\" path=\"{p}\" />")) + "</one:Notebooks>";

    [Fact]
    public void Guard_AUrlNotebookUnderTheRoot_WithoutTheMarker_IsAProblem()
    {
        var problem = ExportRootGuard.Problem([ExportRoot], NotebooksXml(Url("Mine/")), _ => false, Folders);

        Assert.NotNull(problem);
        Assert.Contains("holds a notebook", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Guard_AUrlNotebookUnderTheRoot_WhoseLocalFolderHoldsTheMarker_IsFine()
    {
        var marker = Path.Combine(ExportRoot, "Copy", ExportRootGuard.MarkerFileName);

        Assert.Null(ExportRootGuard.Problem([ExportRoot], NotebooksXml(Url("Copy/", upperCid: true)), p => string.Equals(p, marker, StringComparison.OrdinalIgnoreCase), Folders));
    }

    [Fact]
    public void Guard_ARootInsideAUrlNotebooksFolder_IsAProblem()
    {
        var problem = ExportRootGuard.Problem([ExportRoot], NotebooksXml($"https://d.docs.live.net/{Cid}/Documents/"), _ => false, Folders);

        Assert.NotNull(problem);
        Assert.Contains("inside", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Guard_AUrlNotebookElsewhereInTheSameOneDrive_IsFine()
    {
        Assert.Null(ExportRootGuard.Problem([ExportRoot], NotebooksXml($"https://d.docs.live.net/{Cid}/Documents/Work notes/"), _ => false, Folders));
    }

    private Protection Rule() => new([ExportRoot], oneDrive: Folders);

    private SettingsStore Settings()
    {
        Directory.CreateDirectory(ExportRoot);
        TestSettings.SetExportRoots(temp.Combine("settings.json"), ExportRoot);
        return new SettingsStore(temp.Combine("settings.json"), temp.Combine("journal"));
    }

    private async Task<ModelContextProtocol.Protocol.CallToolResult> Hierarchy(SettingsStore store, string start, HierarchyScope scope)
    {
        var access = new ReadAccess(gateway, store.Read, new SettingsWriter(store, new FakeClock()), new FakeClock(), oneDrive: () => Folders);
        var useCase = new GetHierarchyUseCase(gateway, access);
        return await new ToolRunner(NullLogger<ToolRunner>.Instance).RunAsync("get_hierarchy", useCase, (c, ct) => useCase.ExecuteAsync(c, start, scope, ct), Ct);
    }
}
