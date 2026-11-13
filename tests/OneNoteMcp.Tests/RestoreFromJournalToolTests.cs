using System.Collections.Concurrent;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using OneNoteMcp.Core;
using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.Journal;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Core.Writes;
using OneNoteMcp.Server;
using OneNoteMcp.Server.Tools;
using OneNoteMcp.Xml;
using Xunit;
using static OneNoteMcp.Tests.PageWriteCheckTests;

namespace OneNoteMcp.Tests;

/// <summary>
/// Story 2.11: <c>restore_from_journal</c> through the AD-7 write pipeline as a <c>Create</c>, one test per row of the
/// plan's I/O matrix and Q2–Q6. Notebook <c>W</c> holds an open section, a password-protected one, a section inside the
/// group <c>G</c>, and the recycle bin; entries are made by a real <see cref="PageSnapshot"/> of the synthetic page
/// (one protected, DPAPI) or written straight to the journal for the cases a snapshot can't make. Each test has a write
/// lock of its own and checks it is free again; refusals before step 7 leave no section, page or content write.
/// </summary>
public sealed class RestoreFromJournalToolTests : IDisposable
{
    private const string Ns = "xmlns:one=\"" + OneNoteXml.Namespace2013 + "\"";
    private const string NotebookId = "{NBW00000-0000-4000-8000-000000000001}{1}{B0}";
    private const string NotebookPath = @"C:\notebooks\W\";
    private const string SectionId = "{SW000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string ProtectedId = "{SP000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string GroupId = "{GW000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string InnerId = "{SI000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string RemadeId = "{SR000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string RecoveredId = "{SC000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string TemplateId = "{7E000000-0000-4000-8000-000000000001}{20}{B0}";
    private const string NewPageDate = "2026-11-12T01:00:00.000Z";
    private const string ClearedDate = "2026-11-12T01:00:05.000Z";
    private const string Title = "Synthetic Update";

    private readonly TempDirectory temp = new();
    private readonly FakeClock clock = new();
    private readonly FakeGateway gateway = new();
    private readonly ConcurrentQueue<WriteLockHeld> holds = new();
    private readonly WriteLock writeLock;
    private readonly PageTextCache cache = PageTextCache.WithDefaults();
    private readonly List<SectionSpec> sections =
    [
        new(SectionId, "Open", null, string.Empty),
        new(ProtectedId, "Vault", null, "encrypted=\"true\""),
        new(InnerId, "Inner", "G", string.Empty),
    ];

    private bool template;

    public RestoreFromJournalToolTests()
    {
        writeLock = new WriteLock($@"Local\OneNoteMcp.Test.{Guid.NewGuid():N}", holds.Enqueue);

        gateway.Parents[NotebookId] = string.Empty;
        gateway.Parents[SectionId] = NotebookId;
        gateway.Parents[ProtectedId] = NotebookId;
        gateway.Parents[GroupId] = NotebookId;
        gateway.Parents[InnerId] = GroupId;
        Self(NotebookId, $"<one:Notebook {Ns} name=\"W\" nickname=\"W\" ID=\"{NotebookId}\" path=\"{NotebookPath}\" />");
        Self(GroupId, $"<one:SectionGroup {Ns} name=\"G\" ID=\"{GroupId}\" path=\"C:\\notebooks\\W\\G\" />");
        foreach (var section in sections)
        {
            Self(section.Id, section.Xml(withNs: true));
        }

        gateway.Pages[PageId] = Snapshot;
        gateway.OnCreated = (_, id) => gateway.Pages[id] = NewPage(id, NewPageDate, template);
        gateway.OnDelete = (page, _) => gateway.Pages[page] = NewPage(page, ClearedDate, withTemplate: false);
        Grant();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewId => FakeGateway.CreatedPageId(1);

    private string Folder => temp.Combine("journal");

    private SettingsStore Store => new(temp.Combine("settings.json"), Folder);

    public void Dispose() => temp.Dispose();

    // --- The matrix ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task RestoreInPlace_CreatesAPageInTheEntrysSection_AndWritesTheSnapshotWithEveryObjectIdStripped()
    {
        var entryId = await EntryAsync(SectionId);
        cache.Set(NewId, DateTimeOffset.UnixEpoch, "cached text", long.MaxValue);

        var root = Ok(await Tool().RestoreFromJournal(entryId, Ct));

        Assert.Equal((SectionId, NewPageStyle.BlankPageWithTitle), Assert.Single(gateway.CreateCalls));
        Assert.Empty(gateway.CreateSectionCalls);
        Assert.Empty(gateway.DeleteCalls);
        var (xml, date) = Assert.Single(gateway.UpdateCalls);
        Assert.Equal(NewPageDate, date); // the new page's own date: force false, the gateway's date rule holds
        var page = XDocument.Parse(xml).Root!;
        Assert.Equal(NewId, (string?)page.Attribute("ID"));
        Assert.DoesNotContain(page.DescendantsAndSelf(), e => e.Attribute("objectID") is not null);
        Assert.Equal(
            XDocument.Parse(Read).Root!.Descendants(XName.Get("T", OneNoteXml.Namespace2013)).Select(t => t.Value),
            page.Descendants(XName.Get("T", OneNoteXml.Namespace2013)).Select(t => t.Value));
        Assert.Empty(SchemaValidation.Problems(xml));
        var meta = root.GetProperty("meta");
        Assert.Equal(NewId, meta.GetProperty("pageId").GetString());
        Assert.Equal(SectionId, meta.GetProperty("parentId").GetString());
        Assert.Equal(0, root.GetProperty("warnings").GetArrayLength());
        Assert.False(cache.TryGet(NewId, DateTimeOffset.UnixEpoch, out _));
        Assert.Equal(meta.GetProperty("correlationId").GetString(), Assert.Single(holds).CorrelationId);
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task AnExistingPage_IsNeverWritten_OnlyThePageCreateNewPageReturned()
    {
        var entryId = await EntryAsync(SectionId);
        template = true;

        Ok(await Tool().RestoreFromJournal(entryId, Ct));

        Assert.All(gateway.UpdateCalls, call => Assert.Equal(NewId, (string?)XDocument.Parse(call.PageXml).Root!.Attribute("ID")));
        Assert.All(gateway.DeleteCalls, call => Assert.Equal(NewId, call.PageId));
        Assert.DoesNotContain(gateway.UpdateCalls, call => call.PageXml.Contains(PageId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task SectionRemade_AtTheSameNamePath_GetsThePage_WithRestoredElsewhere()
    {
        var entryId = await EntryAsync(SectionId);
        Replace(SectionId, new SectionSpec(RemadeId, "OPEN", null, string.Empty)); // names compare ignoring case

        var root = Ok(await Tool().RestoreFromJournal(entryId, Ct));

        Assert.Equal(RemadeId, Assert.Single(gateway.CreateCalls).SectionId);
        Assert.Equal(RemadeId, root.GetProperty("meta").GetProperty("parentId").GetString());
        Assert.Contains("parentNamePath", Warning(root, "restoredElsewhere"), StringComparison.Ordinal);
        Assert.Empty(gateway.CreateSectionCalls);
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task SectionRemadeInAGroup_MatchesGroupAndName_SegmentBySegment()
    {
        var entryId = await EntryAsync(InnerId);
        Replace(InnerId, new SectionSpec(RemadeId, "inner", "g", string.Empty));
        sections.Add(new SectionSpec(RecoveredId, "Inner", null, string.Empty)); // the same name, but not in G

        var root = Ok(await Tool().RestoreFromJournal(entryId, Ct));

        Assert.Equal(RemadeId, Assert.Single(gateway.CreateCalls).SectionId);
        Assert.Equal(["restoredElsewhere"], Codes(root));
    }

    [Fact]
    public async Task SectionGone_CreatesRecovered_UnderTheNotebookByParentId_AndPutsThePageThere()
    {
        var entryId = await EntryAsync(SectionId);
        sections.RemoveAll(s => s.Id == SectionId);

        var root = Ok(await Tool().RestoreFromJournal(entryId, Ct));

        Assert.Equal((NotebookId, "Recovered.one"), Assert.Single(gateway.CreateSectionCalls));
        Assert.Equal(FakeGateway.CreatedSectionId(1), Assert.Single(gateway.CreateCalls).SectionId);
        Assert.Equal(FakeGateway.CreatedSectionId(1), root.GetProperty("meta").GetProperty("parentId").GetString());
        Assert.Contains("Recovered", Warning(root, "restoredElsewhere"), StringComparison.Ordinal);
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task AnExistingRecovered_IsUsedAsFound_AndOneInAGroupDoesntCount()
    {
        var entryId = await EntryAsync(SectionId);
        sections.RemoveAll(s => s.Id == SectionId);
        sections.Add(new SectionSpec(InnerId + "x", "Recovered", "G", string.Empty));
        sections.Add(new SectionSpec(RecoveredId, "recovered", null, string.Empty));

        var root = Ok(await Tool().RestoreFromJournal(entryId, Ct));

        Assert.Empty(gateway.CreateSectionCalls);
        Assert.Equal(RecoveredId, Assert.Single(gateway.CreateCalls).SectionId);
        Assert.Equal(["restoredElsewhere"], Codes(root));
    }

    [Fact]
    public async Task ASectionInTheRecycleBin_IsNotLive()
    {
        var entryId = await EntryAsync(SectionId);
        Replace(SectionId, new SectionSpec(SectionId, "Open", null, "isInRecycleBin=\"true\""));

        Ok(await Tool().RestoreFromJournal(entryId, Ct));

        Assert.Single(gateway.CreateSectionCalls);
    }

    [Fact]
    public async Task NotebookNotOpen_IsObjectNotFound_AskingToOpenItsPath_EvenWhenAnOpenNotebookHasTheEntrysNotebookId()
    {
        var entryId = await EntryAsync(SectionId);
        notebookPathOverride = @"C:\elsewhere\W"; // the same ID, another path: never matched by notebookId (AD-8)

        var error = Error(await Tool().RestoreFromJournal(entryId, Ct), "ObjectNotFound");

        Assert.Contains(NotebookPath.TrimEnd('\\'), error.GetProperty("userAction").GetString(), StringComparison.Ordinal);
        Assert.True(error.GetProperty("retryable").GetBoolean()); // AD-9: retryable once the user opens it
        await AssertRefusedAsync();
    }

    [Fact]
    public async Task NoWriteGrant_AndNoDialog_IsWriteNotGranted()
    {
        var entryId = await EntryAsync(SectionId);
        File.WriteAllText(Store.Path, """{"schemaVersion": 1}""");

        Error(await Tool().RestoreFromJournal(entryId, Ct), "WriteNotGranted");

        await AssertRefusedAsync();
    }

    [Fact]
    public async Task NoWriteGrant_RaisesTheGrantDialog_AsCreatePageDoes_ThenRestores()
    {
        var entryId = await EntryAsync(SectionId);
        File.WriteAllText(Store.Path, """{"schemaVersion": 1}""");
        var prompt = new FakePrompt(new GrantPromptResult(GrantPromptStatus.Accepted, "W"));

        Ok(await RestoreWithPrompt(prompt, entryId));

        Assert.Equal(new WriteGrantQuestion(NotebookId, "W", NotebookPath), Assert.Single(prompt.WriteQuestions));
        Assert.Empty(prompt.Questions);
        Assert.Single(gateway.UpdateCalls);
    }

    [Fact]
    public async Task NoWriteGrant_DialogDeclined_IsWriteNotGranted()
    {
        var entryId = await EntryAsync(SectionId);
        File.WriteAllText(Store.Path, """{"schemaVersion": 1}""");

        Error(await RestoreWithPrompt(new FakePrompt(new GrantPromptResult(GrantPromptStatus.Declined)), entryId), "WriteNotGranted");

        await AssertRefusedAsync();
    }

    [Fact]
    public async Task TargetLocked_IsSectionLocked_Retryable_NoFallThrough()
    {
        var entryId = await EntryAsync(SectionId);
        Replace(SectionId, new SectionSpec(SectionId, "Open", null, "locked=\"true\""));

        var error = Error(await Tool().RestoreFromJournal(entryId, Ct), "SectionLocked");

        Assert.True(error.GetProperty("retryable").GetBoolean());
        await AssertRefusedAsync();
    }

    [Fact]
    public async Task Protected_SectionLive_IsRestoredThere_WithTheWriteGrantOnly_NoReadDialog_AndNothingNamesItsContent()
    {
        var entryId = await EntryAsync(ProtectedId);
        Assert.Contains("\"protected\"", RawEntry(entryId), StringComparison.Ordinal); // DPAPI on disk
        var prompt = new FakePrompt();

        var result = await RestoreWithPrompt(prompt, entryId);

        var root = Ok(result);
        Assert.Equal(ProtectedId, Assert.Single(gateway.CreateCalls).SectionId);
        Assert.Equal(ProtectedId, root.GetProperty("meta").GetProperty("parentId").GetString());
        Assert.Empty(prompt.Order); // neither the read-grant nor the write-grant dialog
        Assert.Single(gateway.UpdateCalls);
        AssertNamesNothing(result);
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task Protected_SectionLocked_IsSectionLocked_RetryableAfterUnlock()
    {
        var entryId = await EntryAsync(ProtectedId);
        Replace(ProtectedId, new SectionSpec(ProtectedId, "Vault", null, "encrypted=\"true\" locked=\"true\""));

        var result = await Tool().RestoreFromJournal(entryId, Ct);

        Assert.True(Error(result, "SectionLocked").GetProperty("retryable").GetBoolean());
        AssertNamesNothing(result);
        await AssertRefusedAsync();
    }

    [Fact]
    public async Task Protected_SectionGone_IsObjectNotFound_NamingTheEntryFile_NeverParentNamePathOrRecovered()
    {
        var entryId = await EntryAsync(ProtectedId);
        sections.RemoveAll(s => s.Id == ProtectedId);
        sections.Add(new SectionSpec(RemadeId, "Vault", "G", "encrypted=\"true\"")); // the same name, protected, but another file: another key
        sections.Add(new SectionSpec(RecoveredId, "Recovered", null, string.Empty));

        var result = await Tool().RestoreFromJournal(entryId, Ct);

        var error = Error(result, "ObjectNotFound");
        Assert.Contains(Path.GetFileName(EntryFile(entryId)), error.GetProperty("userAction").GetString(), StringComparison.Ordinal);
        Assert.False(error.GetProperty("retryable").GetBoolean());
        AssertNamesNothing(result);
        await AssertRefusedAsync();
    }

    [Fact]
    public async Task Protected_SectionNoLongerProtected_IsObjectNotFound()
    {
        var entryId = await EntryAsync(ProtectedId);
        Replace(ProtectedId, new SectionSpec(ProtectedId, "Vault", null, string.Empty));

        var result = await Tool().RestoreFromJournal(entryId, Ct);

        var error = Error(result, "ObjectNotFound");
        Assert.True(error.GetProperty("retryable").GetBoolean()); // AD-9: retryable once the user protects it again
        var userAction = error.GetProperty("userAction").GetString()!;
        Assert.Contains("password-protect", userAction, StringComparison.Ordinal);
        Assert.Contains(Path.GetFileName(EntryFile(entryId)), userAction, StringComparison.Ordinal);
        AssertNamesNothing(result);
        await AssertRefusedAsync();
    }

    [Theory]
    [InlineData("hierarchy")]
    [InlineData("onenoteBackup")]
    public async Task HierarchyAndBackupEntries_AreNotSupported_BeforeAnyComCall(string kind)
    {
        var entryId = WriteEntry(Plain() with { SnapshotKind = kind, Xml = null, BackupFile = kind == "onenoteBackup" ? temp.Combine("journal\\backup-1.one") : null });

        var error = Error(await Tool().RestoreFromJournal(entryId, Ct), "NotSupported");

        var userAction = error.GetProperty("userAction").GetString()!;
        Assert.Contains(kind == "onenoteBackup" ? "backup-1.one" : Path.GetFileName(EntryFile(entryId)), userAction, StringComparison.Ordinal);
        if (kind == "onenoteBackup")
        {
            Assert.Contains("open it in OneNote", userAction, StringComparison.Ordinal);
        }

        AssertNoComCall();
        await AssertRefusedAsync();
    }

    [Fact]
    public async Task SoftDeletedSectionEntry_IsNotSupported_SayingTheSectionIsInTheRecycleBinUnderItsId()
    {
        // Story 5.5 Q4: the entry can't bring a section back; the recycle bin can.
        var entryId = WriteEntry(Plain() with
        {
            SnapshotKind = JournalEntry.HierarchyKind,
            Operation = SoftDeleteUseCase.OperationName,
            OperationClass = nameof(OperationClass.SoftDelete),
            ObjectId = SectionId,
            ParentId = NotebookId,
            ParentNamePath = string.Empty,
            Xml = $"<one:Section {Ns} name=\"Open\" ID=\"{SectionId}\" path=\"C:\\notebooks\\W\\Open.one\"><one:Page ID=\"{PageId}\" name=\"{Title}\" /></one:Section>",
        });

        var error = Error(await Tool().RestoreFromJournal(entryId, Ct), "NotSupported");

        var userAction = error.GetProperty("userAction").GetString()!;
        Assert.Contains(SectionId, userAction, StringComparison.Ordinal);
        Assert.Contains("recycle bin under the same ID", userAction, StringComparison.Ordinal);
        Assert.DoesNotContain(Title, error.GetRawText(), StringComparison.Ordinal);
        AssertNoComCall();
        await AssertRefusedAsync();
    }

    [Fact]
    public async Task SoftDeletedGroupEntry_IsNotSupported_SayingItsSectionsAreInTheRecycleBin_AndTheEntryRecordsItsLayout()
    {
        var entryId = WriteEntry(Plain() with
        {
            SnapshotKind = JournalEntry.HierarchyKind,
            Operation = SoftDeleteUseCase.OperationName,
            OperationClass = nameof(OperationClass.SoftDelete),
            ObjectId = GroupId,
            ParentId = NotebookId,
            ParentNamePath = string.Empty,
            Title = "G",
            Xml = $"<one:SectionGroup {Ns} name=\"G\" ID=\"{GroupId}\" path=\"C:\\notebooks\\W\\G\"><one:Section name=\"Inner\" ID=\"{InnerId}\" path=\"C:\\notebooks\\W\\G\\Inner.one\" /></one:SectionGroup>",
        });

        var error = Error(await Tool().RestoreFromJournal(entryId, Ct), "NotSupported");

        var userAction = error.GetProperty("userAction").GetString()!;
        Assert.Contains("one by one", userAction, StringComparison.Ordinal);
        Assert.Contains(Path.GetFileName(EntryFile(entryId)), userAction, StringComparison.Ordinal);
        Assert.Contains("section group", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        AssertNoComCall();
        await AssertRefusedAsync();
    }

    [Fact]
    public async Task SoftDeletedPageEntry_WithItsRecycleBinId_RestoresAsANewPage_Unchanged()
    {
        // Story 5.5 Q5: a page soft delete's entry is an ordinary page entry; the original stays in Deleted Pages.
        var entryId = WriteEntry(Plain() with
        {
            Operation = SoftDeleteUseCase.OperationName,
            OperationClass = nameof(OperationClass.SoftDelete),
            RecycleBinId = "{5E900000-0000-4000-8000-000000000001}{1}{E1900000000000000000000000000000000000000099}",
        });

        var root = Ok(await Tool().RestoreFromJournal(entryId, Ct));

        Assert.Equal((SectionId, NewPageStyle.BlankPageWithTitle), Assert.Single(gateway.CreateCalls));
        Assert.Single(gateway.UpdateCalls);
        Assert.Empty(gateway.DeleteHierarchyCalls);
        Assert.Equal(NewId, root.GetProperty("meta").GetProperty("pageId").GetString());
    }

    [Fact]
    public async Task UnknownEntry_IsObjectNotFound_WithNoComCall()
    {
        await EntryAsync(SectionId);
        gateway.Calls.Clear();

        var error = Error(await Tool().RestoreFromJournal(Guid.NewGuid().ToString("D"), Ct), "ObjectNotFound");

        Assert.Contains("30 days", error.GetProperty("userAction").GetString(), StringComparison.Ordinal);
        AssertNoComCall();
        await AssertRefusedAsync();
    }

    [Fact]
    public async Task UnknownEntry_WithNoJournalFolderYet_IsObjectNotFound()
    {
        Error(await Tool().RestoreFromJournal(Guid.NewGuid().ToString("N"), Ct), "ObjectNotFound");

        AssertNoComCall();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-guid")]
    public async Task BadArgument_IsInvalidArgument_NamingEntryId_AndNothingIsRead(string entryId)
    {
        var error = Error(await Tool().RestoreFromJournal(entryId, Ct), "InvalidArgument");

        Assert.Contains("entryId", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        AssertNoComCall();
        Assert.False(Directory.Exists(Folder));
        await AssertRefusedAsync();
    }

    [Fact]
    public async Task UnreadableEntry_IsJournalFailed_WithNoComCall()
    {
        var entryId = Guid.NewGuid();
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(Folder, JournalFiles.EntryName(clock.GetUtcNow().UtcDateTime, 1, entryId)), "not gzip");

        Error(await Tool().RestoreFromJournal(entryId.ToString("D"), Ct), "JournalFailed");

        AssertNoComCall();
        await AssertRefusedAsync();
    }

    [Fact]
    public async Task AFileFoundByNameHoldingAnotherEntry_IsJournalFailed_WithNoComCall()
    {
        var entryId = WriteEntry(Plain());
        var file = EntryFile(entryId);
        var asked = Guid.NewGuid();
        File.Move(file, file.Replace(Guid.Parse(entryId).ToString("N"), asked.ToString("N"), StringComparison.Ordinal));

        var error = Error(await Tool().RestoreFromJournal(asked.ToString("D"), Ct), "JournalFailed");

        Assert.Contains(entryId, error.GetProperty("message").GetString(), StringComparison.Ordinal);
        AssertNoComCall();
        await AssertRefusedAsync();
    }

    [Fact]
    public async Task ANewerSchemaVersion_IsNotSupported_WithNoComCall()
    {
        var entryId = WriteEntry(Plain() with { SchemaVersion = JournalEntry.CurrentSchemaVersion + 1 });

        Error(await Tool().RestoreFromJournal(entryId, Ct), "NotSupported");

        AssertNoComCall();
        await AssertRefusedAsync();
    }

    [Fact]
    public async Task SnapshotXmlThatIsntAPage_IsJournalFailed_BeforeAnyWrite()
    {
        var entryId = WriteEntry(Plain() with { Xml = $"<one:Section {Ns} />" });

        Error(await Tool().RestoreFromJournal(entryId, Ct), "JournalFailed");

        await AssertRefusedAsync();
    }

    [Fact]
    public async Task MissingBinaries_RestoreThePageWithout_Them_AndCountThemByKind()
    {
        // One image the snapshot lacked, and an attachment whose temporary copy isn't in the temp folder (Q2).
        var xml = WithInsertedFile(@"C:\Users\someone\Documents\report.docx", @"C:\Users\someone\Documents\report.docx");
        var entryId = WriteEntry(Plain() with
        {
            Xml = xml,
            BinaryComplete = false,
            MissingBinary = [new MissingBinary(MissingBinary.ImageKind, G(21), null), new MissingBinary(MissingBinary.AttachmentKind, "x", null)],
        });

        var root = Ok(await Tool().RestoreFromJournal(entryId, Ct));

        var sent = Assert.Single(gateway.UpdateCalls).PageXml;
        Assert.DoesNotContain("InsertedFile", sent, StringComparison.Ordinal);
        Assert.DoesNotContain("report.docx", sent, StringComparison.Ordinal);
        var message = Warning(root, "binaryMissing");
        Assert.Contains("1 image", message, StringComparison.Ordinal);
        Assert.Contains("1 attachment", message, StringComparison.Ordinal);
        Assert.DoesNotContain("report", message, StringComparison.Ordinal);
        var userAction = WarningUserAction(root, "binaryMissing");
        Assert.Contains(ImagesAdvice, userAction, StringComparison.Ordinal);
        Assert.Contains(AttachmentsAdvice, userAction, StringComparison.Ordinal);
        Assert.DoesNotContain(OtherAdvice, userAction, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnlyAnAttachmentMissing_AdvisesAboutAttachmentsOnly()
    {
        // Seen live: "(1 attachment)" came with advice that opened with images.
        var entryId = WriteEntry(Plain() with { Xml = WithInsertedFile(@"C:\Users\someone\Documents\report.docx", @"C:\Users\someone\Documents\report.docx") });

        var root = Ok(await Tool().RestoreFromJournal(entryId, Ct));

        Assert.Contains("1 attachment", Warning(root, "binaryMissing"), StringComparison.Ordinal);
        var userAction = WarningUserAction(root, "binaryMissing");
        Assert.Equal(AttachmentsAdvice, userAction);
        Assert.DoesNotContain("Images", userAction, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnImageHeldByCallbackOrPath_IsDroppedAndCounted_InlineDataIsKept()
    {
        // Read is the piBasic form: its image is a one:CallbackID, which a page write can't send; here a one:File instead.
        var withFile = After(p => p.Descendants(XName.Get("CallbackID", OneNoteXml.Namespace2013)).Single()
            .ReplaceWith(new XElement(XName.Get("File", OneNoteXml.Namespace2013), new XAttribute("path", @"C:\Users\someone\secret.png"))));
        var entryId = WriteEntry(Plain() with { Xml = withFile });

        var root = Ok(await Tool().RestoreFromJournal(entryId, Ct));

        var sent = Assert.Single(gateway.UpdateCalls).PageXml;
        Assert.DoesNotContain("one:Image", sent, StringComparison.Ordinal);
        Assert.DoesNotContain("secret.png", sent, StringComparison.Ordinal);
        Assert.Contains("1 image", Warning(root, "binaryMissing"), StringComparison.Ordinal);
        Assert.Empty(SchemaValidation.Problems(sent));
    }

    [Theory]
    [InlineData("<link rel=\"stylesheet\" href=\"file:///C:/x.css\">")]
    [InlineData("<img src=\"file:///C:/secret.png\">")]
    public async Task TextWithRefusedHtml_IsRefusedLikeAPageWrite_BeforeAnyPageIsCreated(string html)
    {
        // Phill, 2026-11-13: restore runs the page-write HTML check on text runs too.
        XNamespace one = OneNoteXml.Namespace2013;
        var entryId = WriteEntry(Plain() with { Xml = After(p => p.Descendants(one + "T").First().Value = html) });

        var ex = Error(await Tool().RestoreFromJournal(entryId, Ct), "InvalidArgument");

        Assert.Contains("whose HTML page writes don't accept yet", ex.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(UpdatePageContentWrite.RefusedHtmlUserAction, ex.GetProperty("userAction").GetString());
        Assert.Empty(gateway.UpdateCalls);
        Assert.Empty(gateway.CreateCalls);
    }

    [Fact]
    public async Task TextWithAllowedHtml_Restores()
    {
        XNamespace one = OneNoteXml.Namespace2013;
        var entryId = WriteEntry(Plain() with
        {
            Xml = After(p => p.Descendants(one + "T").First().Value =
                "<span style=\"font-weight:bold\">a</span> <a href=\"https://example.com/\">b</a> <b>c</b>"),
        });

        Ok(await Tool().RestoreFromJournal(entryId, Ct));

        Assert.Contains("example.com", Assert.Single(gateway.UpdateCalls).PageXml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnHtmlBlock_IsDroppedAndCountedAsOther_EvenWithInlineData_AndNestedInAnOE()
    {
        // Phill, 2026-11-13: a forged journal file must never carry what a page write refuses, so restore drops every
        // one:HTMLBlock (OneNote fetches the local files its HTML names) and counts it under binaryMissing.
        XNamespace one = OneNoteXml.Namespace2013;
        var withHtml = SnapshotWith(p =>
        {
            p.Add(new XElement(one + "Outline", new XElement(one + "OEChildren",
                new XElement(one + "OE", new XElement(one + "HTMLBlock",
                    new XElement(one + "Data", Convert.ToBase64String("<img src=\"file:///C:/secret-a.png\">"u8.ToArray())))))));
            p.Add(new XElement(one + "Outline", new XElement(one + "OEChildren",
                new XElement(one + "OE", new XElement(one + "HTMLBlock",
                    new XElement(one + "File", new XAttribute("path", @"C:\Users\someone\secret-b.htm")))))));
        });
        var entryId = WriteEntry(Plain() with { Xml = withHtml });

        var root = Ok(await Tool().RestoreFromJournal(entryId, Ct));

        var sent = Assert.Single(gateway.UpdateCalls).PageXml;
        Assert.DoesNotContain("HTMLBlock", sent, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-b", sent, StringComparison.Ordinal);
        var message = Warning(root, "binaryMissing");
        Assert.Contains("2 other", message, StringComparison.Ordinal);
        Assert.DoesNotContain("image", message, StringComparison.Ordinal);
        Assert.Equal(OtherAdvice, WarningUserAction(root, "binaryMissing"));
    }

    [Fact]
    public async Task AttachmentKept_WhenItsTempCopyIsThere_IsSentWithPathCacheOnly()
    {
        var cacheFile = Path.Combine(Path.GetTempPath(), "{" + Guid.NewGuid().ToString("D").ToUpperInvariant() + "}.bin");
        File.WriteAllText(cacheFile, "attachment bytes");
        try
        {
            var entryId = WriteEntry(Plain() with { Xml = WithInsertedFile(@"C:\Users\someone\Documents\report.docx", cacheFile) });

            var root = Ok(await Tool().RestoreFromJournal(entryId, Ct));

            var file = XDocument.Parse(Assert.Single(gateway.UpdateCalls).PageXml).Descendants(XName.Get("InsertedFile", OneNoteXml.Namespace2013)).Single();
            Assert.Equal(cacheFile, (string?)file.Attribute("pathCache"));
            Assert.Null(file.Attribute("pathSource"));
            Assert.Equal(0, root.GetProperty("warnings").GetArrayLength());
        }
        finally
        {
            File.Delete(cacheFile);
        }
    }

    [Fact]
    public async Task AttachmentRefused_OnAnEmptyPage_IsSentOnceMoreWithoutIt_WithBinaryMissing()
    {
        var cacheFile = TempCopy();
        try
        {
            var entryId = WriteEntry(Plain() with { Xml = WithInsertedFile("C:\\x\\a.docx", cacheFile) });
            gateway.UpdateFailures.Enqueue(new OneNoteException(ErrorCode.InvalidXml, "UpdatePageContent failed: hrInvalidXML.", unchecked((int)0x80042001)));

            var root = Ok(await Tool().RestoreFromJournal(entryId, Ct));

            Assert.Equal(2, gateway.UpdateCalls.Count);
            Assert.Contains("InsertedFile", gateway.UpdateCalls[0].PageXml, StringComparison.Ordinal);
            Assert.DoesNotContain("InsertedFile", gateway.UpdateCalls[1].PageXml, StringComparison.Ordinal);
            Assert.Equal(NewPageDate, gateway.UpdateCalls[1].ExpectedLastModified);
            Assert.Contains("refused", Warning(root, "binaryMissing"), StringComparison.Ordinal);
            await AssertLockFreeAsync();
        }
        finally
        {
            File.Delete(cacheFile);
        }
    }

    [Fact]
    public async Task AttachmentRefused_ButThePageHoldsContent_FailsWithTheCode_NamingThePage_NoSecondWrite()
    {
        var cacheFile = TempCopy();
        try
        {
            var entryId = WriteEntry(Plain() with { Xml = WithInsertedFile("C:\\x\\a.docx", cacheFile) });
            gateway.UpdateFailures.Enqueue(new OneNoteException(ErrorCode.InvalidXml, "UpdatePageContent failed: hrInvalidXML.", unchecked((int)0x80042001)));
            // The re-read after the refusal (the second page read) finds content on the page: no second write (Q2, Q3).
            var partly = NewPage(NewId, NewPageDate, withTemplate: true);
            var reads = 0;
            var probe = new ReadCounter(gateway, () => ++reads == 2 ? partly : null);

            var error = Error(await Tool(probe).RestoreFromJournal(entryId, Ct), "InvalidXml");

            Assert.Single(gateway.UpdateCalls);
            Assert.Contains(NewId, error.GetProperty("message").GetString(), StringComparison.Ordinal);
            Assert.Contains("delete", error.GetProperty("userAction").GetString(), StringComparison.Ordinal);
            await AssertLockFreeAsync();
        }
        finally
        {
            File.Delete(cacheFile);
        }
    }

    [Theory]
    [InlineData(ErrorCode.Timeout, "Timeout")]
    [InlineData(ErrorCode.ComUnavailable, "ComUnavailable")]
    public async Task AttachmentKept_AndAnAmbiguousFailure_IsNotRetried(ErrorCode code, string expected)
    {
        // Follow-up review (2.11): the retry never follows an outcome that may have written (AD-9 ambiguous codes).
        var cacheFile = TempCopy();
        try
        {
            var entryId = WriteEntry(Plain() with { Xml = WithInsertedFile("C:\\x\\a.docx", cacheFile) });
            gateway.UpdateFailures.Enqueue(new OneNoteException(code, "UpdatePageContent failed."));

            Error(await Tool().RestoreFromJournal(entryId, Ct), expected);

            Assert.Single(gateway.UpdateCalls);
        }
        finally
        {
            File.Delete(cacheFile);
        }
    }

    [Fact]
    public async Task AttachmentKept_AndModalDialogOpen_IsNotRetried()
    {
        var cacheFile = TempCopy();
        try
        {
            var entryId = WriteEntry(Plain() with { Xml = WithInsertedFile("C:\\x\\a.docx", cacheFile) });
            gateway.UpdateFailures.Enqueue(new OneNoteException(ErrorCode.ModalDialogOpen, "UpdatePageContent failed: hrModalDialogOpen.", unchecked((int)0x80042030)));

            Error(await Tool().RestoreFromJournal(entryId, Ct), "ModalDialogOpen");

            Assert.Single(gateway.UpdateCalls);
        }
        finally
        {
            File.Delete(cacheFile);
        }
    }

    [Fact]
    public async Task ContentWriteFails_LeavesTheEmptyPage_FailsWithThatStepsCode_NamingThePage()
    {
        var entryId = await EntryAsync(SectionId);
        gateway.UpdateFailure = new OneNoteException(ErrorCode.ModalDialogOpen, "UpdatePageContent failed: hrModalDialogOpen.", unchecked((int)0x80042030));

        var error = Error(await Tool().RestoreFromJournal(entryId, Ct), "ModalDialogOpen");

        Assert.Single(gateway.CreateCalls);
        Assert.Contains(NewId, error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Contains(NewId, error.GetProperty("userAction").GetString(), StringComparison.Ordinal);
        Assert.True(error.GetProperty("retryable").GetBoolean());
        Assert.False(error.TryGetProperty("outcomeUnknown", out _));
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task AnAmbiguousContentWriteFailure_IsOutcomeUnknown_NamingThePage()
    {
        var entryId = await EntryAsync(SectionId);
        gateway.UpdateFailure = new OneNoteException(ErrorCode.Timeout, "UpdatePageContent did not return within 30 s.");

        var error = Error(await Tool().RestoreFromJournal(entryId, Ct), "Timeout");

        Assert.True(error.GetProperty("outcomeUnknown").GetBoolean());
        Assert.False(error.GetProperty("retryable").GetBoolean());
        Assert.Contains(NewId, error.GetProperty("message").GetString(), StringComparison.Ordinal);
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task AClientCancelAfterCreateNewPage_IsOutcomeUnknown_AndNamesTheNewPage()
    {
        // 2.11 Q3: the pipeline's own step-7 cancel message carries the created page and the delete-it guidance.
        var entryId = await EntryAsync(SectionId);
        using var cts = new CancellationTokenSource();
        gateway.OnUpdateCall = async ct =>
        {
            await cts.CancelAsync();
            ct.ThrowIfCancellationRequested();
        };

        var error = Error(await Tool().RestoreFromJournal(entryId, cts.Token), "Cancelled");

        Assert.True(error.GetProperty("outcomeUnknown").GetBoolean());
        var message = error.GetProperty("message").GetString()!;
        Assert.Contains(NewId, message, StringComparison.Ordinal);
        Assert.Contains("delete the empty page", message, StringComparison.Ordinal);
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task ASnapshotWhoseSentVariantFailsTheSchemaGate_IsRefusedBeforeCom()
    {
        // The gate runs on each variant step 7 may send (with kept files, and without them); a kept attachment carrying
        // an attribute the 2013 schema doesn't declare fails it before anything is created.
        var cacheFile = TempCopy();
        try
        {
            var entryId = WriteEntry(Plain() with
            {
                Xml = WithInsertedFile("C:\\x\\a.docx", cacheFile).Replace("preferredName=", "bogus=\"1\" preferredName=", StringComparison.Ordinal),
            });

            Error(await Tool().RestoreFromJournal(entryId, Ct), "InvalidXml");

            await AssertRefusedAsync();
        }
        finally
        {
            File.Delete(cacheFile);
        }
    }

    [Fact]
    public async Task AnAmbiguousCreateFailure_IsOutcomeUnknown_WithNoContentWrite()
    {
        var entryId = await EntryAsync(SectionId);
        gateway.CreateFailure = new OneNoteException(ErrorCode.ComUnavailable, "CreateNewPage failed: RPC_E_DISCONNECTED.");

        var error = Error(await Tool().RestoreFromJournal(entryId, Ct), "ComUnavailable");

        Assert.True(error.GetProperty("outcomeUnknown").GetBoolean());
        Assert.Empty(gateway.UpdateCalls);
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task ATemplatedNewPage_HasEachTopLevelTemplateObjectDeleted_ThenIsWrittenWithTheDateAfterTheDeletes()
    {
        var entryId = await EntryAsync(SectionId);
        template = true;

        Ok(await Tool().RestoreFromJournal(entryId, Ct));

        Assert.Equal((NewId, TemplateId, NewPageDate), Assert.Single(gateway.DeleteCalls));
        Assert.Equal(ClearedDate, Assert.Single(gateway.UpdateCalls).ExpectedLastModified);
    }

    [Fact]
    public async Task APageWithNoTitle_IsCreatedWithNoTitle()
    {
        var entryId = WriteEntry(Plain() with { Xml = SnapshotWith(p => p.Element(XName.Get("Title", OneNoteXml.Namespace2013))!.Remove()) });

        Ok(await Tool().RestoreFromJournal(entryId, Ct));

        Assert.Equal(NewPageStyle.BlankPageNoTitle, Assert.Single(gateway.CreateCalls).Style);
    }

    // --- Helpers ------------------------------------------------------------------------------------------------------

    private string? notebookPathOverride;

    private sealed record SectionSpec(string Id, string Name, string? Group, string Extra)
    {
        public string Path => Group is null ? $"C:\\notebooks\\W\\{Name}.one" : $"C:\\notebooks\\W\\{Group}\\{Name}.one";

        public string Xml(bool withNs = false) =>
            $"<one:Section {(withNs ? Ns : string.Empty)} name=\"{Name}\" ID=\"{Id}\" path=\"{Path}\" {Extra} />";
    }

    private void Self(string id, string xml) => gateway.Hierarchies[(id, HierarchyScope.Self)] = "<?xml version=\"1.0\"?>\n" + xml;

    private void Replace(string id, SectionSpec with)
    {
        var at = sections.FindIndex(s => s.Id == id);
        sections[at] = with;
    }

    /// <summary>Publishes <see cref="sections"/> as both reads the restore makes: all notebooks (step 1) and W's sections (step 3).</summary>
    private void Publish()
    {
        var body = new System.Text.StringBuilder();
        foreach (var section in sections)
        {
            body.Append(section.Group is null
                ? section.Xml()
                : $"<one:SectionGroup name=\"{section.Group}\" ID=\"{GroupId}\" path=\"C:\\notebooks\\W\\{section.Group}\">{section.Xml()}</one:SectionGroup>");
        }

        body.Append("<one:SectionGroup name=\"OneNote_RecycleBin\" ID=\"{RB}\" path=\"C:\\notebooks\\W\\OneNote_RecycleBin\" isRecycleBin=\"true\">")
            .Append("<one:Section name=\"Open\" ID=\"{SD}\" path=\"C:\\notebooks\\W\\OneNote_RecycleBin\\Open.one\" isInRecycleBin=\"true\" />")
            .Append("<one:Section name=\"Recovered\" ID=\"{SE}\" path=\"C:\\notebooks\\W\\OneNote_RecycleBin\\Recovered.one\" isInRecycleBin=\"true\" />")
            .Append("</one:SectionGroup>");
        var notebook = $"<one:Notebook {Ns} name=\"W\" nickname=\"W\" ID=\"{NotebookId}\" path=\"{notebookPathOverride ?? NotebookPath}\">{body}</one:Notebook>";
        gateway.Hierarchies[(NotebookId, HierarchyScope.Sections)] = "<?xml version=\"1.0\"?>\n" + notebook;
        gateway.Hierarchies[(string.Empty, HierarchyScope.Sections)] =
            $"<?xml version=\"1.0\"?>\n<one:Notebooks {Ns}>{notebook.Replace(" " + Ns, string.Empty, StringComparison.Ordinal)}</one:Notebooks>";
    }

    private void Grant() =>
        new SettingsWriter(Store, clock).SaveWriteGrant(
            new NotebookGrant(GrantKeys.NotebookKey(NotebookPath), "W", NotebookId, Write: true, PermanentDelete: false, clock.GetUtcNow(), GrantedVia.Cli));

    /// <summary>A real snapshot of the synthetic page in <paramref name="sectionId"/>; returns the entry's ID. Clears the calls it made.</summary>
    private async Task<string> EntryAsync(string sectionId)
    {
        gateway.Parents[PageId] = sectionId;
        var written = await new PageSnapshot(gateway, new JournalStore(Store.Read), clock)
            .TakeAsync(new PageSnapshotRequest("snapshot", UpdatePageContentWrite.OperationName, OperationClass.PageWrite, PageId, Read), Ct);
        gateway.Calls.Clear();
        gateway.PageCalls.Clear();
        return written.EntryId.ToString("D");
    }

    /// <summary>An unprotected page entry of the synthetic page in section Open, for <c>with</c> edits.</summary>
    private static JournalEntry Plain() => new()
    {
        EntryId = Guid.NewGuid(),
        CorrelationId = "c",
        Operation = UpdatePageContentWrite.OperationName,
        OperationClass = nameof(OperationClass.PageWrite),
        ObjectId = PageId,
        NotebookId = NotebookId,
        NotebookPath = GrantKeys.NotebookKey(NotebookPath),
        NotebookName = "W",
        ParentId = SectionId,
        ParentNamePath = "Open",
        Title = Title,
        TakenAtUtc = new DateTime(2026, 11, 12, 0, 0, 0, DateTimeKind.Utc),
        BinaryComplete = true,
        Xml = Snapshot,
    };

    /// <summary><see cref="Snapshot"/> after <paramref name="edit"/>.</summary>
    private static string SnapshotWith(Action<XElement> edit)
    {
        var page = XDocument.Parse(Snapshot);
        edit(page.Root!);
        return page.ToString();
    }

    /// <summary>The synthetic page as a piBinaryData read returns it: its image's binary inline, not by callback.</summary>
    private static string Snapshot => After(p =>
    {
        var callback = p.Descendants(XName.Get("CallbackID", OneNoteXml.Namespace2013)).Single();
        callback.ReplaceWith(new XElement(XName.Get("Data", OneNoteXml.Namespace2013), "iVBORw0KGgo="));
    });

    private string WriteEntry(JournalEntry entry) => new JournalStore(Store.Read).Write(entry).EntryId.ToString("D");

    private string EntryFile(string entryId) => JournalFiles.Find(Folder, Guid.Parse(entryId))!;

    private string RawEntry(string entryId)
    {
        using var file = File.OpenRead(EntryFile(entryId));
        using var gzip = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        return reader.ReadToEnd();
    }

    /// <summary>The synthetic page with an attachment in a new OE of the first Outline.</summary>
    private static string WithInsertedFile(string pathSource, string pathCache) => SnapshotWith(p =>
        p.Descendants(XName.Get("OEChildren", OneNoteXml.Namespace2013)).First().Add(
            new XElement(
                XName.Get("OE", OneNoteXml.Namespace2013),
                new XAttribute("objectID", G(90)),
                new XElement(
                    XName.Get("InsertedFile", OneNoteXml.Namespace2013),
                    new XAttribute("pathSource", pathSource),
                    new XAttribute("pathCache", pathCache),
                    new XAttribute("preferredName", "report.docx")))));

    private static string TempCopy()
    {
        var path = Path.Combine(Path.GetTempPath(), "{" + Guid.NewGuid().ToString("D") + "}.bin");
        File.WriteAllText(path, "bytes");
        return path;
    }

    /// <summary>A new page as OneNote makes it: a title line, and with <paramref name="withTemplate"/> one template Outline.</summary>
    private static string NewPage(string id, string date, bool withTemplate) =>
        $"<?xml version=\"1.0\"?>\n<one:Page {Ns} ID=\"{id}\" name=\"\" dateTime=\"{date}\" lastModifiedTime=\"{date}\" pageLevel=\"1\">"
        + "<one:Title><one:OE objectID=\"{7E000000-0000-4000-8000-000000000001}{10}{B0}\"><one:T><![CDATA[]]></one:T></one:OE></one:Title>"
        + (withTemplate
            ? $"<one:Outline objectID=\"{TemplateId}\"><one:OEChildren><one:OE objectID=\"{{7E000000-0000-4000-8000-000000000001}}{{21}}{{B0}}\"><one:T><![CDATA[template]]></one:T></one:OE></one:OEChildren></one:Outline>"
            : string.Empty)
        + "</one:Page>";

    private RestoreFromJournalUseCase UseCase(IOneNoteGateway? over = null)
    {
        Publish();
        var g = over ?? gateway;
        return new RestoreFromJournalUseCase(
            g,
            new WriteAccess(g, Store.Read),
            new WritePipeline(writeLock, g, cache, Store.Read, TimeProvider.System, dialog: new WriteGrantDialog(g, new SettingsWriter(Store, clock), clock)),
            Store.Read);
    }

    private RestoreFromJournalTool Tool(IOneNoteGateway? over = null) => new(new ToolRunner(NullLogger<ToolRunner>.Instance), UseCase(over));

    private Task<CallToolResult> RestoreWithPrompt(IGrantPrompt prompt, string entryId)
    {
        var useCase = UseCase();
        return new ToolRunner(NullLogger<ToolRunner>.Instance).RunAsync(
            RestoreFromJournalTool.Name,
            useCase,
            (context, token) => useCase.ExecuteAsync(context, entryId, token),
            Ct,
            prompt);
    }

    private void AssertNoComCall()
    {
        Assert.Empty(gateway.Calls);
        Assert.Empty(gateway.PageCalls);
    }

    /// <summary>Acceptance 1: nothing created or written, and the lock is free.</summary>
    private async Task AssertRefusedAsync()
    {
        Assert.Empty(gateway.CreateCalls);
        Assert.Empty(gateway.CreateSectionCalls);
        Assert.Empty(gateway.UpdateCalls);
        Assert.Empty(gateway.DeleteCalls);
        await AssertLockFreeAsync();
    }

    private async Task AssertLockFreeAsync()
    {
        using var probe = await writeLock.AcquireAsync("free?", TimeSpan.FromMilliseconds(500), Ct);
    }

    /// <summary>Acceptance 2: no message, warning or userAction carries the protected page's title, content or parentNamePath.</summary>
    private static void AssertNamesNothing(CallToolResult result)
    {
        var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text)) + result.StructuredContent?.GetRawText();
        foreach (var secret in new[] { Title, "Synthetic update", "Outline A", "Vault" })
        {
            Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static List<string> Codes(JsonElement root) =>
        [.. root.GetProperty("warnings").EnumerateArray().Select(w => w.GetProperty("code").GetString()!)];

    private static string Warning(JsonElement root, string code) =>
        root.GetProperty("warnings").EnumerateArray().First(w => w.GetProperty("code").GetString() == code).GetProperty("message").GetString()!;

    private static string WarningUserAction(JsonElement root, string code) =>
        root.GetProperty("warnings").EnumerateArray().First(w => w.GetProperty("code").GetString() == code).GetProperty("userAction").GetString()!;

    private const string ImagesAdvice =
        "Images and ink missing from the snapshot weren't on this PC when it was taken; re-insert any you still have.";

    private const string AttachmentsAdvice =
        "Attachments and media come back only while OneNote's temporary copy still exists; re-insert any that are missing from the original files.";

    private const string OtherAdvice =
        "Other embedded content, such as HTML blocks and printouts, isn't restored; add it back by hand if you need it.";

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

    /// <summary>Wraps the fake: <paramref name="answer"/> may replace a <c>GetPageContent</c> result (null keeps the fake's).</summary>
    private sealed class ReadCounter(FakeGateway inner, Func<string?> answer) : IOneNoteGateway
    {
        public Task<string> GetHierarchyAsync(string startNodeId, HierarchyScope scope, CancellationToken ct) => inner.GetHierarchyAsync(startNodeId, scope, ct);

        public Task<string> GetHierarchyParentAsync(string objectId, CancellationToken ct) => inner.GetHierarchyParentAsync(objectId, ct);

        public async Task<string> GetPageContentAsync(string pageId, PageInfo pageInfo, CancellationToken ct)
        {
            var real = await inner.GetPageContentAsync(pageId, pageInfo, ct);
            return answer() ?? real;
        }

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

        public Task WaitForAbandonedWritesAsync(CancellationToken ct) => inner.WaitForAbandonedWritesAsync(ct);
    }
}
