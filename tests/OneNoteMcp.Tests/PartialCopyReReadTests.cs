using OneNoteMcp.Core;
using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.Writes;
using OneNoteMcp.Xml;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>
/// The one re-read after <c>0x80042004</c> that the page move, the section move and the restore share (backlog 17): the
/// message for each, counts only in a protected target, names compared by kind (Q1), and a re-read that throws.
/// </summary>
public sealed class PartialCopyReReadTests
{
    private const string Ns = "xmlns:one=\"http://schemas.microsoft.com/office/onenote/2013/onenote\"";
    private const string SourceId = "{50000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string TargetId = "{7A000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string NotebookId = "{4B000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string GroupId = "{6A000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string ObjectId = "{0B000000-0000-4000-8000-000000000001}{1}{E1}";
    private const string CopyId = "{C0000000-0000-4000-8000-000000000001}{1}{E1}";
    private const string OtherId = "{0D000000-0000-4000-8000-000000000001}{1}{E1}";
    private const string ObjectName = "Plans";

    private static readonly Protection NoRoots = new([]);
    private readonly FakeGateway gateway = new();
    private readonly Dictionary<HierarchyTarget, string> xmlOf = new(ReferenceEqualityComparer.Instance);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task APageMove_SaysWhereThePageIs_AndNamesTheCopy()
    {
        var source = Section(SourceId, Page(ObjectId, ObjectName));
        var target = Section(TargetId, Page(OtherId, "Other"));
        Serve(SourceId, Section(SourceId, Page(ObjectId, ObjectName)));
        Serve(TargetId, Section(TargetId, Page(OtherId, "Other"), Page(CopyId, ObjectName)));

        var found = await Find("move", source, target, HierarchyKind.Page);

        Assert.Equal(
            $"The move may have left a partial copy. Re-read straight after the failure: page {ObjectId} is still in section {SourceId}, and page {ObjectId} isn't in section {TargetId}, which has 1 new page(s) with its name ({CopyId}); a copy in a cloud notebook can land later.",
            found);
    }

    [Fact]
    public async Task APageMove_IntoAProtectedTarget_GivesTheCountOnly()
    {
        var target = Section(TargetId, encrypted: true);
        Serve(SourceId, Section(SourceId));
        Serve(TargetId, Section(TargetId, encrypted: true, Page(CopyId, ObjectName)));

        var found = await Find("move", Section(SourceId, Page(ObjectId, ObjectName)), target, HierarchyKind.Page);

        Assert.Contains($"page {ObjectId} is no longer in section {SourceId}", found, StringComparison.Ordinal);
        Assert.Contains($"section {TargetId}, which has 1 new page(s) with its name;", found, StringComparison.Ordinal);
        Assert.DoesNotContain(CopyId, found, StringComparison.Ordinal);
        Assert.DoesNotContain(ObjectName, found, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASectionMove_SaysWhereTheSectionIs_AndNamesTheCopy()
    {
        var source = Notebook(NotebookId, Child("Section", ObjectId, ObjectName));
        var target = Group(GroupId);
        Serve(NotebookId, Notebook(NotebookId, Child("Section", ObjectId, ObjectName)));
        Serve(GroupId, Group(GroupId, Child("Section", CopyId, ObjectName)));

        var found = await Find("move", source, target, HierarchyKind.Section);

        Assert.Equal(
            $"The move may have left a partial copy. Re-read straight after the failure: section {ObjectId} is still in notebook {NotebookId}, and section {ObjectId} isn't in section group {GroupId}, which has 1 new section(s) with its name ({CopyId}); a copy in a cloud notebook can land later.",
            found);
    }

    [Fact]
    public async Task ASectionMove_ThatArrived_SaysItIsInTheTarget()
    {
        Serve(NotebookId, Notebook(NotebookId));
        Serve(GroupId, Group(GroupId, Child("Section", ObjectId, ObjectName)));

        var found = await Find("move", Notebook(NotebookId, Child("Section", ObjectId, ObjectName)), Group(GroupId), HierarchyKind.Section);

        Assert.Contains($"section {ObjectId} is no longer in notebook {NotebookId}", found, StringComparison.Ordinal);
        Assert.Contains($"section {ObjectId} is in section group {GroupId}, which has no new section with its name", found, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APageRestore_ReadsTheTargetOnly_AndNamesTheCopy()
    {
        Serve(TargetId, Section(TargetId, Page(CopyId, ObjectName)));

        var found = await Find("restore", null, Section(TargetId), HierarchyKind.Page);

        Assert.Equal(
            $"The restore may have left a partial copy. Re-read straight after the failure: page {ObjectId} isn't in section {TargetId}, which has 1 new page(s) with its name ({CopyId}); a copy in a cloud notebook can land later.",
            found);
        Assert.Equal([(TargetId, HierarchyScope.Children)], gateway.Calls);
    }

    [Fact]
    public async Task APageRestore_IntoAProtectedTarget_GivesTheCountOnly()
    {
        Serve(TargetId, Section(TargetId, encrypted: true, Page(CopyId, ObjectName), Page(OtherId, ObjectName)));

        var found = await Find("restore", null, Section(TargetId, encrypted: true), HierarchyKind.Page);

        Assert.Contains("which has 2 new page(s) with its name;", found, StringComparison.Ordinal);
        Assert.DoesNotContain(CopyId, found, StringComparison.Ordinal);
        Assert.DoesNotContain(OtherId, found, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ASectionRestore_SaysWhetherTheSectionIsInTheTarget(bool arrived)
    {
        Serve(NotebookId, arrived ? Notebook(NotebookId, Child("Section", ObjectId, ObjectName)) : Notebook(NotebookId));

        var found = await Find("restore", null, Notebook(NotebookId), HierarchyKind.Section);

        Assert.Equal(
            $"The restore may have left a partial copy. Re-read straight after the failure: section {ObjectId} {(arrived ? "is in" : "isn't in")} notebook {NotebookId}, which has no new section with its name; a copy in a cloud notebook can land later.",
            found);
    }

    [Fact]
    public async Task ASectionCopy_InAGroupTarget_IsNamed_AsOnlyASectionTargetIsProtected()
    {
        // Protection.IsProtected is true only for a section target, so a notebook or group target always names its copies.
        Serve(GroupId, Group(GroupId, Child("Section", CopyId, ObjectName)));

        var found = await PartialCopyReRead.FindingsAsync(
            gateway, new Protection([@"C:\"]), "move", null, Group(GroupId), ObjectId, HierarchyKind.Section, ObjectName, Ct);

        Assert.Contains($"1 new section(s) with its name ({CopyId})", found, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APageNameDifferingOnlyInCase_IsNotCounted()
    {
        Serve(TargetId, Section(TargetId, Page(CopyId, ObjectName.ToUpperInvariant())));

        var found = await Find("move", null, Section(TargetId), HierarchyKind.Page);

        Assert.Contains("which has no new page with its name", found, StringComparison.Ordinal);
        Assert.DoesNotContain(CopyId, found, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Section", HierarchyKind.Section)]
    [InlineData("SectionGroup", HierarchyKind.SectionGroup)]
    public async Task ASectionOrGroupNameDifferingOnlyInCase_IsCounted(string element, HierarchyKind kind)
    {
        Serve(GroupId, Group(GroupId, Child(element, CopyId, ObjectName.ToUpperInvariant())));

        var found = await Find("move", null, Group(GroupId), kind);

        Assert.Contains($"1 new {(kind == HierarchyKind.Section ? "section" : "section group")}(s) with its name ({CopyId})", found, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANewChildOfAnotherKind_IsNotCounted()
    {
        Serve(GroupId, Group(GroupId, Child("SectionGroup", CopyId, ObjectName)));

        var found = await Find("move", null, Group(GroupId), HierarchyKind.Section);

        Assert.Contains("which has no new section with its name", found, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReReadThatThrows_OnlyAddsToTheMessage()
    {
        // No answer for either ID: the fake throws ObjectNotFound, as OneNote does for an unknown ID.
        var found = await Find("move", Section(SourceId), Section(TargetId), HierarchyKind.Page);

        Assert.Equal(
            $"The move may have left a partial copy. Re-read straight after the failure: section {SourceId} couldn't be re-read (ObjectNotFound), and section {TargetId} couldn't be re-read (ObjectNotFound); a copy in a cloud notebook can land later.",
            found);
    }

    [Fact]
    public async Task ACancelledReRead_SaysSo()
    {
        Serve(TargetId, Section(TargetId));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var found = await PartialCopyReRead.FindingsAsync(gateway, NoRoots, "restore", null, Section(TargetId), ObjectId, HierarchyKind.Page, ObjectName, cancelled.Token);

        Assert.Contains($"section {TargetId} couldn't be re-read (the write's deadline or a cancel)", found, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSettledWarning_IsOneTextForEveryWrite()
    {
        var warning = CloudSettle.SettledWarning("The move of X");

        Assert.Equal(Core.Results.ToolWarnings.Settled, warning.Code);
        Assert.Equal("The move of X touched a cloud notebook, so the server waited 10 s before reading back what it did, as OneNote can fail or block on a write straight after one.", warning.Message);
        Assert.Equal(CloudSettle.SettledUserAction, warning.UserAction);
    }

    private Task<string> Find(string verb, HierarchyTarget? source, HierarchyTarget target, HierarchyKind kind) =>
        PartialCopyReRead.FindingsAsync(gateway, NoRoots, verb, source, target, ObjectId, kind, ObjectName, Ct);

    private void Serve(string id, HierarchyTarget node) => gateway.Hierarchies[(id, HierarchyScope.Children)] = xmlOf[node];

    private HierarchyTarget Read(string xml)
    {
        var node = HierarchyTarget.Read(xml);
        xmlOf[node] = xml;
        return node;
    }

    private HierarchyTarget Section(string id, params string[] pages) => Section(id, false, pages);

    private HierarchyTarget Section(string id, bool encrypted, params string[] pages) =>
        Read($"<one:Section {Ns} ID=\"{id}\" name=\"S\" path=\"C:\\nb\\S.one\"{(encrypted ? " encrypted=\"true\"" : string.Empty)}>{string.Concat(pages)}</one:Section>");

    private HierarchyTarget Notebook(string id, params string[] children) =>
        Read($"<one:Notebook {Ns} ID=\"{id}\" name=\"N\" path=\"C:\\nb\">{string.Concat(children)}</one:Notebook>");

    private HierarchyTarget Group(string id, params string[] children) =>
        Read($"<one:SectionGroup {Ns} ID=\"{id}\" name=\"G\" path=\"C:\\nb\\G\">{string.Concat(children)}</one:SectionGroup>");

    private static string Page(string id, string name) => $"<one:Page ID=\"{id}\" name=\"{name}\" pageLevel=\"1\" />";

    private static string Child(string element, string id, string name) => $"<one:{element} ID=\"{id}\" name=\"{name}\" path=\"C:\\nb\\{name}\" />";
}
