using OneNoteMcp.Core;
using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Xml;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>
/// Backlog 12: a <c>.one</c> file opened by hand sits under <c>one:OpenSections</c>, outside any notebook (entry 15's
/// probe P3). <see cref="WriteAccess"/>, which every write's step 1 calls, refuses a write in or to such a loose section
/// with <see cref="ErrorCode.NotSupported"/> naming the section, never the caller's "isn't inside a notebook" fallback. The
/// fixture is P3's shape: the section's parent chain ends at once (an empty parent), and it reads at <c>self</c> as a
/// <c>one:Section</c> with a local path and no notebook.
/// </summary>
public sealed class LooseSectionWriteTests : IDisposable
{
    private const string Ns = "xmlns:one=\"" + OneNoteXml.Namespace2013 + "\"";
    private const string LooseId = "{LS000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string LoosePageId = "{LS000000-0000-4000-8000-000000000001}{1}{E1}";
    private const string LooseSubpageId = "{LS000000-0000-4000-8000-000000000001}{1}{E2}";
    private const string NotebookId = "{NB000000-0000-4000-8000-000000000001}{1}{B0}";

    private readonly TempDirectory temp = new();
    private readonly FakeGateway gateway = new();
    private readonly WriteAccess access;

    public LooseSectionWriteTests()
    {
        gateway.Parents[LooseId] = string.Empty;
        gateway.Parents[LoosePageId] = LooseId;
        gateway.Parents[LooseSubpageId] = LoosePageId;
        gateway.Parents[NotebookId] = string.Empty;
        gateway.Hierarchies[(LooseId, HierarchyScope.Self)] =
            $"<?xml version=\"1.0\"?>\n<one:Section {Ns} name=\"Loose\" ID=\"{LooseId}\" path=\"C:\\exports\\Loose.one\" />";
        gateway.Hierarchies[(LoosePageId, HierarchyScope.Self)] = $"<?xml version=\"1.0\"?>\n<one:Page {Ns} name=\"P\" ID=\"{LoosePageId}\" />";
        gateway.Hierarchies[(NotebookId, HierarchyScope.Self)] =
            $"<?xml version=\"1.0\"?>\n<one:Notebook {Ns} name=\"A\" nickname=\"A\" ID=\"{NotebookId}\" path=\"C:\\notebooks\\A\\\" />";
        var store = new SettingsStore(Path.Combine(temp.Path, "settings.json"), temp.Combine("journal"));
        access = new WriteAccess(gateway, store.Read);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => temp.Dispose();

    private static OneNoteException Fallback() => new(ErrorCode.ObjectNotFound, "the caller's fallback");

    [Theory]
    [InlineData(LoosePageId, "Page")]
    [InlineData(LooseSubpageId, "Page")]
    [InlineData(LooseId, "Section")]
    public async Task CheckAsync_InOrOfALooseSection_IsNotSupported_NamingTheSection(string objectId, string kind)
    {
        var ex = await Assert.ThrowsAsync<OneNoteException>(() => access.CheckAsync(objectId, Fallback, Ct));

        AssertLoose(ex, objectId, kind);
    }

    [Fact]
    public async Task CheckContainerAsync_OfALooseSection_IsNotSupported_NamingIt()
    {
        var ex = await Assert.ThrowsAsync<OneNoteException>(() => access.CheckContainerAsync(LooseId, Fallback, Ct));

        AssertLoose(ex, LooseId, "Section");
    }

    [Fact]
    public async Task CheckPermanentDeleteAsync_InALooseSection_IsNotSupported()
    {
        var ex = await Assert.ThrowsAsync<OneNoteException>(() => access.CheckPermanentDeleteAsync(LoosePageId, Fallback, Ct));

        AssertLoose(ex, LoosePageId, "Page");
    }

    [Fact]
    public async Task ANotebookItself_KeepsTheCallersFallback()
    {
        var ex = await Assert.ThrowsAsync<OneNoteException>(() => access.CheckAsync(NotebookId, Fallback, Ct));

        Assert.Equal("the caller's fallback", ex.Message);
    }

    [Fact]
    public async Task CheckAsync_OfALooseSection_ForAPage_NamesThePage()
    {
        // set_page_level and a page move check the page's section (review finding): the page is named all the same.
        var ex = await Assert.ThrowsAsync<OneNoteException>(() => access.CheckAsync(LooseId, Fallback, Ct, subjectId: LoosePageId));

        AssertLoose(ex, LoosePageId, "Page");
    }

    [Fact]
    public async Task AnObjectWhoseSelfReadFails_KeepsTheCallersFallback()
    {
        // Review finding: the extra self read never replaces the caller's fallback with a gateway error.
        const string orphan = "{OR000000-0000-4000-8000-000000000001}{1}{B0}";
        gateway.Parents[orphan] = string.Empty;

        var ex = await Assert.ThrowsAsync<OneNoteException>(() => access.CheckAsync(orphan, Fallback, Ct));

        Assert.Equal("the caller's fallback", ex.Message);
    }

    private static void AssertLoose(OneNoteException ex, string objectId, string kind)
    {
        Assert.Equal(ErrorCode.NotSupported, ex.Code);
        Assert.False(ex.Retryable);
        Assert.Contains(LooseId, ex.Message, StringComparison.Ordinal);
        Assert.Contains(objectId, ex.Message, StringComparison.Ordinal);
        Assert.StartsWith(kind, ex.Message, StringComparison.Ordinal);
        Assert.Contains("outside any notebook", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("isn't a page", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Loose", ex.Message.Replace(LooseId, string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Equal(WriteAccess.LooseSectionUserAction, ex.UserAction);
    }
}
