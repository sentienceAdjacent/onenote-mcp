using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using OneNoteMcp.Core;
using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Core.Writes;
using OneNoteMcp.Server;
using OneNoteMcp.Server.Tools;
using OneNoteMcp.Xml;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>
/// Story 3.3: <c>publish</c>, a page as PDF, through the AD-7 write pipeline as an <c>ExternalWrite</c>, one test per row
/// of the plan's I/O matrix, against <see cref="FakeGateway"/>: notebook <c>W</c> with a plain section, a password-protected
/// section, a locked one and a section group, a page in each section, and a temporary <c>settings.json</c> whose only export
/// root is a folder under a temporary directory (absent until the first export creates it). Each test has a write lock of
/// its own. Refusals leave no <c>Publish</c> call and no file.
/// </summary>
public sealed class PublishToolTests : IDisposable
{
    private const string Ns = "xmlns:one=\"" + OneNoteXml.Namespace2013 + "\"";
    private const string NotebookId = "{NBW00000-0000-4000-8000-000000000001}{1}{B0}";
    private const string NotebookPath = @"C:\notebooks\W";
    private const string SectionId = "{SW000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string VaultId = "{SP000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string LockedId = "{SL000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string GroupId = "{GF000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string PageId = "{5E900000-0000-4000-8000-000000000001}{1}{E1900000000000000000000000000000000000000001}";
    private const string VaultPageId = "{5E900000-0000-4000-8000-000000000001}{1}{E1900000000000000000000000000000000000000002}";
    private const string LockedPageId = "{5E900000-0000-4000-8000-000000000001}{1}{E1900000000000000000000000000000000000000003}";
    private const string Date = "2026-11-16T00:00:00.000Z";

    private readonly TempDirectory temp = new();
    private readonly FakeClock clock = new();
    private readonly FakeGateway gateway = new();
    private readonly ConcurrentQueue<WriteLockHeld> holds = new();
    private readonly WriteLock writeLock;

    public PublishToolTests()
    {
        writeLock = new WriteLock($@"Local\OneNoteMcp.Test.{Guid.NewGuid():N}", holds.Enqueue);
        WriteSettings();

        gateway.Parents[NotebookId] = string.Empty;
        foreach (var id in new[] { SectionId, VaultId, LockedId, GroupId })
        {
            gateway.Parents[id] = NotebookId;
        }

        gateway.Parents[PageId] = SectionId;
        gateway.Parents[VaultPageId] = VaultId;
        gateway.Parents[LockedPageId] = LockedId;

        Self(NotebookId, $"<one:Notebook {Ns} name=\"W\" nickname=\"W\" ID=\"{NotebookId}\" path=\"{NotebookPath}\" lastModifiedTime=\"{Date}\" />");
        Section(SectionId, "Open", string.Empty);
        Section(VaultId, "Vault", "encrypted=\"true\"");
        Section(LockedId, "Locked vault", "encrypted=\"true\" locked=\"true\"");
        Self(GroupId, $"<one:SectionGroup {Ns} name=\"Group\" ID=\"{GroupId}\" path=\"{NotebookPath}\\Group\" lastModifiedTime=\"{Date}\" />");
        foreach (var id in new[] { PageId, VaultPageId, LockedPageId })
        {
            Self(id, $"<one:Page {Ns} ID=\"{id}\" name=\"Secret title\" lastModifiedTime=\"{Date}\" />");
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The only export root, absent until an export creates it.</summary>
    private string Root => temp.Combine("exports");

    private string CanonicalRoot => FilePathGate.Canonical(Root, out _)!;

    private SettingsStore Store => new(temp.Combine("settings.json"), temp.Combine("journal"));

    public void Dispose() => temp.Dispose();

    // --- Happy path -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task APage_IsPublishedAsPdf_UnderTheFirstRoot_CreatedIfMissing()
    {
        Assert.False(Directory.Exists(Root));

        var root = Ok(await Tool().Publish(PageId, "x.pdf", PublishFormatArgument.Pdf, Ct));

        var expected = Path.Combine(CanonicalRoot, "x.pdf");
        var meta = root.GetProperty("meta");
        Assert.Equal(PageId, meta.GetProperty("objectId").GetString());
        Assert.Equal(expected, meta.GetProperty("outputPath").GetString());
        Assert.Empty(root.GetProperty("warnings").EnumerateArray());
        Assert.Equal((PageId, expected, ExportFormat.Pdf), Assert.Single(gateway.PublishCalls));
        Assert.True(File.Exists(expected));
        Assert.True(new FileInfo(expected).Length > 0);
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task ARelativePathWithAFolder_IsResolvedUnderTheFirstRoot_AndItsFolderCreated()
    {
        var root = Ok(await Tool().Publish(PageId, @"sub\deeper\x.pdf", PublishFormatArgument.Pdf, Ct));

        var expected = Path.Combine(CanonicalRoot, "sub", "deeper", "x.pdf");
        Assert.Equal(expected, root.GetProperty("meta").GetProperty("outputPath").GetString());
        Assert.True(File.Exists(expected));
    }

    [Fact]
    public async Task AnAbsolutePathUnderARoot_IsAccepted_AndAnUppercaseExtensionToo()
    {
        WriteSettings(extraRoot: temp.Combine("second"));
        var target = Path.Combine(temp.Combine("second"), "Y.PDF");

        var root = Ok(await Tool().Publish(PageId, target, PublishFormatArgument.Pdf, Ct));

        Assert.Equal(FilePathGate.Canonical(target, out _), root.GetProperty("meta").GetProperty("outputPath").GetString());
        Assert.True(File.Exists(target));
    }

    // --- Outside every root ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(@"..\x.pdf")]
    [InlineData(@"sub\..\..\x.pdf")]
    [InlineData("<elsewhere>")]
    [InlineData("<sibling>")]
    [InlineData("<another drive>")]
    public async Task APathOutsideEveryRoot_IsExportPathNotAllowed_AndNothingIsWritten(string path)
    {
        path = path switch
        {
            "<elsewhere>" => temp.Combine(@"elsewhere\x.pdf"),
            "<sibling>" => temp.Combine(@"exports-evil\x.pdf"), // shares the root's prefix, not its folder
            "<another drive>" => UnusedDriveLetter() + @":\x.pdf",
            _ => path,
        };

        Error(await Tool().Publish(PageId, path, PublishFormatArgument.Pdf, Ct), "ExportPathNotAllowed");

        await AssertRefusedAsync();
    }

    [Fact]
    public async Task AJunctionInsideTheRootThatLeadsOut_IsExportPathNotAllowed()
    {
        Directory.CreateDirectory(Root);
        var outside = Directory.CreateDirectory(temp.Combine("outside")).FullName;
        var link = Path.Combine(Root, "link");
        Junction(link, outside);
        try
        {
            Error(await Tool().Publish(PageId, @"link\x.pdf", PublishFormatArgument.Pdf, Ct), "ExportPathNotAllowed");

            Assert.Empty(gateway.PublishCalls);
            Assert.Empty(Directory.GetFiles(outside));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public async Task ALinkAtThePathWhoseTargetIsntAPdf_IsExportPathNotAllowed()
    {
        // A junction named x.pdf, inside the root, leading to a folder inside the root too: only the name is wrong.
        var real = Directory.CreateDirectory(Path.Combine(Root, "real")).FullName;
        var link = Path.Combine(Root, "x.pdf");
        Junction(link, real);
        try
        {
            var error = Error(await Tool().Publish(PageId, "x.pdf", PublishFormatArgument.Pdf, Ct), "ExportPathNotAllowed");

            Assert.Contains("isn't a .pdf", error.GetProperty("message").GetString(), StringComparison.Ordinal);
            Assert.Empty(gateway.PublishCalls);
            Assert.Empty(Directory.GetFileSystemEntries(real));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public void ARelativeName_UnderAFirstRootThatIsntALocalFolder_IsExportPathNotAllowed_NamingNoRoot()
    {
        var roots = new ExportRoots([@"\\server\share\exports", temp.Combine("second")]);

        var error = Assert.Throws<OneNoteException>(() => ExportTarget.Resolve("x.pdf", roots));

        Assert.Equal(ErrorCode.ExportPathNotAllowed, error.Code);
        Assert.Contains("first export root isn't a usable local folder", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("server", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRootItself_IsNeverATarget()
    {
        var roots = new ExportRoots([temp.Combine("r.pdf")]);

        var error = Assert.Throws<OneNoteException>(() => ExportTarget.Resolve(temp.Combine("r.pdf"), roots));

        Assert.Equal(ErrorCode.ExportPathNotAllowed, error.Code);
    }

    [Fact]
    public void NoExportRoots_RefusesEveryPath_RelativeOrAbsolute()
    {
        var roots = new ExportRoots([]);

        Assert.Equal(ErrorCode.ExportPathNotAllowed, Assert.Throws<OneNoteException>(() => ExportTarget.Resolve("x.pdf", roots)).Code);
        Assert.Equal(ErrorCode.ExportPathNotAllowed, Assert.Throws<OneNoteException>(() => ExportTarget.Resolve(temp.Combine("x.pdf"), roots)).Code);
    }

    // --- Bad paths ------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"\\server\share\x.pdf")]
    [InlineData(@"\\?\C:\x.pdf")]
    [InlineData(@"\\.\C:\x.pdf")]
    [InlineData("C:x.pdf")]
    [InlineData(@"\x.pdf")]
    [InlineData("a<b.pdf")]
    [InlineData("a|b.pdf")]
    [InlineData("a*.pdf")]
    [InlineData("a\tb.pdf")]
    [InlineData(@"x:y.pdf")]
    [InlineData(@"sub\x:y.pdf")]
    [InlineData("<stream>")]
    [InlineData("x.docx")]
    [InlineData("x.pdf.txt")]
    [InlineData("x")]
    [InlineData("x.pdf ")]
    [InlineData("x.pdf.")]
    [InlineData("https://example.com/x.pdf")]
    public async Task ABadPath_IsInvalidArgument_BeforeCom(string path)
    {
        if (path == "<stream>")
        {
            path = Path.Combine(Root, "a.txt:b.pdf");
        }

        Error(await Tool().Publish(PageId, path, PublishFormatArgument.Pdf, Ct), "InvalidArgument");

        await AssertRefusedAsync();
    }

    // --- The file exists ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnExistingFile_IsFileExists_AndLeftUntouched()
    {
        Directory.CreateDirectory(Root);
        var existing = Path.Combine(Root, "x.pdf");
        File.WriteAllText(existing, "mine");

        var error = Error(await Tool().Publish(PageId, "x.pdf", PublishFormatArgument.Pdf, Ct), "FileExists");

        Assert.Contains(Path.Combine(CanonicalRoot, "x.pdf"), error.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(gateway.PublishCalls);
        Assert.Equal("mine", File.ReadAllText(existing));
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task AnExistingFolderOfThatName_IsFileExists()
    {
        Directory.CreateDirectory(Path.Combine(Root, "x.pdf"));

        Error(await Tool().Publish(PageId, "x.pdf", PublishFormatArgument.Pdf, Ct), "FileExists");

        Assert.Empty(gateway.PublishCalls);
    }

    [Fact]
    public async Task AFileThatAppearsUnderTheLock_IsFileExists_AndLeftUntouched()
    {
        // Step 1 reads the page's section once (the walk); step 3 reads it again under the lock. The file appears between.
        var target = Path.Combine(Root, "x.pdf");
        var sectionReads = 0;
        var hooked = new HookedGateway(gateway)
        {
            BeforeHierarchy = (id, scope) =>
            {
                if (id == SectionId && scope == HierarchyScope.Self && ++sectionReads == 2)
                {
                    Directory.CreateDirectory(Root);
                    File.WriteAllText(target, "raced");
                }
            },
        };

        Error(await Tool(hooked).Publish(PageId, "x.pdf", PublishFormatArgument.Pdf, Ct), "FileExists");

        Assert.Equal(2, sectionReads);
        Assert.Empty(gateway.PublishCalls);
        Assert.Equal("raced", File.ReadAllText(target));
        await AssertLockFreeAsync();
    }

    // --- Protected sections ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(VaultPageId, VaultId)]
    [InlineData(LockedPageId, LockedId)]
    public async Task AProtectedPage_WithoutAReadGrant_IsReadNotGranted_EvenWhenLocked(string pageId, string sectionId)
    {
        var result = await Tool().Publish(pageId, "x.pdf", PublishFormatArgument.Pdf, Ct);

        var error = Error(result, "ReadNotGranted");
        Assert.Contains(sectionId, error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Vault", result.StructuredContent!.Value.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("Secret", result.StructuredContent.Value.GetRawText(), StringComparison.Ordinal);
        await AssertRefusedAsync();
    }

    [Fact]
    public async Task APageMovedIntoAProtectedSectionBeforeTheLock_IsReadNotGranted()
    {
        // Step 1 walks the page to its plain section; right after that read, the page moves to the Vault. Step 3 walks
        // it again under the lock and finds the Vault, which has no read grant.
        var hooked = new HookedGateway(gateway)
        {
            BeforeHierarchy = (id, scope) =>
            {
                if (id == SectionId && scope == HierarchyScope.Self)
                {
                    gateway.Parents[PageId] = VaultId;
                }
            },
        };

        var error = Error(await Tool(hooked).Publish(PageId, "x.pdf", PublishFormatArgument.Pdf, Ct), "ReadNotGranted");

        Assert.Contains(VaultId, error.GetProperty("message").GetString(), StringComparison.Ordinal);
        await AssertRefusedAsync();
    }

    [Fact]
    public async Task ASectionProtectedBeforeTheLock_IsReadNotGranted()
    {
        // Step 1 reads the plain section; by step 3's read (the second) it is password-protected, with no read grant.
        var sectionReads = 0;
        var hooked = new HookedGateway(gateway)
        {
            BeforeHierarchy = (id, scope) =>
            {
                if (id == SectionId && scope == HierarchyScope.Self && ++sectionReads == 2)
                {
                    Section(SectionId, "Open", "encrypted=\"true\"");
                }
            },
        };

        var error = Error(await Tool(hooked).Publish(PageId, "x.pdf", PublishFormatArgument.Pdf, Ct), "ReadNotGranted");

        Assert.Equal(2, sectionReads);
        Assert.Contains(SectionId, error.GetProperty("message").GetString(), StringComparison.Ordinal);
        await AssertRefusedAsync();
    }

    [Fact]
    public async Task AFolderThatCantBeCreated_IsInternal_NamingNoPath()
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(Path.Combine(Root, "sub"), "a file where the folder should be");

        var error = Error(await Tool().Publish(PageId, @"sub\x.pdf", PublishFormatArgument.Pdf, Ct), "Internal");

        var message = error.GetProperty("message").GetString()!;
        Assert.DoesNotContain(temp.Path, message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sub", message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(gateway.PublishCalls);
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task AProtectedLockedPage_WithAReadGrant_IsSectionLocked_BeforeCom()
    {
        GrantRead(LockedId, "Locked vault");

        Error(await Tool().Publish(LockedPageId, "x.pdf", PublishFormatArgument.Pdf, Ct), "SectionLocked");

        await AssertRefusedAsync();
    }

    [Fact]
    public async Task AProtectedUnlockedPage_WithAReadGrant_IsPublished_WithExportNotEncrypted()
    {
        GrantRead(VaultId, "Vault");

        var root = Ok(await Tool().Publish(VaultPageId, "x.pdf", PublishFormatArgument.Pdf, Ct));

        var warning = Assert.Single(root.GetProperty("warnings").EnumerateArray());
        Assert.Equal("exportNotEncrypted", warning.GetProperty("code").GetString());
        Assert.Contains(VaultId, warning.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Secret", root.GetRawText(), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(CanonicalRoot, "x.pdf")));
    }

    // --- Not a page, not PDF, not an ID ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(SectionId)]
    [InlineData(GroupId)]
    [InlineData(NotebookId)]
    public async Task AnythingButAPage_IsNotSupported(string objectId)
    {
        var error = Error(await Tool().Publish(objectId, "x.pdf", PublishFormatArgument.Pdf, Ct), "NotSupported");

        Assert.Contains("pages only in this release", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        await AssertRefusedAsync();
    }

    [Fact]
    public async Task AFormatButPdf_IsNotSupported_BeforeCom()
    {
        var useCase = UseCase();
        var result = await Runner().RunAsync(
            PublishTool.Name,
            useCase,
            (context, ct) => useCase.ExecuteAsync(context, PageId, "x.one", ExportFormat.OneNote, ct),
            Ct);

        Error(result, "NotSupported");
        await AssertRefusedAsync();
    }

    [Fact]
    public async Task AnEmptyObjectId_IsInvalidArgument()
    {
        Error(await Tool().Publish(string.Empty, "x.pdf", PublishFormatArgument.Pdf, Ct), "InvalidArgument");

        await AssertRefusedAsync();
    }

    [Fact]
    public async Task AnInvalidSettingsFile_IsSettingsInvalid_AndNothingIsWritten()
    {
        File.WriteAllText(Store.Path, "{ not json");

        Error(await Tool().Publish(PageId, "x.pdf", PublishFormatArgument.Pdf, Ct), "SettingsInvalid");

        await AssertRefusedAsync();
    }

    // --- Step 7: the landing wait and COM failures ----------------------------------------------------------------------

    [Fact]
    public async Task AFileThatNeverLands_IsTimeout_OutcomeUnknown_NamingThePath()
    {
        WriteSettings(publishSeconds: 1);
        gateway.PublishedBytes = null;

        var error = Error(await Tool().Publish(PageId, "x.pdf", PublishFormatArgument.Pdf, Ct), "Timeout");

        Assert.True(error.GetProperty("outcomeUnknown").GetBoolean());
        Assert.False(error.GetProperty("retryable").GetBoolean());
        var message = error.GetProperty("message").GetString()!;
        Assert.Contains("never appeared", message, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(CanonicalRoot, "x.pdf"), message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(gateway.PublishCalls);
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task AFileStillOpenForWriting_IsTimeout_OutcomeUnknown_NamingThePath()
    {
        WriteSettings(publishSeconds: 1);
        FileStream? held = null;
        gateway.OnPublish = async (_, target, ct) =>
        {
            held = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite);
            await held.WriteAsync(new byte[] { 0x25, 0x50, 0x44, 0x46 }, ct);
            await held.FlushAsync(ct);
        };

        try
        {
            var error = Error(await Tool().Publish(PageId, "x.pdf", PublishFormatArgument.Pdf, Ct), "Timeout");

            Assert.True(error.GetProperty("outcomeUnknown").GetBoolean());
            var message = error.GetProperty("message").GetString()!;
            Assert.Contains("still open", message, StringComparison.Ordinal);
            Assert.Contains(Path.Combine(CanonicalRoot, "x.pdf"), message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            held?.Dispose();
        }
    }

    [Fact]
    public async Task APublishThatFails_KeepsItsMappedCode_AndNamesThePath()
    {
        gateway.PublishFailure = new OneNoteException(ErrorCode.FileExists, "Publish failed: hrFileAlreadyExists (0x8004201A).", unchecked((int)0x8004201A));

        var error = Error(await Tool().Publish(PageId, "x.pdf", PublishFormatArgument.Pdf, Ct), "FileExists");

        Assert.Equal("0x8004201A", error.GetProperty("hresult").GetString());
        Assert.False(error.TryGetProperty("outcomeUnknown", out _));
        Assert.Contains(Path.Combine(CanonicalRoot, "x.pdf"), error.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task APublishThatTimesOutInCom_IsOutcomeUnknown_NamingThePath()
    {
        gateway.PublishFailure = new OneNoteException(ErrorCode.Timeout, "Publish timed out.");

        var error = Error(await Tool().Publish(PageId, "x.pdf", PublishFormatArgument.Pdf, Ct), "Timeout");

        Assert.True(error.GetProperty("outcomeUnknown").GetBoolean());
        Assert.Contains(Path.Combine(CanonicalRoot, "x.pdf"), error.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    // --- Helpers --------------------------------------------------------------------------------------------------------

    private void WriteSettings(int? publishSeconds = null, string? extraRoot = null)
    {
        var settings = new Dictionary<string, object>
        {
            ["schemaVersion"] = 1,
            ["exportRoots"] = extraRoot is null ? new[] { Root } : new[] { Root, extraRoot },
        };
        if (publishSeconds is { } seconds)
        {
            settings["timeouts"] = new Dictionary<string, object> { ["publishSeconds"] = seconds };
        }

        File.WriteAllText(temp.Combine("settings.json"), JsonSerializer.Serialize(settings));
    }

    private void Self(string id, string xml) => gateway.Hierarchies[(id, HierarchyScope.Self)] = "<?xml version=\"1.0\"?>\n" + xml;

    private void Section(string id, string name, string extra) =>
        Self(id, $"<one:Section {Ns} name=\"{name}\" ID=\"{id}\" path=\"{NotebookPath}\\{name}.one\" lastModifiedTime=\"{Date}\" {extra} />");

    private void GrantRead(string sectionId, string name) =>
        new SettingsWriter(Store, clock).SaveReadGrant(new ProtectedSectionGrant(
            GrantKeys.SectionKey($@"{NotebookPath}\{name}.one"),
            name,
            GrantKeys.NotebookKey(NotebookPath),
            sectionId,
            clock.GetUtcNow(),
            GrantedVia.Cli));

    private PublishUseCase UseCase(IOneNoteGateway? over = null)
    {
        var used = over ?? gateway;
        return new PublishUseCase(
            used,
            new ReadAccess(used, Store.Read, new SettingsWriter(Store, clock), clock),
            Store.Read,
            new WritePipeline(writeLock, used, null, Store.Read, TimeProvider.System),
            TimeProvider.System,
            TimeSpan.FromMilliseconds(10));
    }

    private static ToolRunner Runner() => new(NullLogger<ToolRunner>.Instance);

    private PublishTool Tool(IOneNoteGateway? over = null) => new(Runner(), UseCase(over));

    /// <summary>A drive letter nothing is mounted on (so it is neither the root's drive nor a network drive).</summary>
    private static char UnusedDriveLetter()
    {
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        return Enumerable.Range('D', 'Z' - 'D' + 1).Select(c => (char)c).Reverse().First(c => !used.Contains(c));
    }

    private static void Junction(string link, string target)
    {
        using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        mklink.WaitForExit();
        Assert.Equal(0, mklink.ExitCode);
    }

    private async Task AssertRefusedAsync()
    {
        Assert.Empty(gateway.PublishCalls);
        Assert.False(Directory.Exists(Root) && Directory.EnumerateFileSystemEntries(Root, "*", SearchOption.AllDirectories).Any());
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

    /// <summary>The fake gateway, with a hook before each <c>GetHierarchy</c>; <c>Publish</c> goes through to it.</summary>
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

        public Task UpdateHierarchyAsync(string changesXml, CancellationToken ct) => inner.UpdateHierarchyAsync(changesXml, ct);

        public Task DeleteHierarchyAsync(string objectId, string expectedLastModified, bool permanent, CancellationToken ct) => inner.DeleteHierarchyAsync(objectId, expectedLastModified, permanent, ct);

        public Task PublishAsync(string objectId, string targetPath, ExportFormat format, CancellationToken ct) => inner.PublishAsync(objectId, targetPath, format, ct);

        public Task WaitForAbandonedWritesAsync(CancellationToken ct) => inner.WaitForAbandonedWritesAsync(ct);
    }
}
