using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OneNoteMcp.Core;
using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.AppState;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Core.Writes;
using OneNoteMcp.Server;
using OneNoteMcp.Server.Tools;
using OneNoteMcp.Xml;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>
/// Story 4.5's <c>open_notebook</c>, one test per row of the plan's I/O matrix and more, through tool, use case, the
/// <c>AppState</c> pipeline with its write lock (a test mutex of its own) and <see cref="FakeGateway"/>. Links and the file
/// system are scripted (<see cref="FakeLinks"/>, <see cref="FakeFolders"/>), except in the tests on a real temporary folder,
/// which check the acceptance criterion and the real probe. Nothing here reaches real OneNote.
/// </summary>
public sealed class OpenNotebookToolTests
{
    private const string LocalId = "{4B0B0000-0000-4000-8000-0000000045A1}{1}{B0}";
    private const string CloudId = "{4B0B0000-0000-4000-8000-0000000045B2}{1}{B0}";
    private const string LocalPath = @"C:\Notebooks\Notebook A";
    private const string ClosedPath = @"C:\Notebooks\Notebook C";
    private const string CloudPath = "https://d.docs.live.net/0000000000000000/Documents/Notebook B";
    private const string OneDriveFolder = @"C:\Users\Someone\OneDrive";
    private const string Ns = OneNoteXml.Namespace2013;

