using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using OneNoteMcp.Core;
using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Core.Writes;
using OneNoteMcp.Server;
using OneNoteMcp.Server.Grants;
using OneNoteMcp.Server.Tools;
using OneNoteMcp.Xml;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>
/// Story 5.3: <c>create_section</c>, <c>create_section_group</c> and <c>create_notebook</c> through the AD-7 write
/// pipeline, one test or theory per row of the plan's I/O matrix, against <see cref="FakeGateway"/> scripted with a
/// synthetic notebook <c>W</c>, a temporary <c>settings.json</c> and a temporary folder for new notebooks. Each test has a
/// write lock of its own (a unique mutex name), and checks it is free again after every outcome.
/// </summary>
public sealed class CreateHierarchyToolTests : IDisposable
{
    private const string NotebookId = "{NBW00000-0000-4000-8000-000000000001}{1}{B0}";
    private const string NotebookPath = @"C:\notebooks\W\";
    private const string SectionId = "{SW000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string GroupId = "{GW000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string RecycleBinId = "{RBW00000-0000-4000-8000-000000000001}{1}{B0}";
    private const string DeletedPagesId = "{SDW00000-0000-4000-8000-000000000001}{1}{B0}";
    private const string BinnedGroupId = "{GBW00000-0000-4000-8000-000000000001}{1}{B0}";
    private const string PageId = "{SW000000-0000-4000-8000-000000000001}{1}{E1}";
    private const string PlansId = "{S2000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string OldId = "{S3000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string UnknownId = "{00000000-0000-4000-8000-00000000DEAD}{1}{B0}";

    /// <summary>A notebook name that appears nowhere else, so a test can show it never reaches a message.</summary>
    private const string NewNotebook = "Trips Zq7";

    private const string Ns = "xmlns:one=\"" + OneNoteXml.Namespace2013 + "\"";

    private readonly TempDirectory temp = new();
    private readonly FakeClock clock = new();
    private readonly FakeGateway gateway = new();
    private readonly ConcurrentQueue<WriteLockHeld> holds = new();
    private readonly WriteLock writeLock;
    private readonly PageTextCache cache = PageTextCache.WithDefaults();

    public CreateHierarchyToolTests()
    {
        writeLock = new WriteLock($@"Local\OneNoteMcp.Test.{Guid.NewGuid():N}", holds.Enqueue);
        Directory.CreateDirectory(SettingsFolder);
        Directory.CreateDirectory(NotebooksFolder);

        // Notebook W: section Ideas and group Archive at the top, section Plans in the group, the recycle bin with a
        // recycled group and Deleted Pages, and a page.
        Parents[NotebookId] = string.Empty;
        Parents[SectionId] = NotebookId;
        Parents[GroupId] = NotebookId;
        Parents[RecycleBinId] = NotebookId;
        Parents[DeletedPagesId] = RecycleBinId;
        Parents[BinnedGroupId] = RecycleBinId;
        Parents[PageId] = SectionId;
        Self(NotebookId, $"<one:Notebook {Ns} name=\"W\" nickname=\"W\" ID=\"{NotebookId}\" path=\"{NotebookPath}\" />");
        Self(SectionId, $"<one:Section {Ns} name=\"Ideas\" ID=\"{SectionId}\" path=\"C:\\notebooks\\W\\Ideas.one\" />");
        Self(GroupId, $"<one:SectionGroup {Ns} name=\"Archive\" ID=\"{GroupId}\" path=\"C:\\notebooks\\W\\Archive\" />");
        Self(RecycleBinId, $"<one:SectionGroup {Ns} name=\"OneNote_RecycleBin\" ID=\"{RecycleBinId}\" path=\"C:\\notebooks\\W\\OneNote_RecycleBin\" isRecycleBin=\"true\" />");
        Self(PageId, $"<one:Page {Ns} ID=\"{PageId}\" name=\"P\" />");

        Children(NotebookId, $"""
            <one:Notebook {Ns} name="W" nickname="W" ID="{NotebookId}" path="{NotebookPath}">
              <one:Section name="Ideas" ID="{SectionId}" path="C:\notebooks\W\Ideas.one" />
              <one:SectionGroup name="Archive" ID="{GroupId}" path="C:\notebooks\W\Archive" />
              <one:SectionGroup name="OneNote_RecycleBin" ID="{RecycleBinId}" path="C:\notebooks\W\OneNote_RecycleBin" isRecycleBin="true" />
            </one:Notebook>
            """);
        Children(GroupId, $"""
            <one:SectionGroup {Ns} name="Archive" ID="{GroupId}" path="C:\notebooks\W\Archive">
              <one:Section name="Plans" ID="{PlansId}" path="C:\notebooks\W\Archive\Plans.one" />
              <one:Section name="Old" ID="{OldId}" path="C:\notebooks\W\Archive\Old.one" isInRecycleBin="true" />
            </one:SectionGroup>
            """);
        Children(SectionId, $"<one:Section {Ns} name=\"Ideas\" ID=\"{SectionId}\" path=\"C:\\notebooks\\W\\Ideas.one\"><one:Page ID=\"{PageId}\" name=\"P\" /></one:Section>");
        Children(PageId, $"<one:Page {Ns} ID=\"{PageId}\" name=\"P\" />");
        Children(RecycleBinId, $"<one:SectionGroup {Ns} name=\"OneNote_RecycleBin\" ID=\"{RecycleBinId}\" path=\"C:\\notebooks\\W\\OneNote_RecycleBin\" isRecycleBin=\"true\" />");
        Children(DeletedPagesId, $"<one:Section {Ns} name=\"Deleted Pages\" ID=\"{DeletedPagesId}\" path=\"C:\\notebooks\\W\\OneNote_RecycleBin\\OneNote_DeletedPages.one\" isInRecycleBin=\"true\" isDeletedPages=\"true\" />");
        Children(BinnedGroupId, $"<one:SectionGroup {Ns} name=\"Gone\" ID=\"{BinnedGroupId}\" path=\"C:\\notebooks\\W\\OneNote_RecycleBin\\Gone\" isInRecycleBin=\"true\" />");

        OpenNotebooksAre();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Dictionary<string, string> Parents => gateway.Parents;

    /// <summary>The settings file's own folder, apart from <see cref="NotebooksFolder"/> (a notebook can't go in OneNoteMcp's folders).</summary>
    private string SettingsFolder => temp.Combine("cfg");

    /// <summary>The folder new notebooks go in.</summary>
    private string NotebooksFolder => temp.Combine("notebooks");

    private SettingsStore Store => new(Path.Combine(SettingsFolder, "settings.json"));

    /// <summary>The new notebook's folder, as the server makes it canonical.</summary>
    private string Target => FilePathGate.Canonical(Path.Combine(NotebooksFolder, NewNotebook), out _)!;

    public void Dispose() => temp.Dispose();

    // --- Sections and section groups: granted -------------------------------------------------------------------------

    [Fact]
    public async Task Section_Granted_UnderTheNotebook_IsOk_WithObjectAndParentIds_AndOneOpenHierarchy()
    {
        Grant();

        var root = Ok(await SectionTool().CreateSection(NotebookId, "Plans", Ct));

        var meta = root.GetProperty("meta");
        Assert.Equal(["correlationId", "parentId", "objectId"], meta.EnumerateObject().Select(p => p.Name));
        Assert.Equal(FakeGateway.CreatedSectionId(1), meta.GetProperty("objectId").GetString());
        Assert.Equal(NotebookId, meta.GetProperty("parentId").GetString());
        Assert.Equal(0, root.GetProperty("warnings").GetArrayLength());
        Assert.Equal([(NotebookId, "Plans.one")], gateway.CreateSectionCalls);
        Assert.Contains((NotebookId, HierarchyScope.Children), gateway.Calls);
        Assert.Equal(meta.GetProperty("correlationId").GetString(), Assert.Single(holds).CorrelationId);
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task Section_Granted_UnderAGroup_IsOk()
    {
        Grant();

        var root = Ok(await SectionTool().CreateSection(GroupId, "Ideas", Ct));

        Assert.Equal(GroupId, root.GetProperty("meta").GetProperty("parentId").GetString());
        Assert.Equal([(GroupId, "Ideas.one")], gateway.CreateSectionCalls);
    }

    [Theory]
    [InlineData("Notes.one", "Notes.one")]
    [InlineData("Notes.ONE", "Notes.ONE")]
    [InlineData("Notes.txt", "Notes.txt.one")]
    public async Task Section_OneIsAddedUnlessTheNameEndsWithIt(string name, string sent)
    {
        Grant();

        Ok(await SectionTool().CreateSection(NotebookId, name, Ct));

        Assert.Equal([(NotebookId, sent)], gateway.CreateSectionCalls);
    }

    [Fact]
    public async Task Section_ARecycledSectionOfTheSameName_DoesNotCount()
    {
        Grant();

        Ok(await SectionTool().CreateSection(GroupId, "Old", Ct));

        Assert.Equal([(GroupId, "Old.one")], gateway.CreateSectionCalls);
    }

    [Fact]
    public async Task Group_Granted_IsOk_WithCftFolderNamesOnly()
    {
        Grant();

        var root = Ok(await GroupTool().CreateSectionGroup(NotebookId, "Trips", Ct));

        Assert.Equal(FakeGateway.CreatedGroupId(1), root.GetProperty("meta").GetProperty("objectId").GetString());
        Assert.Equal(NotebookId, root.GetProperty("meta").GetProperty("parentId").GetString());
        Assert.Equal([(NotebookId, "Trips")], gateway.CreateGroupCalls);
        Assert.Empty(gateway.CreateSectionCalls);
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task Group_Granted_UnderAGroup_IsOk()
    {
        Grant();

        Ok(await GroupTool().CreateSectionGroup(GroupId, "2025", Ct));

        Assert.Equal([(GroupId, "2025")], gateway.CreateGroupCalls);
    }

    // --- Existing names -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("ideas")]
    [InlineData("IDEAS.one")]
    public async Task Section_ExistingSectionName_IgnoringCase_IsOk_WithItsId_AlreadyExisted_AndNoCom(string name)
    {
        Grant();

        var root = Ok(await SectionTool().CreateSection(NotebookId, name, Ct));

        Assert.Equal(SectionId, root.GetProperty("meta").GetProperty("objectId").GetString());
        var warning = Assert.Single(root.GetProperty("warnings").EnumerateArray());
        Assert.Equal(ToolWarnings.AlreadyExisted, warning.GetProperty("code").GetString());
        Assert.Contains(SectionId, warning.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("deas", warning.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(gateway.CreateSectionCalls);
        Assert.Single(holds); // decided under the lock, at step 4
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task Group_ExistingGroupName_IgnoringCase_IsOk_WithItsId_AlreadyExisted_AndNoCom()
    {
        Grant();

        var root = Ok(await GroupTool().CreateSectionGroup(NotebookId, "ARCHIVE", Ct));

        Assert.Equal(GroupId, root.GetProperty("meta").GetProperty("objectId").GetString());
        Assert.Equal(ToolWarnings.AlreadyExisted, Assert.Single(root.GetProperty("warnings").EnumerateArray()).GetProperty("code").GetString());
        Assert.Empty(gateway.CreateGroupCalls);
    }

    [Fact]
    public async Task Section_NamedLikeAGroupThere_IsInvalidArgument_AndNoCom()
    {
        // Phill, 2026-11-13, 5.3 Q4: a section and a section group never share a name under one parent.
        Grant();

        var error = Error(await SectionTool().CreateSection(NotebookId, "archive", Ct), "InvalidArgument");

        Assert.Contains(GroupId, error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("rchive", error.GetRawText(), StringComparison.OrdinalIgnoreCase);
        AssertNoCreate();
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task Group_NamedLikeASectionThere_IsInvalidArgument_AndNoCom()
    {
        Grant();

        Error(await GroupTool().CreateSectionGroup(NotebookId, "Ideas", Ct), "InvalidArgument");

        AssertNoCreate();
    }

    // --- Refused before COM ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("..")]
    [InlineData("x..y")]
    [InlineData("x?")]
    [InlineData("x*")]
    [InlineData("a:b")]
    [InlineData("a\tb")]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData(" x")]
    [InlineData("x ")]
    [InlineData("x.")]
    [InlineData(".x")]
    [InlineData("   ")]
    [InlineData("")]
    [InlineData("OneNote_RecycleBin")]
    [InlineData("onenote_deletedpages")]
    [InlineData("OneNote_RecycleBin.one")]
    [InlineData("ab\u0085cd")]
    [InlineData("ab\u2028cd")]
    [InlineData("ab\u202Ecd")]
    [InlineData("ab\u2066cd")]
    [InlineData("ab\u200Bcd")]
    [InlineData("\uFEFFabcd")]
    public async Task BadName_IsInvalidArgument_BeforeTheLock_NeverQuotingIt(string name)
    {
        Grant();

        foreach (var result in new[] { await SectionTool().CreateSection(NotebookId, name, Ct), await GroupTool().CreateSectionGroup(NotebookId, name, Ct) })
        {
            var error = Error(result, "InvalidArgument");
            Assert.Equal(CreateHierarchyUseCase.NameUserAction, error.GetProperty("userAction").GetString());
            if (name.Trim().Length > 2)
            {
                Assert.DoesNotContain(name, error.GetProperty("message").GetString(), StringComparison.Ordinal);
            }
        }

        AssertNoCreate();
        Assert.Empty(holds);
        Assert.Empty(gateway.Calls); // before settings or the hierarchy are read
    }

    [Theory]
    [InlineData("a .one")]
    [InlineData(".one")]
    public async Task Section_BadFileName_IsInvalidArgument_BeforeTheLock(string name)
    {
        Grant();

        Error(await SectionTool().CreateSection(NotebookId, name, Ct), "InvalidArgument");

        AssertNoCreate();
        Assert.Empty(holds);
    }

    [Theory]
    [InlineData(SectionId)]
    [InlineData(PageId)]
    [InlineData(RecycleBinId)]
    [InlineData(DeletedPagesId)]
    [InlineData(BinnedGroupId)]
    public async Task ParentNotALiveContainer_IsObjectNotFound_AskingForANotebookOrGroup_AndNoCom(string parentId)
    {
        Grant();

        foreach (var result in new[] { await SectionTool().CreateSection(parentId, "New", Ct), await GroupTool().CreateSectionGroup(parentId, "New", Ct) })
        {
            var error = Error(result, "ObjectNotFound");
            Assert.Equal(CreateHierarchyUseCase.NotAContainerUserAction, error.GetProperty("userAction").GetString());
            Assert.False(error.GetProperty("retryable").GetBoolean());
            Assert.Contains(parentId, error.GetProperty("message").GetString(), StringComparison.Ordinal);
        }

        AssertNoCreate();
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task UnknownParent_IsObjectNotFound_AndNoCom()
    {
        Grant();

        Error(await SectionTool().CreateSection(UnknownId, "New", Ct), "ObjectNotFound");

        AssertNoCreate();
    }

    [Fact]
    public async Task Ungranted_ClientWithoutElicitation_IsWriteNotGranted_NoLock_NoCom()
    {
        File.WriteAllText(Store.Path, """{"schemaVersion": 1}""");

        foreach (var result in new[] { await SectionTool().CreateSection(NotebookId, "New", Ct), await GroupTool().CreateSectionGroup(GroupId, "New", Ct) })
        {
            var error = Error(result, "WriteNotGranted");
            Assert.Contains("Never run the grant command yourself", error.GetProperty("userAction").GetString(), StringComparison.Ordinal);
            Assert.Contains(NotebookId, error.GetProperty("message").GetString(), StringComparison.Ordinal);
        }

        AssertNoCreate();
        Assert.Empty(holds);
    }

    [Fact]
    public async Task Ungranted_TheGrantDialogTypedRight_GrantsTheParentNotebook_ThenCreates()
    {
        File.WriteAllText(Store.Path, """{"schemaVersion": 1}""");
        var prompt = new ScriptedGrantPrompt(GrantPromptStatus.Accepted, "W");

        var result = await RunWithGrantPrompt(prompt, (useCase, context, ct) => useCase.CreateSectionAsync(context, NotebookId, "New", ct));

        Ok(result);
        Assert.Equal(1, prompt.Asked);
        Assert.Equal(GrantedVia.Elicitation, Store.Read().Notebooks[GrantKeys.NotebookKey(NotebookPath)].GrantedVia);
        Assert.Equal([(NotebookId, "New.one")], gateway.CreateSectionCalls);
    }

    [Fact]
    public async Task Ungranted_TheGrantDialogDeclined_IsWriteNotGranted_NoLock_NoCom()
    {
        File.WriteAllText(Store.Path, """{"schemaVersion": 1}""");
        var prompt = new ScriptedGrantPrompt(GrantPromptStatus.Declined, null);

        Error(await RunWithGrantPrompt(prompt, (useCase, context, ct) => useCase.CreateSectionGroupAsync(context, GroupId, "New", ct)), "WriteNotGranted");

        AssertNoCreate();
        Assert.Empty(holds);
    }

    [Fact]
    public async Task SettingsInvalid_IsSettingsInvalid_AtStep1_AndNoCom()
    {
        File.WriteAllText(Store.Path, """{"schemaVersion": 1, "journal": {"retentionDays": 0}}""");

        Error(await SectionTool().CreateSection(NotebookId, "New", Ct), "SettingsInvalid");
        Error(await NotebookTool(Location(NotebooksFolder)).CreateNotebook(NewNotebook, NotebooksFolder, Ct), "SettingsInvalid");

        AssertNoCreate();
        Assert.Empty(holds);
        Assert.Empty(gateway.Calls);
    }

    // --- Step 7 ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(ErrorCode.Timeout)]
    [InlineData(ErrorCode.ComUnavailable)]
    public async Task ComTimeoutOrDisconnect_IsOutcomeUnknown_AndNotRetried(ErrorCode code)
    {
        Grant();
        gateway.CreateSectionFailure = new OneNoteException(code, "m");
        gateway.CreateGroupFailure = new OneNoteException(code, "m");

        foreach (var result in new[] { await SectionTool().CreateSection(NotebookId, "New", Ct), await GroupTool().CreateSectionGroup(NotebookId, "New", Ct) })
        {
            var error = Error(result, code.ToString());
            Assert.True(error.GetProperty("outcomeUnknown").GetBoolean());
            Assert.False(error.GetProperty("retryable").GetBoolean());
        }

        Assert.Single(gateway.CreateSectionCalls);
        Assert.Single(gateway.CreateGroupCalls);
        await AssertLockFreeAsync();
    }

    // --- Notebooks ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Notebook_InAnAbsentFolder_IsCreated_Granted_AndListGrantsShowsIt()
    {
        ReadBackAt(Target);
        var prompt = Location(NotebooksFolder);

        var root = Ok(await NotebookTool(prompt).CreateNotebook(NewNotebook, NotebooksFolder, Ct));

        var meta = root.GetProperty("meta");
        Assert.Equal(["correlationId", "path", "objectId"], meta.EnumerateObject().Select(p => p.Name));
        Assert.Equal(FakeGateway.CreatedNotebookId(1), meta.GetProperty("objectId").GetString());
        Assert.Equal(Target, meta.GetProperty("path").GetString());
        Assert.Equal(0, root.GetProperty("warnings").GetArrayLength());
        Assert.Equal([Target], gateway.CreateNotebookCalls);
        Assert.Single(holds);
        await AssertLockFreeAsync();

        var grant = Store.Read().Notebooks[GrantKeys.NotebookKey(Target)];
        Assert.True(grant.Write);
        Assert.False(grant.PermanentDelete);
        Assert.Equal(GrantedVia.Created, grant.GrantedVia);
        Assert.Equal(FakeGateway.CreatedNotebookId(1), grant.IdAtGrant);

        // list_grants: a writeNotebook grant, grantedVia created, active while the notebook is open.
        OpenNotebooksAre((NewNotebook, Target, FakeGateway.CreatedNotebookId(1)));
        var listed = await new ListGrantsUseCase(gateway, Store.Read, clock).ExecuteAsync(new ToolCallContext("c"), Ct);
        var row = Assert.Single(listed.Meta.Grants!, g => g.Key == GrantKeys.NotebookKey(Target));
        Assert.Equal(ListGrantsUseCase.WriteNotebookKind, row.Kind);
        Assert.Equal(GrantedVia.Created, row.GrantedVia);
        Assert.Equal("active", row.Status);
    }

    [Fact]
    public async Task Notebook_InAnEmptyFolder_IsCreated()
    {
        Directory.CreateDirectory(Path.Combine(NotebooksFolder, NewNotebook));
        ReadBackAt(Target);

        Ok(await NotebookTool(Location(NotebooksFolder)).CreateNotebook(NewNotebook, NotebooksFolder, Ct));

        Assert.Equal([Target], gateway.CreateNotebookCalls);
    }

    [Fact]
    public async Task Notebook_TheDialog_NamesTheNotebook_PrefillsTheProposedFolder_AndOffersNoOneDrive()
    {
        ReadBackAt(Target);
        var prompt = Location(NotebooksFolder);

        Ok(await NotebookTool(prompt).CreateNotebook(NewNotebook, NotebooksFolder, Ct));

        var question = Assert.Single(prompt.Questions);
        Assert.Equal(new NotebookLocationQuestion(NewNotebook, NotebooksFolder, OfferOneDrive: false), question);
    }

    [Fact]
    public async Task Notebook_NoProposedFolder_PrefillsOneNotesDefaultNotebookFolder()
    {
        ReadBackAt(Target);
        var prompt = Location(NotebooksFolder);

        Ok(await NotebookTool(prompt).CreateNotebook(NewNotebook, cancellationToken: Ct));

        Assert.Equal(
            FakeGateway.ReadFixture("mcp-test/special/DefaultNotebookFolder.txt").Trim(),
            Assert.Single(prompt.Questions).Folder);
    }

    [Fact]
    public async Task Notebook_TheUsersFolder_Wins_OverTheProposedOne()
    {
        var chosen = temp.Combine("chosen");
        Directory.CreateDirectory(chosen);
        var target = FilePathGate.Canonical(Path.Combine(chosen, NewNotebook), out _)!;
        ReadBackAt(target);

        Ok(await NotebookTool(Location(chosen)).CreateNotebook(NewNotebook, NotebooksFolder, Ct));

        Assert.Equal([target], gateway.CreateNotebookCalls);
    }

    [Fact]
    public async Task Notebook_FolderNotEmpty_EvenWithOnlyAHiddenFile_IsFileExists_AndNoCom()
    {
        var folder = Directory.CreateDirectory(Path.Combine(NotebooksFolder, NewNotebook)).FullName;
        var hidden = Path.Combine(folder, "desktop.ini");
        File.WriteAllText(hidden, "x");
        File.SetAttributes(hidden, FileAttributes.Hidden | FileAttributes.System);

        var error = Error(await NotebookTool(Location(NotebooksFolder)).CreateNotebook(NewNotebook, NotebooksFolder, Ct), "FileExists");

        AssertNoPathOrName(error);
        Assert.Empty(gateway.CreateNotebookCalls);
        Assert.Single(holds); // checked under the lock
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task Notebook_AFileWhereTheFolderGoes_IsFileExists()
    {
        File.WriteAllText(Path.Combine(NotebooksFolder, NewNotebook), "x");

        Error(await NotebookTool(Location(NotebooksFolder)).CreateNotebook(NewNotebook, NotebooksFolder, Ct), "FileExists");

        Assert.Empty(gateway.CreateNotebookCalls);
    }

    [Fact]
    public async Task Notebook_AnOpenNotebooksFolder_IsFileExists()
    {
        OpenNotebooksAre(("Other", Target.ToUpperInvariant() + "\\", "{NBO00000-0000-4000-8000-000000000001}{1}{B0}"));

        Error(await NotebookTool(Location(NotebooksFolder)).CreateNotebook(NewNotebook, NotebooksFolder, Ct), "FileExists");

        Assert.Empty(gateway.CreateNotebookCalls);
    }

    [Fact]
    public async Task Notebook_InsideAnOpenNotebook_IsInvalidArgument()
    {
        OpenNotebooksAre(("Other", NotebooksFolder, "{NBO00000-0000-4000-8000-000000000001}{1}{B0}"));

        var error = Error(await NotebookTool(Location(NotebooksFolder)).CreateNotebook(NewNotebook, NotebooksFolder, Ct), "InvalidArgument");

        AssertNoPathOrName(error);
        Assert.Empty(gateway.CreateNotebookCalls);
        Assert.Single(holds);
    }

    [Fact]
    public async Task Notebook_AroundAnOpenNotebook_IsInvalidArgument()
    {
        OpenNotebooksAre(("Other", Path.Combine(Target, "inner"), "{NBO00000-0000-4000-8000-000000000001}{1}{B0}"));

        Error(await NotebookTool(Location(NotebooksFolder)).CreateNotebook(NewNotebook, NotebooksFolder, Ct), "InvalidArgument");

        Assert.Empty(gateway.CreateNotebookCalls);
    }

    // Security review, 2026-11-13: a closed notebook isn't in OneNote's open list, so its .onetoc2 file marks it.

    [Theory]
    [InlineData("")]
    [InlineData("Group")]
    [InlineData(@"Group\not yet")]
    public async Task Notebook_AProposedFolderInAClosedNotebook_IsInvalidArgument_WithoutAskingTheUser(string under)
    {
        var closed = ClosedNotebook();
        Directory.CreateDirectory(Path.Combine(closed, "Group"));
        var proposed = under.Length == 0 ? closed : Path.Combine(closed, under);
        var prompt = Location(NotebooksFolder);

        var error = Error(await NotebookTool(prompt).CreateNotebook(NewNotebook, proposed, Ct), "InvalidArgument");

        Assert.Contains("notebook", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        AssertNoPathOrName(error);
        Assert.Empty(prompt.Questions);
        Assert.Empty(gateway.CreateNotebookCalls);
        Assert.Empty(holds);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Group")]
    public async Task Notebook_AChosenFolderInAClosedNotebook_IsInvalidArgument_NoLock_NoCom(string under)
    {
        var closed = ClosedNotebook();
        var chosen = Directory.CreateDirectory(Path.Combine(closed, under)).FullName;

        var error = Error(await NotebookTool(Location(chosen)).CreateNotebook(NewNotebook, NotebooksFolder, Ct), "InvalidArgument");

        AssertNoPathOrName(error);
        Assert.Empty(gateway.CreateNotebookCalls);
        Assert.Empty(holds);
    }

    [Fact]
    public async Task Notebook_AFolderNextToAClosedNotebook_IsCreated()
    {
        ClosedNotebook();
        ReadBackAt(Target);

        Ok(await NotebookTool(Location(NotebooksFolder)).CreateNotebook(NewNotebook, NotebooksFolder, Ct));

        Assert.Equal([Target], gateway.CreateNotebookCalls);
    }

    [Fact]
    public async Task Notebook_InsideOneNoteMcpsSettingsFolder_IsInvalidArgument()
    {
        Error(await NotebookTool(Location(SettingsFolder)).CreateNotebook(NewNotebook, NotebooksFolder, Ct), "InvalidArgument");

        Assert.Empty(gateway.CreateNotebookCalls);
    }

    [Fact]
    public async Task Notebook_InsideTheJournalFolder_IsInvalidArgument()
    {
        var journal = temp.Combine("journal");
        Directory.CreateDirectory(journal);
        File.WriteAllText(Store.Path, "{\"schemaVersion\": 1, \"journal\": {\"path\": " + JsonSerializer.Serialize(journal) + "}}");

        Error(await NotebookTool(Location(journal)).CreateNotebook(NewNotebook, NotebooksFolder, Ct), "InvalidArgument");

        Assert.Empty(gateway.CreateNotebookCalls);
    }

    [Fact]
    public void OwnFolders_IncludeLocalAppDataAndAppDataOneNoteMcp_WithoutCatchingTheTestFolder()
    {
        var own = NotebookFolders.OwnFolders(Store.Read());
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OneNoteMcp");
        var roaming = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OneNoteMcp");

        Assert.Contains(own, f => NotebookFolders.Same(f, local));
        Assert.Contains(own, f => NotebookFolders.Same(f, roaming));
        Assert.True(NotebookFolders.Inside(Path.Combine(local, "x"), local));
        Assert.False(NotebookFolders.Inside(local + "-Test\\x", local)); // %LOCALAPPDATA%\OneNoteMcp-Test, the throwaway root
        Assert.False(NotebookFolders.Inside(local, local));
    }

    [Theory]
    [InlineData(@"\\server\share\notebooks")]
    [InlineData(@"\\?\C:\notebooks")]
    [InlineData(@"\\.\C:\notebooks")]
    [InlineData(@"notebooks")]
    [InlineData(@"C:notebooks")]
    public async Task Notebook_AProposedFolderThatIsntLocal_IsInvalidArgument_WithoutAskingTheUser(string folder)
    {
        var prompt = Location(NotebooksFolder);

        var error = Error(await NotebookTool(prompt).CreateNotebook(NewNotebook, folder, Ct), "InvalidArgument");

        AssertNoPathOrName(error);
        Assert.Empty(prompt.Questions);
        Assert.Empty(gateway.CreateNotebookCalls);
        Assert.Empty(holds);
    }

    [Theory]
    [InlineData(@"\\server\share\notebooks")]
    [InlineData(@"relative")]
    public async Task Notebook_AChosenFolderThatIsntLocal_IsInvalidArgument(string chosen)
    {
        Error(await NotebookTool(Location(chosen)).CreateNotebook(NewNotebook, NotebooksFolder, Ct), "InvalidArgument");

        Assert.Empty(gateway.CreateNotebookCalls);
        Assert.Empty(holds);
    }

    [Fact]
    public async Task Notebook_AProposedFolderThatDoesntExist_StillAsksTheUser()
    {
        var missing = temp.Combine("missing");
        ReadBackAt(Target);
        var prompt = Location(NotebooksFolder);

        Ok(await NotebookTool(prompt).CreateNotebook(NewNotebook, missing, Ct));

        Assert.Equal(missing, Assert.Single(prompt.Questions).Folder);
    }

    [Fact]
    public async Task Notebook_AChosenFolderThatDoesntExist_IsInvalidArgument()
    {
        Error(await NotebookTool(Location(temp.Combine("missing"))).CreateNotebook(NewNotebook, NotebooksFolder, Ct), "InvalidArgument");

        Assert.Empty(gateway.CreateNotebookCalls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public async Task Notebook_NoChosenFolder_IsInvalidArgument(string? chosen)
    {
        Error(await NotebookTool(new ScriptedLocationPrompt(new NotebookLocationResult(GrantPromptStatus.Accepted, NotebookLocation.Local, chosen))).CreateNotebook(NewNotebook, NotebooksFolder, Ct), "InvalidArgument");

        Assert.Empty(gateway.CreateNotebookCalls);
    }

    [Theory]
    [InlineData("https://d.docs.live.net/0123456789abcdef/Documents")]
    [InlineData("http://example.invalid/x")]
    public async Task Notebook_AUrlFolder_IsNotSupported_WithoutAskingTheUser(string folder)
    {
        var prompt = Location(NotebooksFolder);

        var error = Error(await NotebookTool(prompt).CreateNotebook(NewNotebook, folder, Ct), "NotSupported");

        Assert.Equal(CloudNotebookCreate.ClosedUserAction, error.GetProperty("userAction").GetString());
        Assert.Empty(prompt.Questions);
        Assert.Empty(gateway.CreateNotebookCalls);
    }

    [Fact]
    public async Task Notebook_AUrlChosenInTheDialog_IsNotSupported()
    {
        Error(await NotebookTool(Location("https://d.docs.live.net/0123456789abcdef/Documents")).CreateNotebook(NewNotebook, NotebooksFolder, Ct), "NotSupported");

        Assert.Empty(gateway.CreateNotebookCalls);
    }

    [Fact]
    public async Task Notebook_OneDriveChosen_WhileTheCloudSeamIsClosed_IsNotSupported()
    {
        Assert.False(CloudNotebookCreate.Enabled);
        var prompt = new ScriptedLocationPrompt(new NotebookLocationResult(GrantPromptStatus.Accepted, NotebookLocation.OneDrive));

        Error(await NotebookTool(prompt).CreateNotebook(NewNotebook, NotebooksFolder, Ct), "NotSupported");

        Assert.Empty(gateway.CreateNotebookCalls);
        Assert.Empty(holds);
    }

    [Theory]
    [InlineData(GrantPromptStatus.Declined)]
    [InlineData(GrantPromptStatus.Cancelled)]
    public async Task Notebook_TheDialogDeclinedOrDismissed_IsCancelled_NoLock_NoCom(GrantPromptStatus status)
    {
        var error = Error(await NotebookTool(new ScriptedLocationPrompt(new NotebookLocationResult(status))).CreateNotebook(NewNotebook, NotebooksFolder, Ct), "Cancelled");

        Assert.Equal(CreateHierarchyUseCase.DeclinedUserAction, error.GetProperty("userAction").GetString());
        Assert.False(error.TryGetProperty("outcomeUnknown", out _));
        Assert.Empty(gateway.CreateNotebookCalls);
        Assert.Empty(holds);
    }

    [Fact]
    public async Task Notebook_AClientWithoutTheDialog_IsNotSupported_NoLock_NoCom()
    {
        foreach (var tool in new[] { NotebookTool(null), NotebookTool(new ScriptedLocationPrompt(NotebookLocationResult.Unsupported)) })
        {
            var error = Error(await tool.CreateNotebook(NewNotebook, NotebooksFolder, Ct), "NotSupported");
            Assert.Equal(CreateHierarchyUseCase.NoDialogUserAction, error.GetProperty("userAction").GetString());
            Assert.False(error.GetProperty("retryable").GetBoolean());
        }

        Assert.Empty(gateway.CreateNotebookCalls);
        Assert.Empty(holds);
    }

    [Theory]
    [InlineData("a/b")]
    [InlineData("..")]
    [InlineData("CON")]
    [InlineData("x.")]
    [InlineData("OneNote_RecycleBin")]
    [InlineData("Trips\u202Eabcd")]
    [InlineData("Trips\u2029abcd")]
    [InlineData("Trips\u200Dabcd")]
    [InlineData("Trips\u009Babcd")]
    public async Task Notebook_BadName_IsInvalidArgument_BeforeTheDialog(string name)
    {
        var prompt = Location(NotebooksFolder);

        var error = Error(await NotebookTool(prompt).CreateNotebook(name, NotebooksFolder, Ct), "InvalidArgument");

        Assert.DoesNotContain("abcd", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Empty(prompt.Questions);
        Assert.Empty(gateway.CreateNotebookCalls);
        Assert.Empty(holds);
    }

    [Fact]
    public async Task AnyKind_ANameOver128Characters_IsInvalidArgument_BeforeAnyDialogOrCom_NeverQuotingIt()
    {
        Grant();
        var longName = new string('q', SectionFileNames.MaxNameLength + 1);
        var prompt = Location(NotebooksFolder);

        foreach (var result in new[]
        {
            await SectionTool().CreateSection(NotebookId, longName, Ct),
            await GroupTool().CreateSectionGroup(NotebookId, longName, Ct),
            await NotebookTool(prompt).CreateNotebook(longName, NotebooksFolder, Ct),
        })
        {
            var error = Error(result, "InvalidArgument");
            Assert.Equal(CreateHierarchyUseCase.NameUserAction, error.GetProperty("userAction").GetString());
            Assert.DoesNotContain("qqqq", error.GetRawText(), StringComparison.Ordinal);
        }

        Assert.Empty(prompt.Questions);
        AssertNoCreate();
        Assert.Empty(holds);
        Assert.Empty(gateway.Calls);
    }

    [Fact]
    public async Task AnyKind_ANameOf128Characters_IsAccepted()
    {
        Grant();
        var name = new string('q', SectionFileNames.MaxNameLength);

        Ok(await SectionTool().CreateSection(NotebookId, name, Ct));
        Ok(await GroupTool().CreateSectionGroup(NotebookId, name, Ct));

        Assert.Single(gateway.CreateSectionCalls);
        Assert.Single(gateway.CreateGroupCalls);
    }

    [Theory]
    [InlineData(ErrorCode.Timeout)]
    [InlineData(ErrorCode.Cancelled)]
    [InlineData(ErrorCode.ComUnavailable)]
    public async Task Notebook_ComTimeout_IsOutcomeUnknown_SaysThereIsNoGrantYet_AndGrantsNothing(ErrorCode code)
    {
        gateway.CreateNotebookFailure = new OneNoteException(code, "m");

        var error = Error(await NotebookTool(Location(NotebooksFolder)).CreateNotebook(NewNotebook, NotebooksFolder, Ct), code.ToString());

        Assert.True(error.GetProperty("outcomeUnknown").GetBoolean());
        Assert.Contains("no write grant yet", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Single(gateway.CreateNotebookCalls);
        Assert.Empty(Store.Read().Notebooks);
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task Notebook_ADefiniteComFailure_IsNotOutcomeUnknown()
    {
        gateway.CreateNotebookFailure = new OneNoteException(ErrorCode.FileExists, "m", unchecked((int)0x8004201A));

        var error = Error(await NotebookTool(Location(NotebooksFolder)).CreateNotebook(NewNotebook, NotebooksFolder, Ct), "FileExists");

        Assert.False(error.TryGetProperty("outcomeUnknown", out _));
        Assert.Empty(Store.Read().Notebooks);
    }

    // --- The automatic grant ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Notebook_ReadBackFails_IsOk_CreatedNotGranted_WithTheCliUserAction()
    {
        // No read-back answer: the fake gateway's unknown-ID error.
        var root = Ok(await NotebookTool(Location(NotebooksFolder)).CreateNotebook(NewNotebook, NotebooksFolder, Ct));

        var warning = AssertCreatedNotGranted(root);
        Assert.Contains("grant --write \"<notebook name>\"", warning.GetProperty("userAction").GetString(), StringComparison.Ordinal);
        Assert.Contains("Never run the grant command yourself", warning.GetProperty("userAction").GetString(), StringComparison.Ordinal);
        Assert.False(root.GetProperty("meta").TryGetProperty("path", out _));
        Assert.Empty(Store.Read().Notebooks);
    }

    [Fact]
    public async Task Notebook_ReadBackElsewhere_IsOk_CreatedNotGranted()
    {
        ReadBackAt(Path.Combine(NotebooksFolder, "Somewhere else"));

        var root = Ok(await NotebookTool(Location(NotebooksFolder)).CreateNotebook(NewNotebook, NotebooksFolder, Ct));

        AssertCreatedNotGranted(root);
        Assert.False(root.GetProperty("meta").TryGetProperty("path", out _));
        Assert.Empty(Store.Read().Notebooks);
    }

    [Fact]
    public async Task Section_WhenBothKindsShareTheName_TheSameKindIsReturned()
    {
        Grant();
        Children(GroupId, $"""
            <one:SectionGroup {Ns} name="Archive" ID="{GroupId}" path="C:\notebooks\W\Archive">
              <one:Section name="Twin" ID="{PlansId}" path="C:\notebooks\W\Archive\Twin.one" />
              <one:SectionGroup name="twin" ID="{OldId}" path="C:\notebooks\W\Archive\twin" />
            </one:SectionGroup>
            """);

        var section = Ok(await SectionTool().CreateSection(GroupId, "TWIN", Ct));
        var group = Ok(await GroupTool().CreateSectionGroup(GroupId, "Twin", Ct));

        Assert.Equal(PlansId, section.GetProperty("meta").GetProperty("objectId").GetString());
        Assert.Equal(OldId, group.GetProperty("meta").GetProperty("objectId").GetString());
        AssertNoCreate();
    }

    [Fact]
    public async Task Notebook_ReturnedIdWasAlreadyOpen_IsOk_CreatedNotGranted()
    {
        const string openId = "{NBO00000-0000-4000-8000-000000000001}{1}{B0}";
        OpenNotebooksAre(("Other", @"C:\notebooks\Other", openId));
        gateway.OnCreateNotebook = _ => openId;
        Self(openId, $"<one:Notebook {Ns} name=\"Other\" ID=\"{openId}\" path=\"{Target}\" />");
        Children(openId, $"<one:Notebook {Ns} name=\"Other\" ID=\"{openId}\" path=\"{Target}\" />");

        var root = Ok(await NotebookTool(Location(NotebooksFolder)).CreateNotebook(NewNotebook, NotebooksFolder, Ct));

        AssertCreatedNotGranted(root);
        Assert.Empty(Store.Read().Notebooks);
    }

    // Security review, 2026-11-13: only an empty notebook is new enough to grant; one holding sections or section groups
    // may be an existing notebook OneNote opened instead.

    [Theory]
    [InlineData("<one:Section name=\"Old\" ID=\"{SN000000-0000-4000-8000-000000000001}{1}{B0}\" path=\"x.one\" />")]
    [InlineData("<one:SectionGroup name=\"Old\" ID=\"{GN000000-0000-4000-8000-000000000001}{1}{B0}\" path=\"x\" />")]
    [InlineData("<one:SectionGroup name=\"OneNote_RecycleBin\" ID=\"{RN000000-0000-4000-8000-000000000001}{1}{B0}\" path=\"r\" isRecycleBin=\"true\" /><one:Section name=\"Old\" ID=\"{SN000000-0000-4000-8000-000000000001}{1}{B0}\" path=\"x.one\" />")]
    public async Task Notebook_ReadBackHoldingSectionsOrGroups_IsOk_CreatedNotGranted(string children)
    {
        ReadBackAt(Target, children);

        var root = Ok(await NotebookTool(Location(NotebooksFolder)).CreateNotebook(NewNotebook, NotebooksFolder, Ct));

        var warning = AssertCreatedNotGranted(root);
        Assert.Contains("already holds sections or section groups", warning.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain(NewNotebook, warning.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(Target, root.GetProperty("meta").GetProperty("path").GetString());
        Assert.Empty(Store.Read().Notebooks);
        Assert.Single(gateway.CreateNotebookCalls);
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task Notebook_ReadBackHoldingOnlyTheRecycleBin_IsGranted()
    {
        ReadBackAt(Target, "<one:SectionGroup name=\"OneNote_RecycleBin\" ID=\"{RN000000-0000-4000-8000-000000000001}{1}{B0}\" path=\"r\" isRecycleBin=\"true\" />");

        var root = Ok(await NotebookTool(Location(NotebooksFolder)).CreateNotebook(NewNotebook, NotebooksFolder, Ct));

        Assert.Equal(0, root.GetProperty("warnings").GetArrayLength());
        Assert.Equal(GrantedVia.Created, Store.Read().Notebooks[GrantKeys.NotebookKey(Target)].GrantedVia);
    }

    [Fact]
    public async Task Notebook_ReadBack_IsOneChildrenScopeRead_OfTheNewNotebook()
    {
        ReadBackAt(Target);

        Ok(await NotebookTool(Location(NotebooksFolder)).CreateNotebook(NewNotebook, NotebooksFolder, Ct));

        Assert.Equal([(FakeGateway.CreatedNotebookId(1), HierarchyScope.Children)], gateway.Calls.Where(c => c.Item1 == FakeGateway.CreatedNotebookId(1)));
    }

    [Fact]
    public async Task Notebook_TheSettingsMutexHeldPastItsTimeout_IsOk_CreatedNotGranted_AndNothingElseIsWritten()
    {
        ReadBackAt(Target);
        File.WriteAllText(Store.Path, """{"schemaVersion": 1}""");
        var before = File.ReadAllText(Store.Path);

        using var holder = SettingsMutexHolder.Take();
        var root = Ok(await NotebookTool(Location(NotebooksFolder), settingsLockTimeout: TimeSpan.FromMilliseconds(200)).CreateNotebook(NewNotebook, NotebooksFolder, Ct));
        holder.Release();

        var warning = AssertCreatedNotGranted(root);
        Assert.Contains("ComBusy", warning.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(Target, root.GetProperty("meta").GetProperty("path").GetString());
        Assert.Equal(before, File.ReadAllText(Store.Path));
        Assert.Single(gateway.CreateNotebookCalls);
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task Notebook_TheGrantIsWritten_UnderTheSettingsMutex_InsideTheWriteLock()
    {
        // AD-7: Local\OneNoteMcp.Write outside, Local\OneNoteMcp.Settings inside. The settings mutex is taken by another
        // thread at the read-back, so the grant save waits on it; meanwhile the write lock must still be held.
        ReadBackAt(Target);
        SettingsMutexHolder? holder = null;
        var hooked = new HookedGateway(gateway)
        {
            BeforeHierarchy = (id, scope) =>
            {
                if (id == FakeGateway.CreatedNotebookId(1) && scope == HierarchyScope.Children)
                {
                    holder = SettingsMutexHolder.Take();
                }
            },
        };

        var tool = NotebookTool(Location(NotebooksFolder), over: hooked, settingsLockTimeout: TimeSpan.FromSeconds(30));
        var create = Task.Run(() => tool.CreateNotebook(NewNotebook, NotebooksFolder, Ct), Ct);
        await WaitUntilAsync(() => holder is not null);
        await Task.Delay(200, Ct);

        Assert.False(create.IsCompleted);
        await AssertLockHeldAsync();
        Assert.Empty(Store.Read().Notebooks);
        holder!.Release();

        Ok(await create);
        Assert.Equal(GrantedVia.Created, Store.Read().Notebooks[GrantKeys.NotebookKey(Target)].GrantedVia);
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task Notebook_SuccessAndRefusals_NeverNameTheNotebookOrItsFolderInMessages()
    {
        // The success's meta.path is the one place the folder appears (Q5), and it carries no message.
        ReadBackAt(Target);
        var root = Ok(await NotebookTool(Location(NotebooksFolder)).CreateNotebook(NewNotebook, NotebooksFolder, Ct));
        var withoutPath = root.GetProperty("warnings").GetRawText();
        Assert.DoesNotContain(NewNotebook, withoutPath, StringComparison.Ordinal);

        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Target).FullName, "Open Notebook.onetoc2"), "x");
        var refused = Error(await NotebookTool(Location(NotebooksFolder)).CreateNotebook(NewNotebook, NotebooksFolder, Ct), "FileExists");
        AssertNoPathOrName(refused);
    }

    // --- The location dialog's elicitation form -------------------------------------------------------------------------

    [Fact]
    public void LocationDialog_OffersALocalFolderOnly_WhileTheSeamIsClosed_PrefilledAndRequired()
    {
        var request = ElicitationNotebookLocationPrompt.Request(new NotebookLocationQuestion("Notebook A", @"C:\Users\x\Notebooks", OfferOneDrive: false));

        Assert.Contains("\"Notebook A\"", request.Message, StringComparison.Ordinal);
        Assert.Contains("--revoke --write", request.Message, StringComparison.Ordinal);
        var properties = request.RequestedSchema!.Properties;
        var location = Assert.IsType<ElicitRequestParams.TitledSingleSelectEnumSchema>(properties[ElicitationNotebookLocationPrompt.LocationField]);
        Assert.Equal([NotebookLocations.Local], location.OneOf.Select(o => o.Const));
        var folder = Assert.IsType<ElicitRequestParams.StringSchema>(properties[ElicitationNotebookLocationPrompt.FolderField]);
        Assert.Equal(@"C:\Users\x\Notebooks", folder.Default);
        Assert.Equal([ElicitationNotebookLocationPrompt.LocationField, ElicitationNotebookLocationPrompt.FolderField], request.RequestedSchema.Required);
    }

    [Fact]
    public void LocationDialog_OffersOneDrive_OnlyWhenAsked()
    {
        var request = ElicitationNotebookLocationPrompt.Request(new NotebookLocationQuestion("Notebook A", null, OfferOneDrive: true));

        var location = Assert.IsType<ElicitRequestParams.TitledSingleSelectEnumSchema>(request.RequestedSchema!.Properties[ElicitationNotebookLocationPrompt.LocationField]);
        Assert.Equal([NotebookLocations.Local, NotebookLocations.OneDrive], location.OneOf.Select(o => o.Const));
        Assert.Null(Assert.IsType<ElicitRequestParams.StringSchema>(request.RequestedSchema.Properties[ElicitationNotebookLocationPrompt.FolderField]).Default);
    }

    [Fact]
    public void LocationDialog_Answers()
    {
        Assert.Equal(GrantPromptStatus.Declined, ElicitationNotebookLocationPrompt.Read(new ElicitResult { Action = "decline" }).Status);
        Assert.Equal(GrantPromptStatus.Cancelled, ElicitationNotebookLocationPrompt.Read(new ElicitResult { Action = "cancel" }).Status);
        Assert.Equal(GrantPromptStatus.Cancelled, ElicitationNotebookLocationPrompt.Read(new ElicitResult { Action = "other" }).Status);

        Assert.Equal(
            new NotebookLocationResult(GrantPromptStatus.Accepted, NotebookLocation.Local, @"D:\N"),
            ElicitationNotebookLocationPrompt.Read(Accept(NotebookLocations.Local, @"D:\N")));
        Assert.Equal(
            new NotebookLocationResult(GrantPromptStatus.Accepted, NotebookLocation.Local, @"D:\N"),
            ElicitationNotebookLocationPrompt.Read(Accept("elsewhere", @"D:\N")));
        Assert.Equal(
            new NotebookLocationResult(GrantPromptStatus.Accepted, NotebookLocation.OneDrive),
            ElicitationNotebookLocationPrompt.Read(Accept(NotebookLocations.OneDrive, @"D:\N")));
        Assert.Equal(
            new NotebookLocationResult(GrantPromptStatus.Accepted, NotebookLocation.Local),
            ElicitationNotebookLocationPrompt.Read(new ElicitResult { Action = "accept" }));

        static ElicitResult Accept(string location, string folder) => new()
        {
            Action = "accept",
            Content = new Dictionary<string, JsonElement>
            {
                [ElicitationNotebookLocationPrompt.LocationField] = JsonSerializer.SerializeToElement(location),
                [ElicitationNotebookLocationPrompt.FolderField] = JsonSerializer.SerializeToElement(folder),
            },
        };
    }

    // --- Helpers ----------------------------------------------------------------------------------------------------

    private void Self(string id, string xml) =>
        gateway.Hierarchies[(id, HierarchyScope.Self)] = "<?xml version=\"1.0\"?>\n" + xml;

    private void Children(string id, string xml) =>
        gateway.Hierarchies[(id, HierarchyScope.Children)] = "<?xml version=\"1.0\"?>\n" + xml;

    /// <summary>The new notebook reads back at <paramref name="path"/>, holding <paramref name="children"/> (XML; none by default).</summary>
    private void ReadBackAt(string path, string children = "")
    {
        var id = FakeGateway.CreatedNotebookId(1);
        Self(id, $"<one:Notebook {Ns} name=\"{NewNotebook}\" ID=\"{id}\" path=\"{path}\" />");
        Children(id, $"<one:Notebook {Ns} name=\"{NewNotebook}\" ID=\"{id}\" path=\"{path}\">{children}</one:Notebook>");
    }

    /// <summary>A closed notebook's folder in <see cref="NotebooksFolder"/>, beside the new one: it holds an <c>.onetoc2</c> file and isn't open.</summary>
    private string ClosedNotebook()
    {
        var closed = Directory.CreateDirectory(Path.Combine(NotebooksFolder, "Closed")).FullName;
        File.WriteAllText(Path.Combine(closed, "Open Notebook.onetoc2"), "x");
        return closed;
    }

    /// <summary>The open notebooks, at <c>Notebooks</c> and <c>Sections</c> scope: W, plus <paramref name="others"/>.</summary>
    private void OpenNotebooksAre(params (string Name, string Path, string Id)[] others)
    {
        var xml = new System.Text.StringBuilder("<?xml version=\"1.0\"?>\n<one:Notebooks " + Ns + ">");
        xml.Append($"<one:Notebook name=\"W\" nickname=\"W\" ID=\"{NotebookId}\" path=\"{NotebookPath}\" />");
        foreach (var (name, path, id) in others)
        {
            xml.Append($"<one:Notebook name=\"{name}\" nickname=\"{name}\" ID=\"{id}\" path=\"{path}\" />");
        }

        var text = xml.Append("</one:Notebooks>").ToString();
        gateway.Hierarchies[(string.Empty, HierarchyScope.Notebooks)] = text;
        gateway.Hierarchies[(string.Empty, HierarchyScope.Sections)] = text;
    }

    private void Grant() =>
        new SettingsWriter(Store, clock).SaveWriteGrant(
            new NotebookGrant(GrantKeys.NotebookKey(NotebookPath), "W", NotebookId, Write: true, PermanentDelete: false, clock.GetUtcNow(), GrantedVia.Cli));

    private CreateHierarchyUseCase UseCase(IOneNoteGateway? over = null, TimeSpan? settingsLockTimeout = null)
    {
        var g = over ?? gateway;
        return new CreateHierarchyUseCase(
            g,
            new WriteAccess(g, Store.Read),
            new WritePipeline(writeLock, g, cache, Store.Read, TimeProvider.System, null, new WriteGrantDialog(g, new SettingsWriter(Store, clock), clock)),
            Store.Read,
            new SettingsWriter(Store, clock, settingsLockTimeout),
            clock);
    }

    private static ToolRunner Runner() => new(NullLogger<ToolRunner>.Instance);

    private CreateSectionTool SectionTool() => new(Runner(), UseCase());

    private CreateSectionGroupTool GroupTool() => new(Runner(), UseCase());

    /// <summary>
    /// <c>create_notebook</c> as the tool runs it, with <paramref name="prompt"/> as the location dialog (the tool's own
    /// MCP adapter needs a live client, so the call goes through the runner directly).
    /// </summary>
    private NotebookCall NotebookTool(INotebookLocationPrompt? prompt, IOneNoteGateway? over = null, TimeSpan? settingsLockTimeout = null) =>
        new(UseCase(over, settingsLockTimeout), prompt);

    private sealed class NotebookCall(CreateHierarchyUseCase useCase, INotebookLocationPrompt? prompt)
    {
        public Task<CallToolResult> CreateNotebook(string name, string? folder = null, CancellationToken cancellationToken = default) =>
            Runner().RunAsync(
                CreateNotebookTool.Name,
                useCase,
                (context, ct) => useCase.CreateNotebookAsync(context, name, folder, prompt, ct),
                cancellationToken);
    }

    private Task<CallToolResult> RunWithGrantPrompt(IGrantPrompt prompt, Func<CreateHierarchyUseCase, ToolCallContext, CancellationToken, Task<ToolSuccess>> run)
    {
        var useCase = UseCase();
        return Runner().RunAsync(CreateSectionTool.Name, useCase, (context, ct) => run(useCase, context, ct), Ct, prompt);
    }

    private static ScriptedLocationPrompt Location(string folder) =>
        new(new NotebookLocationResult(GrantPromptStatus.Accepted, NotebookLocation.Local, folder));

    private sealed class ScriptedLocationPrompt(NotebookLocationResult answer) : INotebookLocationPrompt
    {
        public List<NotebookLocationQuestion> Questions { get; } = [];

        public Task<NotebookLocationResult> AskAsync(NotebookLocationQuestion question, CancellationToken ct)
        {
            Questions.Add(question);
            return Task.FromResult(answer);
        }
    }

    private sealed class ScriptedGrantPrompt(GrantPromptStatus status, string? typed) : IGrantPrompt
    {
        public int Asked { get; private set; }

        public Task<GrantPromptResult> AskAsync(GrantQuestion question, CancellationToken ct) =>
            throw new InvalidOperationException("A create never asks for a read grant.");

        public Task<GrantPromptResult> AskWriteAsync(WriteGrantQuestion question, CancellationToken ct)
        {
            Asked++;
            return Task.FromResult(new GrantPromptResult(status, typed));
        }
    }

    /// <summary>Holds the real <c>Local\OneNoteMcp.Settings</c> mutex on a thread of its own until released (a mutex belongs to its thread).</summary>
    private sealed class SettingsMutexHolder : IDisposable
    {
        private readonly ManualResetEventSlim taken = new();
        private readonly ManualResetEventSlim release = new();
        private readonly Thread thread;

        private SettingsMutexHolder()
        {
            thread = new Thread(() =>
            {
                using var mutex = new Mutex(initiallyOwned: false, SettingsWriter.MutexName);
                try
                {
                    mutex.WaitOne();
                }
                catch (AbandonedMutexException)
                {
                    // Taken all the same.
                }

                taken.Set();
                release.Wait();
                mutex.ReleaseMutex();
            })
            { IsBackground = true };
            thread.Start();
            taken.Wait();
        }

        public static SettingsMutexHolder Take() => new();

        public void Release()
        {
            release.Set();
            thread.Join();
        }

        public void Dispose()
        {
            if (thread.IsAlive)
            {
                Release();
            }

            taken.Dispose();
            release.Dispose();
        }
    }

    /// <summary>The fake gateway, with a hook before each <c>GetHierarchy</c>.</summary>
    private sealed class HookedGateway(FakeGateway inner) : IOneNoteGateway
    {
        public Action<string, HierarchyScope>? BeforeHierarchy { get; init; }

        public Task<string> GetHierarchyAsync(string startNodeId, HierarchyScope scope, CancellationToken ct)
        {
            BeforeHierarchy?.Invoke(startNodeId, scope);
            return inner.GetHierarchyAsync(startNodeId, scope, ct);
        }

        public Task<string> GetHierarchyParentAsync(string objectId, CancellationToken ct) => inner.GetHierarchyParentAsync(objectId, ct);

        public Task<string> GetPageContentAsync(string pageId, PageInfo pageInfo, CancellationToken ct) => inner.GetPageContentAsync(pageId, pageInfo, ct);

        public Task<string> GetBinaryPageContentAsync(string pageId, string callbackId, CancellationToken ct) => inner.GetBinaryPageContentAsync(pageId, callbackId, ct);

        public Task<string> FindPagesAsync(string startNodeId, string query, bool includeUnindexed, CancellationToken ct) => inner.FindPagesAsync(startNodeId, query, includeUnindexed, ct);

        public Task<string> FindMetaAsync(string startNodeId, string name, bool includeUnindexed, CancellationToken ct) => inner.FindMetaAsync(startNodeId, name, includeUnindexed, ct);

        public Task<string> GetHyperlinkToObjectAsync(string hierarchyId, string pageContentObjectId, CancellationToken ct) => inner.GetHyperlinkToObjectAsync(hierarchyId, pageContentObjectId, ct);

        public Task<string> GetWebHyperlinkToObjectAsync(string hierarchyId, string pageContentObjectId, CancellationToken ct) => inner.GetWebHyperlinkToObjectAsync(hierarchyId, pageContentObjectId, ct);

        public Task<string> GetSpecialLocationAsync(SpecialLocation location, CancellationToken ct) => inner.GetSpecialLocationAsync(location, ct);

        public Task NavigateToAsync(string hierarchyId, string objectId, CancellationToken ct) => inner.NavigateToAsync(hierarchyId, objectId, ct);

        public Task<string> CreateNewPageAsync(string sectionId, NewPageStyle style, CancellationToken ct) => inner.CreateNewPageAsync(sectionId, style, ct);

        public Task<string> CreateSectionAsync(string parentId, string sectionFileName, CancellationToken ct) => inner.CreateSectionAsync(parentId, sectionFileName, ct);

        public Task<string> CreateSectionGroupAsync(string parentId, string groupName, CancellationToken ct) => inner.CreateSectionGroupAsync(parentId, groupName, ct);

        public Task<string> CreateNotebookAsync(string folderPath, CancellationToken ct) => inner.CreateNotebookAsync(folderPath, ct);

        public Task UpdatePageContentAsync(string pageXml, string expectedLastModified, CancellationToken ct) => inner.UpdatePageContentAsync(pageXml, expectedLastModified, ct);

        public Task DeletePageContentAsync(string pageId, string objectId, string expectedLastModified, CancellationToken ct) => inner.DeletePageContentAsync(pageId, objectId, expectedLastModified, ct);

        public Task WaitForAbandonedWritesAsync(CancellationToken ct) => inner.WaitForAbandonedWritesAsync(ct);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting");
            await Task.Delay(10, Ct);
        }
    }

    private void AssertNoCreate()
    {
        Assert.Empty(gateway.CreateSectionCalls);
        Assert.Empty(gateway.CreateGroupCalls);
        Assert.Empty(gateway.CreateNotebookCalls);
    }

    private void AssertNoPathOrName(JsonElement error)
    {
        var text = error.GetRawText();
        Assert.DoesNotContain(NewNotebook, text, StringComparison.Ordinal);
        Assert.DoesNotContain(JsonSerializer.Serialize(temp.Path).Trim('"'), text, StringComparison.OrdinalIgnoreCase);
    }

    private static JsonElement AssertCreatedNotGranted(JsonElement root)
    {
        var warning = Assert.Single(root.GetProperty("warnings").EnumerateArray());
        Assert.Equal(ToolWarnings.CreatedNotGranted, warning.GetProperty("code").GetString());
        Assert.Contains(root.GetProperty("meta").GetProperty("objectId").GetString()!, warning.GetProperty("message").GetString(), StringComparison.Ordinal);
        return warning;
    }

    private async Task AssertLockFreeAsync()
    {
        using var probe = await writeLock.AcquireAsync("free?", TimeSpan.FromMilliseconds(500), Ct);
    }

    private async Task AssertLockHeldAsync()
    {
        // From another thread: the write lock is a mutex, and the pipeline may hold it on this one.
        var busy = await Task.Run(() => Assert.ThrowsAsync<OneNoteException>(() => writeLock.AcquireAsync("held?", TimeSpan.FromMilliseconds(100), Ct)), Ct);
        Assert.Equal(ErrorCode.ComBusy, busy.Code);
    }

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
        Assert.True(result.IsError, result.StructuredContent?.GetRawText());
        TextBlockAssert.Matches(result);
        var error = result.StructuredContent!.Value.GetProperty("error");
        Assert.Equal(code, error.GetProperty("code").GetString());
        return error;
    }
}
