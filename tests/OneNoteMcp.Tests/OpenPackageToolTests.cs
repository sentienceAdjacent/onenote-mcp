using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using OneNoteMcp.Core;
using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Core.Writes;
using OneNoteMcp.Server;
using OneNoteMcp.Server.Tools;
using OneNoteMcp.Xml;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>
/// Story 3.8: <c>open_package</c> on <see cref="FakeGateway"/>, one test per row of the plan's I/O matrix. A package is
/// unpacked with <c>OpenPackage</c> into a new folder under an export root that already holds the marker file
/// (<see cref="ExportRootGuard.MarkerFileName"/>), then opened with <c>OpenHierarchy</c>, and the pages are awaited.
/// The export root is a temporary folder; the package is a file in another temporary folder.
/// </summary>
public sealed class OpenPackageToolTests : IDisposable
{
    private const string Ns = "xmlns:one=\"" + OneNoteXml.Namespace2013 + "\"";

    private readonly TempDirectory temp = new();
    private readonly FakeClock clock = new();
    private readonly FakeGateway gateway = new();
    private readonly ConcurrentQueue<WriteLockHeld> holds = new();
    private readonly WriteLock writeLock;
    private readonly string package;

    public OpenPackageToolTests()
    {
        writeLock = new WriteLock($@"Local\OneNoteMcp.Test.{Guid.NewGuid():N}", holds.Enqueue);
        File.WriteAllText(temp.Combine("settings.json"), JsonSerializer.Serialize(new { schemaVersion = 1, exportRoots = new[] { Root } }));
        Directory.CreateDirectory(temp.Combine("downloads"));
        package = temp.Combine(@"downloads\Trip notes.onepkg");
        File.WriteAllBytes(package, [0x4D, 0x53, 0x43, 0x46]);
        Pages(listed: true);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The only export root, absent until a call creates it.</summary>
    private string Root => temp.Combine("exports");

    private string CanonicalRoot => FilePathGate.Canonical(Root, out _)!;

    private SettingsStore Store => new(temp.Combine("settings.json"), temp.Combine("journal"));

    public void Dispose() => temp.Dispose();

    // --- Happy paths -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task APackage_IsUnpackedIntoTheNamedFolder_BesideTheMarker_ThenOpened_AndTheResultNamesBoth()
    {
        var root = Ok(await Tool().OpenPackage(package, "Opened trip", Ct));

        var folder = Path.Combine(CanonicalRoot, "Opened trip");
        var meta = root.GetProperty("meta");
        Assert.Equal(folder, meta.GetProperty("outputPath").GetString());
        Assert.Equal(gateway.OpenedId, meta.GetProperty("objectId").GetString());
        Assert.Empty(root.GetProperty("warnings").EnumerateArray());
        Assert.True(File.Exists(Path.Combine(folder, ExportRootGuard.MarkerFileName)));
        Assert.Equal((FilePathGate.Canonical(package, out _)!, folder), Assert.Single(gateway.OpenPackageCalls));
        Assert.Equal(folder, Assert.Single(gateway.OpenHierarchyCalls));
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task WithNoDestination_TheFolderIsThePackagesName_AndATimestamp_UnderTheFirstRoot_CreatedIfMissing()
    {
        Assert.False(Directory.Exists(Root));

        var root = Ok(await Tool().OpenPackage(package, null, Ct));

        var folder = root.GetProperty("meta").GetProperty("outputPath").GetString()!;
        Assert.Equal(CanonicalRoot, Path.GetDirectoryName(folder));
        Assert.Matches(new Regex(@"^Trip notes \d{8}-\d{6}$"), Path.GetFileName(folder));
        Assert.True(File.Exists(Path.Combine(folder, ExportRootGuard.MarkerFileName)));
    }

    [Fact]
    public async Task ACopyThatListsNoPageInTheWait_IsOk_WithPagesNotListed()
    {
        Pages(listed: false);

        var root = Ok(await Tool().OpenPackage(package, "Opened trip", Ct));

        var warning = Assert.Single(root.GetProperty("warnings").EnumerateArray());
        Assert.Equal(ToolWarnings.PagesNotListed, warning.GetProperty("code").GetString());
        Assert.Contains(gateway.OpenedId, warning.GetProperty("message").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheOpenedCopy_DoesNotTripTheExportRootGuard_BecauseOfItsMarker()
    {
        var folder = Ok(await Tool().OpenPackage(package, "Opened trip", Ct)).GetProperty("meta").GetProperty("outputPath").GetString()!;
        var notebooks = $"<?xml version=\"1.0\"?>\n<one:Notebooks {Ns}><one:Notebook name=\"Opened trip\" ID=\"{gateway.OpenedId}\" path=\"{folder}\" /></one:Notebooks>";

        Assert.Null(ExportRootGuard.Problem([Root], notebooks, File.Exists));
    }

    // --- Refusals before COM -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(@"..\escaped")]
    [InlineData(@"Z:\elsewhere\copy")]
    public async Task ADestinationOutsideEveryRoot_IsExportPathNotAllowed_NothingDone(string destination)
    {
        Error(await Tool().OpenPackage(package, destination, Ct), "ExportPathNotAllowed");

        await AssertNothingDoneAsync();
    }

    [Fact]
    public async Task AnExistingDestination_IsFileExists_AndLeftAlone()
    {
        Directory.CreateDirectory(Path.Combine(Root, "Opened trip"));
        File.WriteAllText(Path.Combine(Root, "Opened trip", "keep.txt"), "mine");

        Error(await Tool().OpenPackage(package, "Opened trip", Ct), "FileExists");

        Assert.Equal(["keep.txt"], Directory.GetFiles(Path.Combine(Root, "Opened trip")).Select(Path.GetFileName));
        Assert.Empty(gateway.OpenPackageCalls);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("not a package")]
    [InlineData("relative")]
    [InlineData("network")]
    [InlineData("empty")]
    public async Task APackageThatIsntALocalOnepkgFile_IsInvalidArgument_NothingDone(string what)
    {
        var bad = what switch
        {
            "missing" => temp.Combine(@"downloads\Gone.onepkg"),
            "not a package" => WriteFile(@"downloads\Trip notes.pdf"),
            "relative" => @"downloads\Trip notes.onepkg",
            "network" => @"\\server\share\Trip notes.onepkg",
            _ => string.Empty,
        };

        Error(await Tool().OpenPackage(bad, "Opened trip", Ct), "InvalidArgument");

        await AssertNothingDoneAsync();
    }

    // --- Step 7 failures ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task OpenPackageFailing_WithNothingWritten_RemovesTheEmptyFolder_AndSaysSo()
    {
        gateway.OpenPackageFailure = new OneNoteException(ErrorCode.Internal, "OpenPackage failed: hrUnknown (0x80042001).", unchecked((int)0x80042001));

        var error = Error(await Tool().OpenPackage(package, "Opened trip", Ct), "Internal");

        Assert.False(Directory.Exists(Path.Combine(Root, "Opened trip")));
        Assert.Contains("removed", error.GetProperty("message").GetString()!, StringComparison.Ordinal);
        Assert.Empty(gateway.OpenHierarchyCalls);
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task OpenPackageTimingOut_KeepsTheFolder_AndNamesIt_OutcomeUnknown()
    {
        gateway.OpenPackageFailure = new OneNoteException(ErrorCode.Timeout, "OpenPackage timed out.");

        var error = Error(await Tool().OpenPackage(package, "Opened trip", Ct), "Timeout");

        var folder = Path.Combine(CanonicalRoot, "Opened trip");
        Assert.True(error.GetProperty("outcomeUnknown").GetBoolean());
        Assert.True(Directory.Exists(folder));
        Assert.Contains(folder, error.GetProperty("message").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OpenHierarchyFailing_AfterTheUnpack_KeepsTheFolder_AndSaysTheUnpackWasDone()
    {
        gateway.OpenHierarchyFailure = new OneNoteException(ErrorCode.ObjectNotFound, "OpenHierarchy failed: hrObjectDoesNotExist (0x80042014).", unchecked((int)0x80042014));

        var error = Error(await Tool().OpenPackage(package, "Opened trip", Ct), "ObjectNotFound");

        var folder = Path.Combine(CanonicalRoot, "Opened trip");
        Assert.True(File.Exists(Path.Combine(folder, "Open Notebook.onetoc2")));
        var message = error.GetProperty("message").GetString()!;
        Assert.Contains("unpacked", message, StringComparison.Ordinal);
        Assert.Contains(folder, message, StringComparison.OrdinalIgnoreCase);
    }

    // --- After the quick review (2026-11-18) --------------------------------------------------------------------------

    [Fact]
    public async Task ACancelDuringThePageWait_StillReturnsTheOpenedCopy_WithPagesNotListed()
    {
        Pages(listed: false);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        gateway.OnGetHierarchy = (id, scope) =>
        {
            if (id == gateway.OpenedId && scope == HierarchyScope.Pages)
            {
                cts.Cancel();
            }
        };

        var root = Ok(await Tool().OpenPackage(package, "Opened trip", cts.Token));

        Assert.Equal(gateway.OpenedId, root.GetProperty("meta").GetProperty("objectId").GetString());
        Assert.Equal(ToolWarnings.PagesNotListed, Assert.Single(root.GetProperty("warnings").EnumerateArray()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task AComUnavailableFromOpenPackage_KeepsTheFolder_AndIsOutcomeUnknown()
    {
        gateway.OpenPackageFailure = new OneNoteException(ErrorCode.ComUnavailable, "OpenPackage failed: the RPC server is unavailable.");

        var error = Error(await Tool().OpenPackage(package, "Opened trip", Ct), "ComUnavailable");

        Assert.True(error.GetProperty("outcomeUnknown").GetBoolean());
        Assert.True(Directory.Exists(Path.Combine(Root, "Opened trip")));
        Assert.DoesNotContain("removed", error.GetProperty("message").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOpenHierarchyFailure_KeepsItsOwnUserAction()
    {
        gateway.OpenHierarchyFailure = new OneNoteException(ErrorCode.ModalDialogOpen, "OpenHierarchy failed: a dialog is open.", userAction: "Close the dialog in OneNote, then retry.");

        var error = Error(await Tool().OpenPackage(package, "Opened trip", Ct), "ModalDialogOpen");

        Assert.Equal("Close the dialog in OneNote, then retry.", error.GetProperty("userAction").GetString());
    }

    [Fact]
    public async Task AFileAtTheDestination_IsFileExists_AndLeftAlone()
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(Path.Combine(Root, "Opened trip"), "mine");

        Error(await Tool().OpenPackage(package, "Opened trip", Ct), "FileExists");

        Assert.Equal("mine", File.ReadAllText(Path.Combine(Root, "Opened trip")));
        Assert.Empty(gateway.OpenPackageCalls);
    }

    [Fact]
    public async Task OpenPackageReportingAnotherPath_IsInternal_TheFolderKept_NothingOpened()
    {
        gateway.OnOpenPackage = (_, destination) =>
        {
            File.WriteAllText(Path.Combine(destination, "Open Notebook.onetoc2"), "toc");
            return destination + "-elsewhere";
        };

        Error(await Tool().OpenPackage(package, "Opened trip", Ct), "Internal");

        Assert.True(Directory.Exists(Path.Combine(Root, "Opened trip")));
        Assert.Empty(gateway.OpenHierarchyCalls);
    }

    [Fact]
    public async Task WithTheGuard_TheOpenedCopyGivesNoWarningOnReads_AndDoesntBlockTheNextWrite()
    {
        var folder = Ok(await Tool().OpenPackage(package, "Opened trip", Ct)).GetProperty("meta").GetProperty("outputPath").GetString()!;
        gateway.Hierarchies[(string.Empty, HierarchyScope.Notebooks)] =
            $"<?xml version=\"1.0\"?>\n<one:Notebooks {Ns}><one:Notebook name=\"Opened trip\" ID=\"{gateway.OpenedId}\" path=\"{folder}\" /></one:Notebooks>";
        var guard = new ExportRootGuard(gateway, clock);

        var protection = await new ReadAccess(gateway, Store.Read, null, clock, guard).ProtectionAsync(Ct);
        Assert.Empty(protection.Warnings);

        var guarded = new OpenPackageTool(
            new ToolRunner(NullLogger<ToolRunner>.Instance),
            new OpenPackageUseCase(gateway, Store.Read, new WritePipeline(writeLock, gateway, null, Store.Read, TimeProvider.System, guard: guard), clock, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(80)));
        Ok(await guarded.OpenPackage(package, "Second copy", Ct));
    }

    [Fact]
    public async Task ADestinationInsideAnOpenNotebooksFolder_IsExportPathNotAllowed_NothingDone()
    {
        gateway.Hierarchies[(string.Empty, HierarchyScope.Notebooks)] =
            $"<?xml version=\"1.0\"?>\n<one:Notebooks {Ns}><one:Notebook name=\"Earlier copy\" ID=\"{{E}}\" path=\"{Path.Combine(Root, "Earlier copy")}\" /></one:Notebooks>";

        Error(await Tool().OpenPackage(package, @"Earlier copy\inner", Ct), "ExportPathNotAllowed");

        Assert.Empty(gateway.OpenPackageCalls);
    }

    // --- Helpers -----------------------------------------------------------------------------------------------------------

    /// <summary>The copy's pages-scope read: one page when <paramref name="listed"/>, else none.</summary>
    private void Pages(bool listed) =>
        gateway.Hierarchies[(gateway.OpenedId, HierarchyScope.Pages)] =
            $"<?xml version=\"1.0\"?>\n<one:Notebook {Ns} name=\"Copy\" ID=\"{gateway.OpenedId}\" path=\"x\"><one:Section name=\"S\" ID=\"{{S}}\" path=\"x\\S.one\">"
            + (listed ? "<one:Page ID=\"{P}\" name=\"Page\" />" : string.Empty)
            + "</one:Section></one:Notebook>";

    private string WriteFile(string relative)
    {
        File.WriteAllText(temp.Combine(relative), "x");
        return temp.Combine(relative);
    }

    private OpenPackageUseCase UseCase() => new(
        gateway,
        Store.Read,
        new WritePipeline(writeLock, gateway, null, Store.Read, TimeProvider.System),
        clock,
        pollInterval: TimeSpan.FromMilliseconds(10),
        listWait: TimeSpan.FromMilliseconds(80));

    private OpenPackageTool Tool() => new(new ToolRunner(NullLogger<ToolRunner>.Instance), UseCase());

    private async Task AssertNothingDoneAsync()
    {
        Assert.Empty(gateway.OpenPackageCalls);
        Assert.Empty(gateway.OpenHierarchyCalls);
        Assert.False(Directory.Exists(Root) && Directory.EnumerateFileSystemEntries(Root).Any());
        await AssertLockFreeAsync();
    }

    private async Task AssertLockFreeAsync()
    {
        using var probe = await writeLock.AcquireAsync("free?", TimeSpan.FromMilliseconds(2000), Ct);
    }

    private static JsonElement Ok(CallToolResult result)
    {
        Assert.NotNull(result.StructuredContent);
        var root = result.StructuredContent.Value;
        Assert.False(result.IsError, root.GetRawText());
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
