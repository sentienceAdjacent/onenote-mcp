using System.Xml.Linq;
using OneNoteMcp.Com;
using OneNoteMcp.Core;
using OneNoteMcp.Xml;
using Xunit;

namespace OneNoteMcp.Integration;

/// <summary>
/// Opt-in: the real <see cref="ComGateway"/> against an already running OneNote. Read-only.
/// Covers the gateway's call path (a COMException raised by a COM call is wrapped as Internal with its HRESULT).
/// </summary>
public sealed class ComGatewayIntegrationTests
{
    private static readonly XNamespace One = OneNoteXml.Namespace2013;

    [Fact]
    public async Task GetHierarchy_Notebooks_ReturnsNotebooksXml()
    {
        Assert.SkipUnless(VtableSmokeTests.OneNoteFixture.Enabled, "Set ONENOTE_MCP_INTEGRATION=1 to run against a real OneNote.");
        Assert.True(new ProcessProbe().IsOneNoteRunning(), "OneNote desktop must already be running.");
        using var gateway = new ComGateway(new ProcessProbe());

        var xml = await gateway.GetHierarchyAsync(string.Empty, HierarchyScope.Notebooks, TestContext.Current.CancellationToken);

        var root = XDocument.Parse(xml).Root!;
        Assert.Equal(One + "Notebooks", root.Name);
        Assert.NotEmpty(root.Elements(One + "Notebook"));
    }

    [Fact]
    public async Task GetHierarchy_UnknownStartId_IsInternalWithHResult()
    {
        Assert.SkipUnless(VtableSmokeTests.OneNoteFixture.Enabled, "Set ONENOTE_MCP_INTEGRATION=1 to run against a real OneNote.");
        Assert.True(new ProcessProbe().IsOneNoteRunning(), "OneNote desktop must already be running.");
        using var gateway = new ComGateway(new ProcessProbe());

        var ex = await Assert.ThrowsAsync<OneNoteException>(() =>
            gateway.GetHierarchyAsync("{00000000-0000-0000-0000-000000000000}{1}{B0}", HierarchyScope.Notebooks, TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCode.Internal, ex.Code);
        Assert.NotNull(ex.ComHResult);
        Assert.Contains($"0x{ex.ComHResult:X8}", ex.Message, StringComparison.Ordinal);
    }
}
