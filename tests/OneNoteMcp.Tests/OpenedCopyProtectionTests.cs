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
/// The 3.8 S2 report (2026-11-18): a section of a copy <c>open_package</c> opened under the default export root
/// (Documents\OneNote Exports) listed its pages through <c>get_hierarchy</c> scope <c>pages</c> with no grant. These
/// reproduce that read on <see cref="FakeGateway"/> with the section shaped as S2 saw it, under the default root.
/// </summary>
public sealed class OpenedCopyProtectionTests : IDisposable
{
    private const string Ns = "xmlns:one=\"" + OneNoteXml.Namespace2013 + "\"";
    private const string SectionId = "{SEC00000-0000-4000-8000-000000000001}{1}{B0}";
    private const string NotebookId = "{CPY00000-0000-4000-8000-000000000001}{1}{B0}";

    private readonly TempDirectory temp = new();
    private readonly FakeGateway gateway = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string DefaultRoot => ExportRoots.Default().Paths[0];

    private static string CopyFolder => Path.Combine(DefaultRoot, "Throwaway a 20261118-134059");

    public void Dispose() => temp.Dispose();

    [Theory]
    [InlineData("absent")]
    [InlineData("default root named")]
    public async Task ASectionOfACopyUnderTheDefaultRoot_AtPagesScope_IsTargetScoped_NotListedWithoutAGrant(string settings)
    {
        var store = Settings(settings);
        Section(Path.Combine(CopyFolder, "New Section 1.one"));

        var result = await Hierarchy(store, SectionId, HierarchyScope.Pages);

        Assert.True(result.IsError, result.StructuredContent?.GetRawText());
        Assert.Equal("ReadNotGranted", result.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task TheCopyNotebook_AtPagesScope_WithholdsItsSections()
    {
        var store = Settings("absent");
        gateway.Hierarchies[(NotebookId, HierarchyScope.Pages)] =
            $"<?xml version=\"1.0\"?>\n<one:Notebook {Ns} name=\"Throwaway a 20261118-134059\" ID=\"{NotebookId}\" path=\"{CopyFolder}\">"
            + $"<one:Section name=\"New Section 1\" ID=\"{SectionId}\" path=\"{Path.Combine(CopyFolder, "New Section 1.one")}\"><one:Page ID=\"{{P}}\" name=\"Secret\" /></one:Section></one:Notebook>";

        var result = await Hierarchy(store, NotebookId, HierarchyScope.Pages);

        Assert.False(result.IsError, result.StructuredContent?.GetRawText());
        var root = result.StructuredContent!.Value;
        Assert.Equal([SectionId], root.GetProperty("meta").GetProperty("withheld").EnumerateArray().Select(e => e.GetString()));
        Assert.DoesNotContain("Secret", root.GetProperty("xml").GetString()!, StringComparison.Ordinal);
    }

    private SettingsStore Settings(string kind)
    {
        var path = temp.Combine("settings.json");
        if (kind == "default root named")
        {
            TestSettings.SetExportRoots(path, DefaultRoot);
        }

        return new SettingsStore(path, temp.Combine("journal"));
    }

    private void Section(string path)
    {
        gateway.Parents[SectionId] = NotebookId;
        gateway.Parents[NotebookId] = string.Empty;
        gateway.Hierarchies[(SectionId, HierarchyScope.Pages)] =
            $"<?xml version=\"1.0\"?>\n<one:Section {Ns} name=\"New Section 1\" ID=\"{SectionId}\" path=\"{path}\" lastModifiedTime=\"2026-11-18T00:00:00.000Z\"><one:Page ID=\"{{P}}\" name=\"Secret\" /></one:Section>";
        gateway.Hierarchies[(SectionId, HierarchyScope.Self)] =
            $"<?xml version=\"1.0\"?>\n<one:Section {Ns} name=\"New Section 1\" ID=\"{SectionId}\" path=\"{path}\" lastModifiedTime=\"2026-11-18T00:00:00.000Z\" />";
    }

    private async Task<ModelContextProtocol.Protocol.CallToolResult> Hierarchy(SettingsStore store, string start, HierarchyScope scope)
    {
        var access = new ReadAccess(gateway, store.Read, new SettingsWriter(store, new FakeClock()), new FakeClock());
        var useCase = new GetHierarchyUseCase(gateway, access);
        return await new ToolRunner(NullLogger<ToolRunner>.Instance).RunAsync("get_hierarchy", useCase, (c, ct) => useCase.ExecuteAsync(c, start, scope, ct), Ct);
    }
}
