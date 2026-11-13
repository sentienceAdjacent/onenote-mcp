using System.Globalization;
using System.Runtime.Versioning;
using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.Journal;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Core.Writes;
using OneNoteMcp.Xml;

namespace OneNoteMcp.Core.UseCases;

/// <summary>
/// <c>restore_from_journal</c> (AD-8; story 2.11): brings back a page from a recovery-journal entry as a new page, through
/// the write pipeline (AD-7) as a <c>Create</c>. It never overwrites: its only content write goes to the page it created.
/// <list type="number">
/// <item>Step 1, before the lock: the entry is found by its ID in the journal folder and read (a protected entry is
/// decrypted; no read grant is ever asked for, AD-21); a <c>hierarchy</c> or <c>onenoteBackup</c> entry is
/// <see cref="ErrorCode.NotSupported"/> before any COM call; the notebook is found among the open notebooks by its
/// <c>NotebookKey</c> equal to the entry's <c>notebookPath</c>, never by <c>notebookId</c>; then the notebook's write grant
/// (the grant dialog as for <c>create_page</c>).</item>
/// <item value="3">Steps 3 and 4, under the lock: the notebook's sections, read live, and the target. A protected entry
/// goes only to the live section whose <c>SectionKey</c> is its marker and that is still protected. Any other goes to the
/// live section with its <c>parentId</c>, else the one whose group/name path is its <c>parentNamePath</c> (ignoring case),
/// else a <c>Recovered</c> section directly under the notebook, made in step 7 if there is none. A locked target is
/// <see cref="ErrorCode.SectionLocked"/> (Q6). The page XML is built (<see cref="RestoredPage.Build"/>) and gated (AD-16).</item>
/// <item value="7">Step 7: the <c>Recovered</c> section if needed, the page, the template objects deleted (Q4), the
/// content written with the new page's own date. A write that carried kept attachments and was refused gets one more
/// try without them, into the page only if it is still empty (Q2). A failure after the page exists names it (Q3).</item>
/// </list>
/// Messages carry IDs, counts and the entry's file name only: never a title, page content or a protected entry's
/// <c>parentNamePath</c> (AD-9, AD-13, AD-21).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RestoreFromJournalUseCase : IUseCase
{
    /// <summary>The section a page goes to when neither its section nor one at its name path is left (AD-8).</summary>
    public const string RecoveredSectionName = "Recovered";

    /// <summary>What to do with an <c>entryId</c> that isn't a GUID.</summary>
    public const string EntryIdUserAction =
        "Pass entryId: the meta.journalEntryId that update_page_content or delete_page_content returned, or the ID their failure message names.";

    /// <summary>What to do when no entry has that ID.</summary>
    public const string UnknownEntryUserAction =
        "Check the entry ID. Journal entries are kept for 30 days (journal.retentionDays), then pruned; an older entry can't be restored.";

    private readonly IOneNoteGateway gateway;
    private readonly WriteAccess access;
    private readonly WritePipeline pipeline;
    private readonly Func<SettingsSnapshot> settings;
    private readonly Func<string, bool> keepPathCache;

    /// <param name="gateway">The gateway.</param>
    /// <param name="access">The write-grant check (AD-6).</param>
    /// <param name="pipeline">The write pipeline (AD-7).</param>
    /// <param name="settings">Reads <c>settings.json</c> for <c>journal.path</c>, at every call.</param>
    /// <param name="keepPathCache">Which attachment <c>pathCache</c>s may be sent (Q2); <see cref="RestoreAttachments.Keeps(string)"/> when null.</param>
    public RestoreFromJournalUseCase(IOneNoteGateway gateway, WriteAccess access, WritePipeline pipeline, Func<SettingsSnapshot> settings, Func<string, bool>? keepPathCache = null)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(settings);
        this.gateway = gateway;
        this.access = access;
        this.pipeline = pipeline;
        this.settings = settings;
        this.keepPathCache = keepPathCache ?? RestoreAttachments.Keeps;
    }

    public OperationClass Class => OperationClass.Create;

    public async Task<ToolSuccess> ExecuteAsync(ToolCallContext context, string entryId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        // A bad argument reads nothing.
        if (string.IsNullOrWhiteSpace(entryId) || !Guid.TryParse(entryId.Trim(), out var id))
        {
            throw new OneNoteException(
                ErrorCode.InvalidArgument,
                string.IsNullOrWhiteSpace(entryId)
                    ? "The argument entryId is empty, so nothing was read or restored."
                    : "The argument entryId isn't a GUID, so nothing was read or restored.",
                userAction: EntryIdUserAction,
                retryable: false);
        }

        var restore = new Restore(gateway, access, settings, keepPathCache, id);
        var result = await pipeline.RunAsync(context, restore, ct).ConfigureAwait(false);
        return new ToolSuccess(null, new ToolMeta(context.CorrelationId, ParentId: result.SectionId, PageId: result.PageId), result.Warnings);
    }

    /// <summary>What a restore made: the new page, the section it is in, and its warnings.</summary>
    private sealed record Restored(string PageId, string SectionId, IReadOnlyList<ToolWarning> Warnings);

    /// <summary>The <c>Create</c> class's steps for one restore.</summary>
    private sealed class Restore(IOneNoteGateway gateway, WriteAccess access, Func<SettingsSnapshot> settings, Func<string, bool> keepPathCache, Guid entryId) : WriteOperation<Restored>
    {
        private const string FileName = RecoveredSectionName + SectionFileNames.Extension;

        private JournalEntry? entry;
        private string entryFile = string.Empty;
        private OpenNotebook? notebook;
        private NotebookSection? target;
        private string? fallback;
        private string? createdPageId;

        public override OperationClass Class => OperationClass.Create;

        public override IReadOnlyCollection<string> WrittenPageIds => createdPageId is null ? [] : [createdPageId];

        /// <summary>Q3 for every step-7 failure once the page exists, the pipeline's cancel and deadline included.</summary>
        public override string? FailureNote => createdPageId is { } pageId
            ? $"Page {pageId} was created for journal entry {entryId:D} but its content may not have been written. {EmptyPageUserAction(pageId)}"
            : null;

        private static string EmptyPageUserAction(string pageId) =>
            $"Ask the user to delete the empty page {pageId} in OneNote before retrying.";

        private JournalEntry Entry => entry ?? throw new InvalidOperationException("Step 1 hasn't read the entry.");

        private bool IsProtected => Entry.SectionKey is not null;

        public override async Task AuthorizeAsync(CancellationToken ct)
        {
            // The entry once, even when step 1 runs again after the grant dialog.
            entry ??= ReadEntry();

            var open = OpenNotebooks.Find(await gateway.GetHierarchyAsync(string.Empty, HierarchyScope.Sections, ct).ConfigureAwait(false));
            notebook = open.FirstOrDefault(n => n.Key.Length > 0 && GrantKeys.Comparer.Equals(n.Key, Entry.NotebookPath))
                ?? throw new OneNoteException(
                    ErrorCode.ObjectNotFound,
                    $"The notebook of journal entry {entryId:D} ({entryFile}) isn't open in OneNote, so nothing was restored.",
                    userAction: $"Ask the user to open the notebook at {Entry.NotebookPath} in OneNote, then retry.",
                    retryable: true); // AD-9: retryable once the user opens it (AD-8)

            access.CheckNotebook(notebook);
        }

        private JournalEntry ReadEntry()
        {
            string? path;
            try
            {
                path = JournalFiles.Find(settings().Journal.Path, entryId);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                throw new OneNoteException(ErrorCode.JournalFailed, $"The journal folder couldn't be searched for entry {entryId:D} ({ex.GetType().Name}).", innerException: ex);
            }

            if (path is null)
            {
                throw new OneNoteException(
                    ErrorCode.ObjectNotFound,
                    $"No journal entry has the ID {entryId:D}, so nothing was restored.",
                    userAction: UnknownEntryUserAction);
            }

            entryFile = Path.GetFileName(path);
            var read = JournalStore.Read(path);

            // Found by name only: a renamed or copied file must not restore another snapshot under this ID.
            if (read.EntryId != entryId)
            {
                throw new OneNoteException(
                    ErrorCode.JournalFailed,
                    $"The journal file {entryFile} found for entry {entryId:D} holds entry {read.EntryId:D}, not that entry, so nothing was restored.");
            }

            switch (read.SnapshotKind)
            {
                case JournalEntry.PageKind:
                    break;
                case "onenoteBackup":
                    throw new OneNoteException(
                        ErrorCode.NotSupported,
                        $"Journal entry {entryId:D} ({entryFile}) holds a OneNote backup of a section, which restore_from_journal doesn't restore, so nothing was restored.",
                        userAction: string.IsNullOrEmpty(read.BackupFile)
                            ? $"The entry file {entryFile} is in the journal folder (journal.path in settings.json) for its 30 days."
                            : $"The backup file {Path.GetFileName(read.BackupFile)} is in the journal folder (journal.path in settings.json). The user can open it in OneNote (File > Open), though that may not bring the section back.");
                default:
                    throw new OneNoteException(
                        ErrorCode.NotSupported,
                        $"Journal entry {entryId:D} ({entryFile}) is a {(read.SnapshotKind.Length == 0 ? "non-page" : read.SnapshotKind)} snapshot, and restore_from_journal restores pages only, so nothing was restored.",
                        userAction: $"The entry file {entryFile} is in the journal folder (journal.path in settings.json) for its 30 days; restore it by hand from there.");
            }

            if (read.Xml is null)
            {
                throw new OneNoteException(ErrorCode.JournalFailed, $"Journal entry {entryId:D} ({entryFile}) holds no page XML.");
            }

            return read;
        }

        public override async Task ReadAsync(CancellationToken ct)
        {
            var nb = notebook ?? throw new InvalidOperationException("Step 1 hasn't found the notebook.");
            var xml = await gateway.GetHierarchyAsync(nb.Id, HierarchyScope.Sections, ct).ConfigureAwait(false);
            var live = XmlReads.Readable(() => NotebookSections.Find(xml), "GetHierarchy").Where(s => !s.InRecycleBin).ToList();
            target = IsProtected ? ProtectedTarget(live) : Target(live);
        }

        /// <summary>A protected entry's only target: the live section with its <c>SectionKey</c>, still protected (AD-8; 2.3 Q6).</summary>
        private NotebookSection ProtectedTarget(IReadOnlyList<NotebookSection> live)
        {
            var section = live.FirstOrDefault(s => s.Path.Length > 0 && GrantKeys.Comparer.Equals(GrantKeys.SectionKey(s.Path), Entry.SectionKey!));
            if (section is null)
            {
                throw new OneNoteException(
                    ErrorCode.ObjectNotFound,
                    $"The password-protected section journal entry {entryId:D} came from is gone, so nothing was restored.",
                    userAction: $"A protected page goes back only into its own protected section. The entry file {entryFile} stays in the journal folder (journal.path in settings.json) for its 30 days.",
                    retryable: false);
            }

            if (!section.Encrypted)
            {
                // AD-9: retryable once the user protects the section again at the same path.
                throw new OneNoteException(
                    ErrorCode.ObjectNotFound,
                    $"The section journal entry {entryId:D} came from is no longer password-protected, so nothing was restored.",
                    userAction: $"A protected page goes back only into its own section while it is password-protected. Ask the user to password-protect that section again in OneNote (same path), then retry. The entry file {entryFile} stays in the journal folder (journal.path in settings.json) for its 30 days.",
                    retryable: true);
            }

            return section;
        }

        /// <summary>An unprotected entry's target: its <c>parentId</c>, else its <c>parentNamePath</c>, else <c>Recovered</c> (null: made in step 7).</summary>
        private NotebookSection? Target(IReadOnlyList<NotebookSection> live)
        {
            if (Entry.ParentId.Length > 0 && live.FirstOrDefault(s => string.Equals(s.Id, Entry.ParentId, StringComparison.Ordinal)) is { } own)
            {
                return own;
            }

            var byPath = live.Where(s => SamePath(s, Entry.ParentNamePath)).ToList();
            if (byPath.Count == 1)
            {
                fallback = "parentNamePath";
                return byPath[0];
            }

            fallback = RecoveredSectionName;
            return live.FirstOrDefault(s => s.Groups.Count == 0 && string.Equals(s.Name, RecoveredSectionName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Segment by segment, ignoring case, as Windows names compare; names can't hold <c>/</c>, so the split is safe.</summary>
        private static bool SamePath(NotebookSection section, string namePath)
        {
            if (namePath.Length == 0)
            {
                return false;
            }

            var segments = namePath.Split('/');
            string[] own = [.. section.Groups, section.Name];
            return segments.Length == own.Length
                && segments.Zip(own).All(pair => string.Equals(pair.First, pair.Second, StringComparison.OrdinalIgnoreCase));
        }

        public override void Validate()
        {
            if (target is { Locked: true } locked)
            {
                throw new OneNoteException(ErrorCode.SectionLocked, $"Section {locked.Id} is locked, so journal entry {entryId:D} wasn't restored.");
            }

            // Both variants step 7 may send (with kept files, and Q2's retry without them), built with a stand-in ID and
            // gated before COM; step 7 gates the exact XML it sends again.
            var standIn = Entry.ObjectId.Length > 0 ? Entry.ObjectId : "{00000000-0000-0000-0000-000000000000}{1}{B0}";
            Gate(Build(standIn, keepFiles: true));
            Gate(Build(standIn, keepFiles: false));
        }

        /// <summary>The AD-16 gate on XML about to be sent: a schema problem is <see cref="ErrorCode.InvalidXml"/>, naming positions and element names only; HTML a page write refuses is <see cref="ErrorCode.InvalidArgument"/>, as for a page write.</summary>
        private void Gate(RestoredPageXml built)
        {
            var problems = SchemaValidation.Problems(built.Xml);
            if (problems.Count > 0)
            {
                throw new OneNoteException(
                    ErrorCode.InvalidXml,
                    string.Create(CultureInfo.InvariantCulture, $"The page in journal entry {entryId:D} ({entryFile}) doesn't match the OneNote 2013 schema at {problems.Count} place(s) (first: line {problems[0].Line}, position {problems[0].Position}{(problems[0].DisplayName is { } name ? ", " + name : string.Empty)}), so it wasn't written."),
                    userAction: $"The entry file {entryFile} stays in the journal folder (journal.path in settings.json) for its 30 days.");
            }

            // Phill, 2026-11-13: the page-write HTML check on the text runs too, with no read to compare against, so a
            // forged entry never carries what a page write refuses. Runs in step 3, before the page is created.
            if (UpdatePageContentWrite.HtmlRefusal(built.Xml, null, $"The page in journal entry {entryId:D} ({entryFile})") is { } refusal)
            {
                throw refusal;
            }
        }

        private RestoredPageXml Build(string pageId, bool keepFiles)
        {
            try
            {
                return RestoredPage.Build(Entry.Xml!, pageId, keepPathCache, keepFiles);
            }
            catch (OneNoteXmlException ex)
            {
                // The parse message carries positions and element names only (AD-13).
                throw new OneNoteException(ErrorCode.JournalFailed, $"The page in journal entry {entryId:D} ({entryFile}) can't be read: {ex.Message}");
            }
        }

        public override async Task<Restored> CallAsync(CancellationToken ct)
        {
            var sectionId = target?.Id ?? await gateway.CreateSectionAsync(notebook!.Id, FileName, ct).ConfigureAwait(false);
            var style = XmlReads.Readable(() => RestoredPage.HasTitle(Entry.Xml!), "The journal entry") ? NewPageStyle.BlankPageWithTitle : NewPageStyle.BlankPageNoTitle;
            createdPageId = await gateway.CreateNewPageAsync(sectionId, style, ct).ConfigureAwait(false);
            var pageId = createdPageId;

            RestoredPageXml sent;
            var refused = 0;
            try
            {
                var date = await ClearTemplateAsync(pageId, ct).ConfigureAwait(false);
                sent = Build(pageId, keepFiles: true);
                Gate(sent);
                try
                {
                    await gateway.UpdatePageContentAsync(sent.Xml, date, ct).ConfigureAwait(false);
                }
                catch (OneNoteException ex) when (sent.KeptFiles > 0 && OneNoteMayHaveRefusedFiles(ex))
                {
                    // Q2: one more try without the files, into this call's own page, only while it is still empty.
                    var basic = await gateway.GetPageContentAsync(pageId, PageInfo.Basic, ct).ConfigureAwait(false);
                    if (XmlReads.Readable(() => RestoredPage.HasContent(basic), "GetPageContent"))
                    {
                        throw;
                    }

                    refused = sent.KeptFiles;
                    sent = Build(pageId, keepFiles: false);
                    Gate(sent);
                    await gateway.UpdatePageContentAsync(sent.Xml, LastModified(basic), ct).ConfigureAwait(false);
                }
            }
            catch (OneNoteException ex)
            {
                // Q3: the page exists but may be empty; the step's own code and retry rule stand (outcomeUnknown is the pipeline's).
                throw new OneNoteException(
                    ex.Code,
                    $"{ex.Message} {FailureNote}",
                    ex.ComHResult,
                    ex,
                    ex.OutcomeUnknown,
                    EmptyPageUserAction(pageId) + " " + (ex.UserAction ?? ToolErrors.UserAction(ex.Code)),
                    ex.Retryable);
            }

            return new Restored(pageId, sectionId, Warnings(sent, refused));
        }

        /// <summary>Q4: deletes each top-level object a template put on the new page, then returns the page's date for the content write.</summary>
        private async Task<string> ClearTemplateAsync(string pageId, CancellationToken ct)
        {
            var basic = await gateway.GetPageContentAsync(pageId, PageInfo.Basic, ct).ConfigureAwait(false);
            var template = XmlReads.Readable(() => RestoredPage.TemplateObjectIds(basic), "GetPageContent");
            foreach (var objectId in template)
            {
                await gateway.DeletePageContentAsync(pageId, objectId, LastModified(basic), ct).ConfigureAwait(false);
                basic = await gateway.GetPageContentAsync(pageId, PageInfo.Basic, ct).ConfigureAwait(false);
            }

            return LastModified(basic);
        }

        private static string LastModified(string pageXml) =>
            XmlReads.Readable(() => PageBasis.From(pageXml), "GetPageContent").LastModifiedTime ?? string.Empty;

        /// <summary>
        /// Q2's retry condition: a definite failure (not AD-9's ambiguous ones) that isn't about the page or OneNote's state
        /// (<c>ModalDialogOpen</c>, <c>ComBusy</c>, <c>SectionLocked</c>, <c>StaleWrite</c>).
        /// </summary>
        private static bool OneNoteMayHaveRefusedFiles(OneNoteException ex) =>
            !WritePipeline.IsAmbiguous(ex)
            && ex.Code is not (ErrorCode.ModalDialogOpen or ErrorCode.ComBusy or ErrorCode.SectionLocked or ErrorCode.StaleWrite);

        private List<ToolWarning> Warnings(RestoredPageXml sent, int refused)
        {
            var warnings = new List<ToolWarning>();
            var counts = new Dictionary<string, int>(sent.Dropped, StringComparer.Ordinal);
            var missingImages = Entry.MissingBinary.Count(m => m.Kind == MissingBinary.ImageKind);
            if (missingImages > 0)
            {
                counts[RestoredPage.ImageKind] = counts.GetValueOrDefault(RestoredPage.ImageKind) + missingImages;
            }

            if (counts.Values.Sum() > 0)
            {
                string[] order = [RestoredPage.ImageKind, RestoredPage.InkKind, RestoredPage.AttachmentKind, RestoredPage.MediaKind, RestoredPage.OtherKind];
                var parts = order.Where(k => counts.GetValueOrDefault(k) > 0)
                    .Select(k => string.Create(CultureInfo.InvariantCulture, $"{counts[k]} {k}"));
                var message = $"The page was restored without some of its binaries ({string.Join(", ", parts)}).";
                if (refused > 0)
                {
                    message += string.Create(CultureInfo.InvariantCulture, $" OneNote refused the {refused} attachment or media file(s) sent from their temporary copies, so the page was written without them.");
                }

                warnings.Add(new ToolWarning(ToolWarnings.BinaryMissing, message, MissingBinaryAdvice(counts)));
            }

            if (fallback is { } used)
            {
                warnings.Add(new ToolWarning(
                    ToolWarnings.RestoredElsewhere,
                    used == RecoveredSectionName
                        ? $"The entry's section is gone, so the page was restored into the notebook's {RecoveredSectionName} section."
                        : "The entry's section ID is gone, so the page was restored into the section at the entry's parentNamePath.",
                    "meta.parentId is the section the page is in now."));
            }

            return warnings;
        }

        /// <summary>binaryMissing's userAction: one sentence per group of kinds actually missing, so the advice matches the counts.</summary>
        private static string MissingBinaryAdvice(Dictionary<string, int> counts)
        {
            bool Any(params string[] kinds) => kinds.Any(k => counts.GetValueOrDefault(k) > 0);
            var advice = new List<string>();
            if (Any(RestoredPage.ImageKind, RestoredPage.InkKind))
            {
                advice.Add("Images and ink missing from the snapshot weren't on this PC when it was taken; re-insert any you still have.");
            }

            if (Any(RestoredPage.AttachmentKind, RestoredPage.MediaKind))
            {
                advice.Add("Attachments and media come back only while OneNote's temporary copy still exists; re-insert any that are missing from the original files.");
            }

            if (Any(RestoredPage.OtherKind))
            {
                advice.Add("Other embedded content, such as HTML blocks and printouts, isn't restored; add it back by hand if you need it.");
            }

            return string.Join(" ", advice);
        }
    }
}