    private static readonly string NotebooksXml = Notebooks(
        ("Notebook A", LocalId, LocalPath),
        ("Notebook B", CloudId, CloudPath));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OpenOk_SendsTheCanonicalPath_ReturnsTheNewId_TheNotebookElement_MetaAndNotebookListMayLag()
    {
        var gateway = Gateway();
        var opened = Notebooks(("Notebook A", LocalId, LocalPath), ("Notebook C", gateway.OpenedId, ClosedPath));
        gateway.OnOpenHierarchyCall = (_, _) =>
        {
            gateway.Hierarchies[(string.Empty, HierarchyScope.Notebooks)] = opened;
            return Task.CompletedTask;
        };
        var log = new List<OpenNotebookLogEntry>();
        var writeLock = AppStatePipelineTests.TestLock();

        var root = Ok(await Tool(gateway, Folders().Notebook(ClosedPath), writeLock: writeLock, log: log).OpenNotebook(ClosedPath, Ct));

        Assert.Equal([ClosedPath], gateway.OpenHierarchyCalls);
        Assert.Equal(2, gateway.Calls.Count); // step 3's read, then the read after the open
        var xml = root.GetProperty("xml").GetString()!;
        Assert.Equal(HierarchyNode.NotebookElement(opened, gateway.OpenedId), xml);
        var meta = root.GetProperty("meta");
        Assert.Equal(["correlationId", "objectId", "path"], meta.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(gateway.OpenedId, meta.GetProperty("objectId").GetString());
        Assert.Equal(ClosedPath, meta.GetProperty("path").GetString());
        var warning = Assert.Single(root.GetProperty("warnings").EnumerateArray());
        Assert.Equal(ToolWarnings.NotebookListMayLag, warning.GetProperty("code").GetString());
        Assert.Equal(OpenNotebookUseCase.ListMayLagMessage, warning.GetProperty("message").GetString());
        Assert.Equal(OpenNotebookUseCase.ListMayLagUserAction, warning.GetProperty("userAction").GetString());

        var entry = Assert.Single(log);
        Assert.Equal(("localPath", OpenNotebookUseCase.Rules.None, "opened"), (entry.PathKind, entry.Rule, entry.Outcome));
        Assert.True(await AppStatePipelineTests.CanTakeAsync(writeLock));
    }

    [Fact]
    public async Task OpenOk_ThroughAJunction_SendsTheJunctionsTarget_AndMetaPathIsIt()
    {
        var gateway = Gateway();
        var links = new FakeLinks(new() { [@"C:\Links\NB"] = ClosedPath });

        var root = Ok(await Tool(gateway, Folders().Notebook(ClosedPath), links: links).OpenNotebook(@"C:\Links\NB", Ct));

        Assert.Equal([ClosedPath], gateway.OpenHierarchyCalls);
        Assert.Equal(ClosedPath, root.GetProperty("meta").GetProperty("path").GetString());
    }

    [Fact]
    public async Task OpenOk_WithTheNotebookNotYetListed_LeavesXmlOut_TheOpenStands()
    {
        var gateway = Gateway();

        var root = Ok(await Tool(gateway, Folders().Notebook(ClosedPath)).OpenNotebook(ClosedPath, Ct));

        Assert.False(root.TryGetProperty("xml", out var xml) && xml.ValueKind == JsonValueKind.String);
        Assert.Equal(gateway.OpenedId, root.GetProperty("meta").GetProperty("objectId").GetString());
    }

    [Fact]
    public async Task TheLockIsHeld_ThroughTheOpen()
    {
        var gateway = Gateway();
        var writeLock = AppStatePipelineTests.TestLock();
        var held = false;
        gateway.OnOpenHierarchyCall = async (_, _) => held = !await AppStatePipelineTests.CanTakeAsync(writeLock);

        Ok(await Tool(gateway, Folders().Notebook(ClosedPath), writeLock: writeLock).OpenNotebook(ClosedPath, Ct));

        Assert.True(held);
    }

    [Theory]
    [InlineData(LocalPath)]
    [InlineData(@"c:\notebooks\NOTEBOOK A\")]
    [InlineData(@"C:/Notebooks/Notebook A")]
    public async Task AlreadyOpen_ByItsNotebookKey_ReturnsItsCurrentId_AndAlreadyExisted_NoOpenHierarchy(string path)
    {
        var gateway = Gateway();
        var log = new List<OpenNotebookLogEntry>();

        var root = Ok(await Tool(gateway, Folders().Notebook(LocalPath), log: log).OpenNotebook(path, Ct));

        Assert.Empty(gateway.OpenHierarchyCalls);
        Assert.Single(gateway.Calls);
        Assert.Equal(LocalId, root.GetProperty("meta").GetProperty("objectId").GetString());
        Assert.Equal(HierarchyNode.NotebookElement(NotebooksXml, LocalId), root.GetProperty("xml").GetString());
        var warning = Assert.Single(root.GetProperty("warnings").EnumerateArray());
        Assert.Equal(ToolWarnings.AlreadyExisted, warning.GetProperty("code").GetString());
        Assert.Contains(LocalId, warning.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal("alreadyOpen", Assert.Single(log).Outcome);
    }

    [Fact]
    public async Task AlreadyOpen_UnderALocalhostAdminSharePath_IsMatched_NoOpenHierarchy()
    {
        const string id = "{4B0B0000-0000-4000-8000-0000000045C3}{1}{B0}";
        var gateway = Gateway(Notebooks(("Notebook L", id, @"\\localhost\c$\Notebooks\Notebook L")));

        var root = Ok(await Tool(gateway, Folders().Notebook(@"C:\Notebooks\Notebook L")).OpenNotebook(@"C:\Notebooks\Notebook L", Ct));

        Assert.Empty(gateway.OpenHierarchyCalls);
        Assert.Equal(id, root.GetProperty("meta").GetProperty("objectId").GetString());
        Assert.Equal(ToolWarnings.AlreadyExisted, root.GetProperty("warnings")[0].GetProperty("code").GetString());
    }

    [Fact]
    public async Task AlreadyOpen_ThroughAJunction_EitherWay_IsMatched_NoOpenHierarchy()
    {
        // OneNote keeps the path a notebook was opened by (spike 4.1 O9): the open notebook's path is the junction here.
        const string id = "{4B0B0000-0000-4000-8000-0000000045D4}{1}{B0}";
        var links = new FakeLinks(new() { [@"C:\Links\NB"] = ClosedPath, [@"C:\Links\NB2"] = ClosedPath });
        var gateway = Gateway(Notebooks(("NB", id, @"C:\Links\NB")));

        var direct = Ok(await Tool(gateway, Folders().Notebook(ClosedPath), links: links).OpenNotebook(ClosedPath, Ct));
        var otherLink = Ok(await Tool(gateway, Folders().Notebook(ClosedPath), links: links).OpenNotebook(@"C:\Links\NB2", Ct));

        Assert.Empty(gateway.OpenHierarchyCalls);
        Assert.Equal(id, direct.GetProperty("meta").GetProperty("objectId").GetString());
        Assert.Equal(id, otherLink.GetProperty("meta").GetProperty("objectId").GetString());
    }

    [Fact]
    public void Names_NeverMatchesAUrl_AnotherServer_OrASiblingWithTheSamePrefix()
    {
        Assert.False(OpenNotebookUseCase.Names(CloudPath, ClosedPath, new FakeLinks([])));
        Assert.False(OpenNotebookUseCase.Names(@"\\otherhost\c$\Notebooks\Notebook C", ClosedPath, new FakeLinks([])));
        Assert.False(OpenNotebookUseCase.Names(@"\\localhost\share\Notebooks\Notebook C", ClosedPath, new FakeLinks([])));
        Assert.False(OpenNotebookUseCase.Names(@"C:\Notebooks\Notebook C2", ClosedPath, new FakeLinks([])));
        Assert.False(OpenNotebookUseCase.Names(string.Empty, ClosedPath, new FakeLinks([])));
        Assert.True(OpenNotebookUseCase.Names(@"\\LOCALHOST\C$\Notebooks\Notebook C\", ClosedPath, new FakeLinks([])));
        Assert.Equal(@"D:\", OpenNotebookUseCase.MapLocalhostAdminShare(@"\\localhost\d$"));
    }

    [Fact]
    public void OpenForms_NameTheFolder_AsWritten_ThroughTheAdminShare_AndCanonical()
    {
        var links = new FakeLinks(new() { [@"C:\Links\NB"] = ClosedPath });

        Assert.Empty(NotebookFolderPath.OpenForms(CloudPath, links));
        Assert.Empty(NotebookFolderPath.OpenForms(string.Empty, links));
        Assert.Empty(NotebookFolderPath.OpenForms("  ", links));
        Assert.Empty(NotebookFolderPath.OpenForms(null, links));

        var share = NotebookFolderPath.OpenForms(@"\\LOCALHOST\C$\Notebooks\Notebook C\", links);
        Assert.Contains(@"\\LOCALHOST\C$\Notebooks\Notebook C", share);
        Assert.Contains(ClosedPath, share);

        Assert.Contains(ClosedPath, NotebookFolderPath.OpenForms(@"C:\Links\NB", links));
        Assert.Contains(ClosedPath, NotebookFolderPath.OpenForms(@"\\localhost\c$\Links\NB", links));

        var other = NotebookFolderPath.OpenForms(@"\\otherhost\c$\Notebooks\Notebook C", links);
        Assert.DoesNotContain(other, f => string.Equals(f, ClosedPath, StringComparison.OrdinalIgnoreCase));
    }

    public static readonly TheoryData<string, string, string> StepOneRefusals = new()
    {
        { CloudPath, OpenNotebookUseCase.Rules.Url, OpenNotebookUseCase.UrlUserAction },
        { "http://example.invalid/Notebook", OpenNotebookUseCase.Rules.Url, OpenNotebookUseCase.UrlUserAction },
        { "https:Notebook", OpenNotebookUseCase.Rules.Url, OpenNotebookUseCase.UrlUserAction },
        { @"\\server\share\Notebook", OpenNotebookUseCase.Rules.Network, OpenNotebookUseCase.PathUserAction },
        { @"\\localhost\c$\Notebooks\Notebook C", OpenNotebookUseCase.Rules.Network, OpenNotebookUseCase.PathUserAction },
        { @"\\?\C:\Notebooks\Notebook C", OpenNotebookUseCase.Rules.Device, OpenNotebookUseCase.PathUserAction },
        { @"\\.\C:\Notebooks\Notebook C", OpenNotebookUseCase.Rules.Device, OpenNotebookUseCase.PathUserAction },
        { @"Notebooks\Notebook C", OpenNotebookUseCase.Rules.NotFullyQualified, OpenNotebookUseCase.PathUserAction },
        { @"C:Notebook C", OpenNotebookUseCase.Rules.NotFullyQualified, OpenNotebookUseCase.PathUserAction },
        { @"C:\Notebooks\Note" + (char)0x202E + "book C", OpenNotebookUseCase.Rules.InvalidCharacters, OpenNotebookUseCase.PathUserAction },
        { @"C:\Notebooks\Note*", OpenNotebookUseCase.Rules.InvalidCharacters, OpenNotebookUseCase.PathUserAction },
        { @"C:\Loop\A", OpenNotebookUseCase.Rules.TooManyLinks, OpenNotebookUseCase.PathUserAction },
        { @"C:\Links\ToServer", OpenNotebookUseCase.Rules.Network, OpenNotebookUseCase.PathUserAction },
        { @"C:\Notebooks\Gone", OpenNotebookUseCase.Rules.Missing, OpenNotebookUseCase.MissingUserAction },
        { @"C:\Notebooks\Notebook C\Section.one", OpenNotebookUseCase.Rules.SectionFile, OpenNotebookUseCase.SectionFileUserAction },
        { @"C:\Packages\Notebook C.onepkg", OpenNotebookUseCase.Rules.Package, OpenNotebookUseCase.PackageUserAction },
        { @"C:\Notebooks\Notebook C\Open Notebook.ONETOC2", OpenNotebookUseCase.Rules.TocFile, OpenNotebookUseCase.TocFileUserAction },
        { @"C:\Notebooks\notes.txt", OpenNotebookUseCase.Rules.NotAFolder, OpenNotebookUseCase.MissingUserAction },
        { OneDriveFolder + @"\Notebooks\Notebook D", OpenNotebookUseCase.Rules.OneDrive, OpenNotebookUseCase.OneDriveUserAction },
        { "  ", OpenNotebookUseCase.Rules.Empty, OpenNotebookUseCase.MissingUserAction },
    };

    [Theory]
    [MemberData(nameof(StepOneRefusals))]
    public async Task RefusedAtStep1_IsInvalidArgument_WithItsUserAction_NothingReadOrSent_AndTheLogHasNoPath(string path, string rule, string userAction)
    {
        var gateway = Gateway();
        var links = new FakeLinks(new()
        {
            [@"C:\Loop\A"] = @"C:\Loop\B",
            [@"C:\Loop\B"] = @"C:\Loop\A",
            [@"C:\Links\ToServer"] = @"\\server\share\Notebook",
        });
        var folders = Folders()
            .Notebook(ClosedPath)
            .Notebook(OneDriveFolder + @"\Notebooks\Notebook D")
            .File(@"C:\Notebooks\notes.txt")
            .File(@"C:\Notebooks\Notebook C\Section.one")
            .File(@"C:\Packages\Notebook C.onepkg")
            .File(@"C:\Notebooks\Notebook C\Open Notebook.ONETOC2");
        var log = new List<OpenNotebookLogEntry>();
        var writeLock = AppStatePipelineTests.TestLock();

        var error = Error(await Tool(gateway, folders, links: links, writeLock: writeLock, log: log).OpenNotebook(path, Ct), "InvalidArgument");

        Assert.False(error.GetProperty("retryable").GetBoolean());
        Assert.False(error.TryGetProperty("outcomeUnknown", out _));
        Assert.Equal(userAction, error.GetProperty("userAction").GetString());
        Assert.Contains("nothing was opened", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Empty(gateway.Calls);
        Assert.Empty(gateway.OpenHierarchyCalls);
        var entry = Assert.Single(log);
        Assert.Equal((rule, "refused"), (entry.Rule, entry.Outcome));
        Assert.Equal(rule == OpenNotebookUseCase.Rules.Url ? "url" : "localPath", entry.PathKind);
        if (path.Trim().Length > 0)
        {
            Assert.DoesNotContain(path.Trim(), error.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
        }

        Assert.True(await AppStatePipelineTests.CanTakeAsync(writeLock));
    }

    [Fact]
    public async Task AFolderWithNoOnetoc2_IsInvalidArgument_WithTheWaitAndRetryUserAction_NoOpenHierarchy()
    {
        var gateway = Gateway();
        var log = new List<OpenNotebookLogEntry>();

        var error = Error(await Tool(gateway, Folders().Folder(ClosedPath), log: log).OpenNotebook(ClosedPath, Ct), "InvalidArgument");

        Assert.Equal(OpenNotebookUseCase.NoNotebookTocUserAction, error.GetProperty("userAction").GetString());
        Assert.Contains("wait", OpenNotebookUseCase.NoNotebookTocUserAction, StringComparison.Ordinal);
        Assert.Single(gateway.Calls); // step 3's read, under the lock
        Assert.Empty(gateway.OpenHierarchyCalls);
        Assert.Equal(OpenNotebookUseCase.Rules.NoNotebookToc, Assert.Single(log).Rule);
    }

    [Theory]
    [InlineData(@"C:\Notebooks\Q3.one")]
    [InlineData(@"C:\Notebooks\Archive.onepkg")]
    [InlineData(@"C:\Notebooks\Odd.onetoc2")]
    public async Task ANotebookFolder_NamedLikeAOneNoteFile_IsOpened_TheFileRulesNameFilesOnly(string folder)
    {
        var gateway = Gateway();

        Ok(await Tool(gateway, Folders().Notebook(folder)).OpenNotebook(folder, Ct));

        Assert.Equal([folder], gateway.OpenHierarchyCalls);
    }

    [Fact]
    public async Task ASectionGroupsFolder_InsideANotebook_IsInvalidArgument_EvenWithItsOwnOnetoc2_NoOpenHierarchy()
    {
        var gateway = Gateway();
        var folders = Folders().Notebook(ClosedPath).Notebook(ClosedPath + @"\Group");

        var error = Error(await Tool(gateway, folders).OpenNotebook(ClosedPath + @"\Group", Ct), "InvalidArgument");

        Assert.Equal(OpenNotebookUseCase.InsideANotebookUserAction, error.GetProperty("userAction").GetString());
        Assert.Empty(gateway.OpenHierarchyCalls);
    }

    [Fact]
    public async Task AOneDriveFolder_SeenOnlyUnderTheLock_IsInvalidArgument_NoOpenHierarchy()
    {
        var gateway = Gateway();
        var asked = 0;
        var oneDrive = new OneDriveFolders([new OneDriveAccount(@"C:\Notebooks")]);

        var error = Error(
            await Tool(gateway, Folders().Notebook(ClosedPath), oneDrive: () => ++asked == 1 ? OneDriveFolders.None : oneDrive).OpenNotebook(ClosedPath, Ct),
            "InvalidArgument");

        Assert.Equal(OpenNotebookUseCase.OneDriveUserAction, error.GetProperty("userAction").GetString());
        Assert.Equal(2, asked);
        Assert.Single(gateway.Calls);
        Assert.Empty(gateway.OpenHierarchyCalls);
    }

    [Fact]
    public async Task AFolderGoneBeforeTheLock_IsInvalidArgument_UnderTheLock()
    {
        var gateway = Gateway();
        var folders = Folders().Notebook(ClosedPath);
        folders.GoneAfter(ClosedPath, entries: 1);

        var error = Error(await Tool(gateway, folders).OpenNotebook(ClosedPath, Ct), "InvalidArgument");

        Assert.Equal(OpenNotebookUseCase.MissingUserAction, error.GetProperty("userAction").GetString());
        Assert.Single(gateway.Calls);
        Assert.Empty(gateway.OpenHierarchyCalls);
    }

    [Fact]
    public async Task AnAbandonedWriteOutstanding_IsComBusy_Retryable_NothingReadUnderTheLock()
    {
        var gateway = Gateway();
        gateway.AbandonedWrites = new TaskCompletionSource();
        var writeLock = AppStatePipelineTests.TestLock();

        var error = Error(await Tool(gateway, Folders().Notebook(ClosedPath), writeLock: writeLock).OpenNotebook(ClosedPath, Ct), "ComBusy");

        Assert.True(error.GetProperty("retryable").GetBoolean());
        Assert.False(error.TryGetProperty("outcomeUnknown", out _));
        Assert.Empty(gateway.Calls);
        Assert.Empty(gateway.OpenHierarchyCalls);
        Assert.True(await AppStatePipelineTests.CanTakeAsync(writeLock));
    }

    public static readonly TheoryData<ErrorCode, int?> AmbiguousOpenFailures = new()
    {
        { ErrorCode.Timeout, null },
        { ErrorCode.ComUnavailable, unchecked((int)0x800706BA) },
        { ErrorCode.Cancelled, null },
        { ErrorCode.Internal, unchecked((int)0x800706BE) },
    };

    [Theory]
    [MemberData(nameof(AmbiguousOpenFailures))]
    public async Task AnAmbiguousOpenFailure_IsOutcomeUnknown_NeverRetried(ErrorCode code, int? hresult)
    {
        var gateway = Gateway();
        gateway.OpenHierarchyFailure = new OneNoteException(code, "OpenHierarchy failed.", hresult);
        var log = new List<OpenNotebookLogEntry>();

        var error = Error(await Tool(gateway, Folders().Notebook(ClosedPath), log: log).OpenNotebook(ClosedPath, Ct), code.ToString());

        Assert.True(error.GetProperty("outcomeUnknown").GetBoolean());
        Assert.False(error.GetProperty("retryable").GetBoolean());
        Assert.Equal(ToolErrors.OutcomeUnknownUserAction, error.GetProperty("userAction").GetString());
        Assert.Single(gateway.OpenHierarchyCalls);
        var entry = Assert.Single(log);
        Assert.Equal((OpenNotebookUseCase.Rules.None, code.ToString()), (entry.Rule, entry.Outcome));
    }

    [Fact]
    public async Task ARefusedOpen_KeepsItsCode_NotOutcomeUnknown()
    {
        var gateway = Gateway();
        gateway.OpenHierarchyFailure = new OneNoteException(ErrorCode.ModalDialogOpen, "A dialog is open in OneNote.", unchecked((int)0x80042030), retryable: true);

        var error = Error(await Tool(gateway, Folders().Notebook(ClosedPath)).OpenNotebook(ClosedPath, Ct), "ModalDialogOpen");

        Assert.False(error.TryGetProperty("outcomeUnknown", out _));
        Assert.Single(gateway.OpenHierarchyCalls);
    }

    [Fact]
    public async Task ATimedOutOpen_KeepsTheLockUntilItSettles()
    {
        var gateway = Gateway();
        var writeLock = AppStatePipelineTests.TestLock();
        var abandoned = new TaskCompletionSource();
        gateway.OnOpenHierarchyCall = (_, _) =>
        {
            gateway.AbandonedWrites = abandoned;
            return Task.CompletedTask;
        };
        gateway.OpenHierarchyFailure = new OneNoteException(ErrorCode.Timeout, "OpenHierarchy did not return within 30 s; the connection to OneNote was reset.");

        var error = Error(await Tool(gateway, Folders().Notebook(ClosedPath), writeLock: writeLock).OpenNotebook(ClosedPath, Ct), "Timeout");

        Assert.True(error.GetProperty("outcomeUnknown").GetBoolean());
        Assert.False(await AppStatePipelineTests.CanTakeAsync(writeLock));
        abandoned.SetResult();
        var released = false;
        for (var i = 0; i < 50 && !released; i++)
        {
            await Task.Delay(100, Ct);
            released = await AppStatePipelineTests.CanTakeAsync(writeLock);
        }

        Assert.True(released);
    }

    [Fact]
    public async Task RealFolder_WithNoOnetoc2_IsRefused_AndItsFileListIsUnchanged()
    {
        // The plan's acceptance criterion, on the real file system and the real probe.
        using var temp = new TempDirectory();
        var folder = temp.Combine("Not A Notebook");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "notes.txt"), "x", Ct);
        var before = Listing(folder);
        var gateway = Gateway();

        var error = Error(await RealTool(gateway).OpenNotebook(folder, Ct), "InvalidArgument");

        Assert.Equal(OpenNotebookUseCase.NoNotebookTocUserAction, error.GetProperty("userAction").GetString());
        Assert.Equal(before, Listing(folder));
        Assert.Empty(gateway.OpenHierarchyCalls);
    }

    [Fact]
    public async Task RealFolder_WithAnOnetoc2_IsOpened_ButItsSubfolderWithOneToo_IsRefused()
    {
        using var temp = new TempDirectory();
        var notebook = temp.Combine("Notebook");
        var group = Path.Combine(notebook, "Group");
        Directory.CreateDirectory(group);
        await File.WriteAllTextAsync(Path.Combine(notebook, "Open Notebook.onetoc2"), string.Empty, Ct);
        await File.WriteAllTextAsync(Path.Combine(group, "Open Notebook.onetoc2"), string.Empty, Ct);
        File.SetAttributes(Path.Combine(notebook, "Open Notebook.onetoc2"), FileAttributes.Hidden);
        var gateway = Gateway();
        var canonical = FilePathGate.Canonical(notebook, out _)!;

        var groupError = Error(await RealTool(gateway).OpenNotebook(group, Ct), "InvalidArgument");
        var root = Ok(await RealTool(gateway).OpenNotebook(notebook + @"\", Ct));

        Assert.Equal(OpenNotebookUseCase.InsideANotebookUserAction, groupError.GetProperty("userAction").GetString());
        Assert.Equal([canonical], gateway.OpenHierarchyCalls);
        Assert.Equal(canonical, root.GetProperty("meta").GetProperty("path").GetString());
    }

    [Fact]
    public void UseCase_IsAppState()
    {
        var gateway = new FakeGateway();
        Assert.Equal(OperationClass.AppState, new OpenNotebookUseCase(gateway, new AppStatePipeline(gateway)).Class);
    }

    [Fact]
    public void Description_IsUserVisible_AndAnnotationsAreNotReadOnlyNotDestructiveIdempotent()
    {
        using var services = BuildServer();
        var open = Assert.Single(services.GetServices<McpServerTool>().Select(t => t.ProtocolTool), t => t.Name == OpenNotebookTool.Name);

        Assert.StartsWith("User-visible", open.Description, StringComparison.Ordinal);
        Assert.Contains("120 s deadline", open.Description, StringComparison.Ordinal);
        Assert.Contains("outcomeUnknown", open.Description, StringComparison.Ordinal);
        Assert.Contains("notebookListMayLag", open.Description, StringComparison.Ordinal);
        Assert.Contains("alreadyExisted", open.Description, StringComparison.Ordinal);
        Assert.False(open.Annotations!.ReadOnlyHint);
        Assert.False(open.Annotations.DestructiveHint);
        Assert.True(open.Annotations.IdempotentHint);
        Assert.False(open.Annotations.OpenWorldHint);
        Assert.Equal(["path"], open.InputSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void TheServer_WiresOpenNotebook()
    {
        using var services = BuildServer();
        Assert.NotNull(services.GetRequiredService<OpenNotebookUseCase>());
    }

    private static string Notebooks(params (string Name, string Id, string Path)[] notebooks) =>
        "<?xml version=\"1.0\"?>\n<one:Notebooks xmlns:one=\"" + Ns + "\">"
        + string.Concat(notebooks.Select(n => "<one:Notebook name=\"" + n.Name + "\" nickname=\"" + n.Name + "\" ID=\"" + n.Id + "\" path=\"" + n.Path + "\" lastModifiedTime=\"2026-11-22T10:00:00.000Z\" />"))
        + "</one:Notebooks>";

    private static FakeGateway Gateway(string? notebooksXml = null)
    {
        var gateway = new FakeGateway();
        gateway.Hierarchies[(string.Empty, HierarchyScope.Notebooks)] = notebooksXml ?? NotebooksXml;
        return gateway;
    }

    private static FakeFolders Folders() => new();

    private static OpenNotebookTool Tool(
        FakeGateway gateway,
        FakeFolders folders,
        FakeLinks? links = null,
        WriteLock? writeLock = null,
        Func<OneDriveFolders>? oneDrive = null,
        List<OpenNotebookLogEntry>? log = null)
    {
        var pipeline = new AppStatePipeline(gateway, writeLock: writeLock ?? AppStatePipelineTests.TestLock());
        var known = new OneDriveFolders([new OneDriveAccount(OneDriveFolder)]);
        return new OpenNotebookTool(
            new ToolRunner(NullLogger<ToolRunner>.Instance),
            new OpenNotebookUseCase(gateway, pipeline, oneDrive ?? (() => known), log is null ? null : log.Add, links ?? new FakeLinks([]), folders));
    }

    /// <summary>The server's own seams: real links and the real file system.</summary>
    private static OpenNotebookTool RealTool(FakeGateway gateway) =>
        new(new ToolRunner(NullLogger<ToolRunner>.Instance), new OpenNotebookUseCase(gateway, new AppStatePipeline(gateway, writeLock: AppStatePipelineTests.TestLock())));

    private static string[] Listing(string folder) =>
        [.. Directory.EnumerateFileSystemEntries(folder, "*", new EnumerationOptions { AttributesToSkip = 0, RecurseSubdirectories = true }).Order(StringComparer.Ordinal)];

    private static ServiceProvider BuildServer()
    {
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddOneNoteMcp();
        return collection.BuildServiceProvider();
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

    /// <summary>Scripted links: a path maps to its immediate target.</summary>
    private sealed class FakeLinks(Dictionary<string, string> links) : FilePathGate.ILinkReader
    {
        public string? ImmediateTarget(string path) => links.GetValueOrDefault(path);
    }

    /// <summary>A scripted file system: folders, files, and the folders that hold a <c>.onetoc2</c>. Paths compare ignoring case.</summary>
    private sealed class FakeFolders : INotebookFolderProbe
    {
        private readonly Dictionary<string, PathEntry> entries = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> tocs = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> goneAfter = new(StringComparer.OrdinalIgnoreCase);

        public FakeFolders Folder(string path)
        {
            entries[path] = PathEntry.Folder;
            return this;
        }

        public FakeFolders Notebook(string path)
        {
            tocs.Add(path);
            return Folder(path);
        }

        public FakeFolders File(string path)
        {
            entries[path] = PathEntry.File;
            return this;
        }

        /// <summary>The folder reads as missing once it has been asked about <paramref name="entries"/> times.</summary>
        public void GoneAfter(string path, int entries) => goneAfter[path] = entries;

        public PathEntry Entry(string path)
        {
            if (goneAfter.TryGetValue(path, out var left))
            {
                if (left <= 0)
                {
                    return PathEntry.Missing;
                }

                goneAfter[path] = left - 1;
            }

            return entries.GetValueOrDefault(path, PathEntry.Missing);
        }

        public bool HoldsNotebookToc(string folder) => tocs.Contains(folder);
    }
}
