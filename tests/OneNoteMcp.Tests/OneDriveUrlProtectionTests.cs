using Microsoft.Extensions.Logging.Abstractions;
using OneNoteMcp.Core;
using OneNoteMcp.Core.Access;
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
/// They were run red (2026-11-18: the section listed its page, the notebook withheld nothing) and are skipped until the fix Phill picks lands (an AD-21 change); the fix gives Protection a seam for the OneDrive folder-to-URL mapping, which these then set to <see cref="OneDriveFolder"/> and the placeholder cid.
/// </summary>
public sealed class OneDriveUrlProtectionTests : IDisposable
{
    private const string Skip = "Red until the OneDrive URL fix (AD-21 change, Phill's pick, 2026-11-18): reproduces the S2 leak.";
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

    private static string Url(string tail, bool upperCid = false) =>
        $"https://d.docs.live.net/{(upperCid ? Cid.ToUpperInvariant() : Cid)}/Documents/OneNote Exports/{tail}";

    public void Dispose() => temp.Dispose();

    [Fact(Skip = Skip)]
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

    [Fact(Skip = Skip)]
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

    private SettingsStore Settings()
    {
        Directory.CreateDirectory(ExportRoot);
        TestSettings.SetExportRoots(temp.Combine("settings.json"), ExportRoot);
        return new SettingsStore(temp.Combine("settings.json"), temp.Combine("journal"));
    }

    private async Task<ModelContextProtocol.Protocol.CallToolResult> Hierarchy(SettingsStore store, string start, HierarchyScope scope)
    {
        var access = new ReadAccess(gateway, store.Read, new SettingsWriter(store, new FakeClock()), new FakeClock());
        var useCase = new GetHierarchyUseCase(gateway, access);
        return await new ToolRunner(NullLogger<ToolRunner>.Instance).RunAsync("get_hierarchy", useCase, (c, ct) => useCase.ExecuteAsync(c, start, scope, ct), Ct);
    }
}
