using System.Runtime.Versioning;
using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Core.Writes;
using OneNoteMcp.Xml;

namespace OneNoteMcp.Core.UseCases;

/// <summary>
/// <c>publish</c> (stories 3.3, 3.13 and 3.7): <c>Publish</c> of a page or a section in any <see cref="ExportFormat"/>, or of
/// a notebook as a package only (AD-7 Formats by scope), to a new file under an export root, through the write pipeline (AD-7) as one <c>ExternalWrite</c>.
/// No write grant and no dialog of any kind.
/// <list type="number">
/// <item>Step 1, before the lock (<see cref="PublishTarget.AuthorizeAsync"/>): an empty <c>objectId</c> is
/// <see cref="ErrorCode.InvalidArgument"/>; a format outside the enum is <see cref="ErrorCode.NotSupported"/>. A page's
/// owning section is resolved through the walk first, because OneNote hides a locked section's pages from
/// <c>GetHierarchy</c>; only when there is none is the target read at <c>self</c>: a section is exported itself, a notebook
/// too but only as a package (any other format is <see cref="ErrorCode.NotSupported"/>, spike 3.1 X1), a section group is
/// <see cref="ErrorCode.NotSupported"/> (epic 3 decision Q7), anything else <see cref="ErrorCode.ObjectNotFound"/>. <c>settings.json</c> must be writable
/// (<see cref="SettingsSnapshot.EnsureWritable"/>). The path is resolved and checked by <see cref="ExportTarget.Resolve"/>.
/// In a password-protected section a read grant held at the call's start is required, never prompted for (AD-21 Exports), else
/// <see cref="ErrorCode.ReadNotGranted"/>. A notebook package needs no read grant (AD-21 Exports, 3.2 plan H1): it keeps
/// every protected section encrypted, locked or not (spike 3.1 XP1, XP2). But a notebook holding a locked section anywhere
/// is <see cref="ErrorCode.SectionLocked"/>, and one holding a section with <c>areAllPagesAvailable</c> false is
/// <see cref="ErrorCode.NotSupported"/>: packing either raises a modal dialog in OneNote that blocks the call, and a Yes to
/// it drops those pages (the 3.7 sitting, S1 and S3; Phill, 2026-11-17). The flag marks a page stub added from another
/// computer that never synced, which nothing on this PC can download (the backlog 10 sitting).</item>
/// <item value="3">Steps 3 and 4, under the lock: a page is walked to its section again, a section re-read (locked is
/// <see cref="ErrorCode.SectionLocked"/>; protected since step 1 needs the read grant too), a notebook re-read (it must
/// still be one, else <see cref="ErrorCode.ObjectNotFound"/>, and hold no locked section and no unsynced page, as in
/// step 1), the path is resolved again and
/// must come out the same, with nothing at it (<see cref="ErrorCode.FileExists"/>), and the target's folder is created,
/// because <c>Publish</c> into a missing folder raises a modal and blocks.</item>
/// <item value="7">Step 7: <c>Publish</c> in the format, then the landing wait (<see cref="PublishedFile"/>) within
/// <c>timeouts.publishSeconds</c> of the call's start, so the write counts as done only once the file is on disk. A wait
/// that runs out is <see cref="ErrorCode.Timeout"/>, which the pipeline makes <c>outcomeUnknown</c>. Every step-7
/// failure names the output path (AD-11), through <see cref="WriteOperation{TResult}.FailureNote"/>.</item>
/// </list>
/// The result is <c>meta.objectId</c> and <c>meta.outputPath</c> (canonical), with the warning <c>exportNotEncrypted</c> for
/// a protected section or a page in one (a section's package too: H1 relaxes only notebook packages, which never warn). A section as EMF is
/// refused with <see cref="ErrorCode.NotSupported"/> in step 1, before any write: the 3.13 sitting found that it holds only
/// the first page (Phill, 2026-11-17). Messages name IDs and the caller's own output path, never a name or content (AD-9, AD-21).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PublishUseCase : IUseCase
{
    /// <summary>The tool's name (AD-14: the COM method's).</summary>
    public const string OperationName = "publish";

    /// <summary>What to do with an empty or unknown ID.</summary>
    public const string ObjectIdUserAction =
        "Pass a page's, a section's or a notebook's ID (get_hierarchy lists them).";

    /// <summary>What to do with a section group (epic 3 decision Q7).</summary>
    public const string GroupUserAction =
        "publish can't export a section group. Export its sections one by one (get_hierarchy with scope sections lists them), into one folder if the user wants them together.";

    /// <summary>What to do with a notebook in a format other than a package (spike 3.1 X1; Phill, 3.7 plan Q1).</summary>
    public const string NotebookUserAction =
        "A notebook exports only as a OneNote package: call publish again with format onepkg and a path ending in .onepkg. For another format, export the notebook's sections one by one (get_hierarchy with scope sections lists them).";

    /// <summary>What to do with a notebook holding a locked section (the 3.7 sitting: packing one raises a blocking dialog).</summary>
    public const string NotebookLockedUserAction =
        "A notebook with a locked section can't be packaged: unlock it, or export the other sections one by one (get_hierarchy with scope sections lists them).";

    /// <summary>
    /// What to do with a notebook holding a section with <c>areAllPagesAvailable</c> false (the 3.7 sitting S3: the pack
    /// dialog): a page there was added on another computer and never synced (the backlog 10 sitting; backlog 11).
    /// </summary>
    public const string NotebookNotDownloadedUserAction =
        "A page in that section was added on another computer and hasn't synced, so this PC can't download it and packing would leave it out. Sync it from the computer that added it, or delete its placeholder page in OneNote, then try again; or export the other sections one by one (get_hierarchy with scope sections lists them).";

    /// <summary>What to do with a section as EMF (the 3.13 sitting: it holds only the first page).</summary>
    public const string SectionEmfUserAction =
        "A section's EMF holds only its first page, so publish refuses it. Export each page as EMF (get_hierarchy with scope pages lists them), or the section as PDF.";

    /// <summary>What to do without the read grant a protected page's export needs (AD-21 Exports).</summary>
    public const string ReadFirstUserAction =
        "Exporting a page from a password-protected section needs a read grant for the section, held when the call starts. Read the page "
        + "with get_page_content first (it asks the user for the grant), then call publish again. Never run the grant command yourself.";

    /// <summary>The <c>exportNotEncrypted</c> warning's <c>userAction</c>.</summary>
    public const string NotEncryptedUserAction =
        "Tell the user that the exported file is not password-protected, and where it is (meta.outputPath).";

    /// <summary>What to do after a step-7 failure that may have left a file (AD-11).</summary>
    public const string CheckTheFileUserAction =
        "Check whether the file at the output path is there and complete. A retry needs a new file name, or the user deletes that file first: nothing overwrites a file.";

    private readonly IOneNoteGateway gateway;
    private readonly ReadAccess readAccess;
    private readonly Func<SettingsSnapshot> settings;
    private readonly WritePipeline pipeline;
    private readonly TimeProvider clock;
    private readonly TimeSpan pollInterval;

    /// <param name="gateway">Reads the hierarchy and publishes.</param>
    /// <param name="readAccess">The non-prompting read-grant check for a protected page (AD-21).</param>
    /// <param name="settings">Reads <c>settings.json</c> (export roots, <c>timeouts.publishSeconds</c>), once per call.</param>
    /// <param name="pipeline">The AD-7 step runner.</param>
    /// <param name="clock">The clock the landing wait runs on; the system clock when null.</param>
    /// <param name="pollInterval">The gap between the landing wait's polls; <see cref="PublishedFile.DefaultPollInterval"/> when null.</param>
    public PublishUseCase(
        IOneNoteGateway gateway,
        ReadAccess readAccess,
        Func<SettingsSnapshot> settings,
        WritePipeline pipeline,
        TimeProvider? clock = null,
        TimeSpan? pollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(readAccess);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(pipeline);
        this.gateway = gateway;
        this.readAccess = readAccess;
        this.settings = settings;
        this.pipeline = pipeline;
        this.clock = clock ?? TimeProvider.System;
        this.pollInterval = pollInterval ?? PublishedFile.DefaultPollInterval;
    }

    public OperationClass Class => OperationClass.ExternalWrite;

    /// <summary>Exports page, section or notebook <paramref name="objectId"/> in <paramref name="format"/> to <paramref name="path"/>.</summary>
    public async Task<ToolSuccess> ExecuteAsync(ToolCallContext context, string objectId, string path, ExportFormat format, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var write = new PublishTarget(this, objectId ?? string.Empty, path ?? string.Empty, format, readAccess.Now());
        var warnings = await pipeline.RunAsync(context, write, ct).ConfigureAwait(false);
        return new ToolSuccess(null, new ToolMeta(context.CorrelationId, ObjectId: objectId, OutputPath: write.OutputPath), warnings);
    }

    /// <summary>The format's name in messages and warnings.</summary>
    internal static string FormatName(ExportFormat format) => format switch
    {
        ExportFormat.OneNote => "OneNote (.one) file",
        ExportFormat.Pdf => "PDF",
        ExportFormat.Xps => "XPS file",
        ExportFormat.Word => "Word document",
        ExportFormat.Mhtml => "MHTML file",
        ExportFormat.Html => "web page",
        ExportFormat.Emf => "EMF",
        ExportFormat.OneNotePackage => "OneNote package",
        _ => "file",
    };

    /// <summary>What a <c>publish</c> exports, as step 1 found it.</summary>
    private enum TargetKind
    {
        Page,
        Section,
        Notebook,
    }

    /// <summary>One page's, section's or notebook's export as an <c>ExternalWrite</c> (AD-7), in the order the class summary gives.</summary>
    private sealed class PublishTarget(PublishUseCase owner, string objectId, string path, ExportFormat format, DateTimeOffset callStart)
        : WriteOperation<IReadOnlyList<ToolWarning>>
    {
        private readonly List<ToolWarning> warnings = [];
        private SettingsSnapshot? snapshot;
        private HierarchyNode? section;
        private HierarchyNode? current;
        private string? target;
        private TargetKind kind = TargetKind.Page;
        private bool warnedNotEncrypted;

        /// <summary>"page {id}", "section {id}" or "notebook {id}", for messages.</summary>
        private string What => kind switch
        {
            TargetKind.Section => $"section {objectId}",
            TargetKind.Notebook => $"notebook {objectId}",
            _ => $"page {objectId}",
        };

        public override OperationClass Class => OperationClass.ExternalWrite;

        /// <summary>The canonical output path, once step 1 resolved it.</summary>
        public string? OutputPath => target;

        /// <summary>Every step-7 failure, the pipeline's own deadline and cancel included, names the output path (AD-11).</summary>
        public override string? FailureNote => target is null ? null : $"The output path is {target}.";

        public override async Task AuthorizeAsync(CancellationToken ct)
        {
            if (objectId.Length == 0)
            {
                throw new OneNoteException(
                    ErrorCode.InvalidArgument,
                    "The argument objectId is empty, so nothing was read or exported.",
                    userAction: ObjectIdUserAction,
                    retryable: false);
            }

            // Only the enum's members reach COM (AD-11: pfOneNote2007 has none, and the gateway refuses it again).
            if (!Enum.IsDefined(format))
            {
                throw new OneNoteException(
                    ErrorCode.NotSupported,
                    "That export format isn't supported, so nothing was exported.",
                    userAction: "Pass one of the formats publish lists.",
                    retryable: false);
            }

            // The owning section first, through the walk (AD-6), as PageWrite does: OneNote hides a locked section's pages,
            // so GetHierarchy of the page itself fails while its parent chain still answers (the 3.3 sitting, 2026-11-17).
            // Only a page has a section as its parent, so a found section also settles the target's kind; with none, the
            // target is read at self, and only a section goes on.
            section = await owner.readAccess.OwningSectionAsync(objectId, ct).ConfigureAwait(false);
            if (section is null)
            {
                section = await TheTargetItselfAsync(ct).ConfigureAwait(false);
                kind = section.Kind == HierarchyKind.Notebook ? TargetKind.Notebook : TargetKind.Section;

                // Spike 3.1 X1: a notebook publishes only as a package; every other format failed or isn't offered.
                if (kind == TargetKind.Notebook && format != ExportFormat.OneNotePackage)
                {
                    throw new OneNoteException(
                        ErrorCode.NotSupported,
                        $"Notebook {objectId} can be exported only as a OneNote package, not as a {FormatName(format)}, so nothing was exported.",
                        userAction: NotebookUserAction,
                        retryable: false);
                }

                // The 3.7 sitting (Phill, 2026-11-17): packing a notebook with a locked section, or one with an unsynced page,
                // raises a modal dialog that blocks the call until the user answers, so such a section refuses the package first.
                if (kind == TargetKind.Notebook)
                {
                    await RefuseASectionThatWouldBlockThePackageAsync(ct).ConfigureAwait(false);
                }

                // The 3.13 sitting (Phill, 2026-11-17): a section's EMF holds only its first page, so it is refused.
                if (kind == TargetKind.Section && format == ExportFormat.Emf)
                {
                    throw new OneNoteException(
                        ErrorCode.NotSupported,
                        $"Section {objectId} can't be exported as EMF, so nothing was exported.",
                        userAction: SectionEmfUserAction,
                        retryable: false);
                }
            }

            // settings.json must be writable (AD-10), and the export roots come from the same read.
            snapshot = owner.settings();
            snapshot.EnsureWritable();
            target = ExportTarget.Resolve(path, snapshot.ExportRoots, format);

            // AD-21 Exports: the section's read grant, held now, never prompted. A notebook package needs none (H1).
            if (kind != TargetKind.Notebook)
            {
                CheckReadGrant(section);
            }
        }

        public override async Task ReadAsync(CancellationToken ct)
        {
            if (kind != TargetKind.Page)
            {
                // The section or notebook again under the lock: a section's ID survives a move, and it may have been
                // protected or locked since; a notebook may have been closed.
                var notebook = kind == TargetKind.Notebook;
                current = await SelfAsync(ct).ConfigureAwait(false);
                if (current.Kind != (notebook ? HierarchyKind.Notebook : HierarchyKind.Section))
                {
                    throw new OneNoteException(
                        ErrorCode.ObjectNotFound,
                        notebook ? $"Notebook {objectId} wasn't found, so nothing was exported." : $"Section {objectId} wasn't found, so nothing was exported.",
                        userAction: notebook
                            ? "Re-read the hierarchy (get_hierarchy) for a current notebook ID."
                            : "Re-read the hierarchy (get_hierarchy) for a current section ID.");
                }

                // A section of the notebook may have been locked, or gained an unsynced page, since step 1.
                if (notebook)
                {
                    await RefuseASectionThatWouldBlockThePackageAsync(ct).ConfigureAwait(false);
                }

                return;
            }

            // The page is walked again under the lock: it may have moved to another section since step 1.
            current = await owner.readAccess.OwningSectionAsync(objectId, ct).ConfigureAwait(false)
                ?? throw new OneNoteException(
                    ErrorCode.ObjectNotFound,
                    $"Page {objectId} isn't inside a section, so nothing was exported.",
                    userAction: "Re-read the hierarchy (get_hierarchy) for a current page ID.");
        }

        public override void Validate()
        {
            var now = current ?? throw new InvalidOperationException("Validate runs after ReadAsync.");

            // A section protected since step 1 needs the grant too; the protected check comes before the locked one (AD-21).
            // A notebook has neither check on its own node: its package keeps protected sections encrypted (H1), and its
            // sections' locked state was checked in ReadAsync (the 3.7 sitting).
            if (kind != TargetKind.Notebook)
            {
                CheckReadGrant(now);
                if (now.Locked)
                {
                    throw new OneNoteException(
                        ErrorCode.SectionLocked,
                        kind == TargetKind.Section ? $"Section {now.Id} is locked, so it wasn't exported." : $"Section {now.Id} is locked, so page {objectId} wasn't exported.");
                }
            }

            // The path again, under the lock: still inside a root, still the same file, still nothing there.
            var again = ExportTarget.Resolve(path, snapshot!.ExportRoots, format);
            if (!string.Equals(again, target, StringComparison.OrdinalIgnoreCase))
            {
                throw new OneNoteException(
                    ErrorCode.ExportPathNotAllowed,
                    $"The path {path} now leads somewhere else than {target}, so nothing was exported.",
                    userAction: ExportTarget.NotAllowedUserAction,
                    retryable: false);
            }

            // Publish into a missing folder raises a modal and blocks (AD-7), so the folder is made first.
            var folder = Path.GetDirectoryName(target!) ?? throw new InvalidOperationException("The target has no folder.");
            try
            {
                Directory.CreateDirectory(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // No path and no inner exception: Internal errors are logged in full, and paths never reach the log (AD-13).
                throw new OneNoteException(
                    ErrorCode.Internal,
                    $"The output file's folder couldn't be created ({ex.GetType().Name}), so {What} wasn't exported.",
                    userAction: "Check that the export root exists or can be created, and that the user can write to it; or choose another path under it.");
            }
        }

        public override async Task<IReadOnlyList<ToolWarning>> CallAsync(CancellationToken ct)
        {
            var file = target ?? throw new InvalidOperationException("Step 1 hasn't resolved the target.");
            var limit = snapshot!.Timeouts.Publish;
            await CallNamingFailureNoteAsync(async () =>
            {
                var started = owner.clock.GetTimestamp();
                await owner.gateway.PublishAsync(objectId, file, format, ct).ConfigureAwait(false);

                // Done means on disk (AD-7): within publishSeconds of the call's start, or Timeout (outcomeUnknown).
                if (await PublishedFile.WaitAsync(file, started, limit, owner.pollInterval, owner.clock, ct).ConfigureAwait(false) is { } failure)
                {
                    throw new OneNoteException(
                        ErrorCode.Timeout,
                        $"OneNote's {FormatName(format)} of {What} didn't land within timeouts.publishSeconds: {PublishedFile.Describe(failure)}.",
                        userAction: CheckTheFileUserAction);
                }
            }).ConfigureAwait(false);
            return warnings;
        }

        /// <summary>
        /// The target read at <c>self</c>, when the walk found no owning section: a section or a notebook is returned; a
        /// section group is <see cref="ErrorCode.NotSupported"/>; anything else (a page outside a section) is
        /// <see cref="ErrorCode.ObjectNotFound"/>.
        /// </summary>
        private async Task<HierarchyNode> TheTargetItselfAsync(CancellationToken ct)
        {
            var self = await SelfAsync(ct).ConfigureAwait(false);
            return self.Kind switch
            {
                HierarchyKind.Section or HierarchyKind.Notebook => self,
                HierarchyKind.SectionGroup => throw new OneNoteException(
                    ErrorCode.NotSupported,
                    $"Object {objectId} is a section group, which OneNote can't export, so nothing was exported.",
                    userAction: GroupUserAction,
                    retryable: false),
                _ => throw new OneNoteException(
                    ErrorCode.ObjectNotFound,
                    $"Object {objectId} isn't a page in a section, a section or a notebook, so nothing was exported.",
                    userAction: "Re-read the hierarchy (get_hierarchy) for a current page, section or notebook ID."),
            };
        }

        /// <summary>
        /// A notebook's sections, at any depth, read live. Packing a notebook raises OneNote's modal "cannot pack these
        /// sections" dialog, which blocks <c>Publish</c>, for a locked section or one with an unsynced page (the 3.7 sitting), so:
        /// the first locked section is <see cref="ErrorCode.SectionLocked"/> (S1); else the first with
        /// <c>areAllPagesAvailable</c> false is <see cref="ErrorCode.NotSupported"/> (S3: a Yes to the dialog writes the
        /// package without those pages; the flag marks a page stub that never synced from the computer that added it). Each
        /// names the section's ID only. A marked section needs no read grant (H1: the package keeps it encrypted); a section
        /// protected by its path alone is plaintext in the package, so it needs its read grant held at the call's start, and
        /// the result warns <c>exportNotEncrypted</c> (Phill, 2026-11-17, 3.15 Q3).
        /// </summary>
        private async Task RefuseASectionThatWouldBlockThePackageAsync(CancellationToken ct)
        {
            var xml = await owner.gateway.GetHierarchyAsync(objectId, HierarchyScope.Sections, ct).ConfigureAwait(false);
            var sections = XmlReads.Readable(() => HierarchyNode.Sections(xml), "GetHierarchy");
            if (sections.FirstOrDefault(s => s.Locked) is { } locked)
            {
                throw new OneNoteException(
                    ErrorCode.SectionLocked,
                    $"Section {locked.Id} in notebook {objectId} is locked, so the notebook wasn't exported.",
                    userAction: NotebookLockedUserAction);
            }

            if (sections.FirstOrDefault(s => !s.AllPagesAvailable) is { } partial)
            {
                throw new OneNoteException(
                    ErrorCode.NotSupported,
                    $"A page in section {partial.Id} of notebook {objectId} hasn't synced from the computer that added it, so packing would leave it out, and the notebook wasn't exported.",
                    userAction: NotebookNotDownloadedUserAction,
                    retryable: false);
            }

            var protection = owner.readAccess.Protection();
            foreach (var byPath in sections.Where(protection.ByPathOnly))
            {
                CheckReadGrant(byPath);
            }
        }

        private async Task<HierarchyNode> SelfAsync(CancellationToken ct)
        {
            var selfXml = await owner.gateway.GetHierarchyAsync(objectId, HierarchyScope.Self, ct).ConfigureAwait(false);
            return XmlReads.Readable(() => HierarchyNode.Root(selfXml), "GetHierarchy");
        }

        /// <summary>
        /// In a protected section, the read grant held at the call's start (AD-21 Exports), never prompted for: without it
        /// <see cref="ErrorCode.ReadNotGranted"/>; with it, the <c>exportNotEncrypted</c> warning, once.
        /// </summary>
        private void CheckReadGrant(HierarchyNode node)
        {
            if (!owner.readAccess.Protection().IsProtected(node))
            {
                return;
            }

            if (!owner.readAccess.HoldsReadGrant(node, callStart))
            {
                throw new OneNoteException(
                    ErrorCode.ReadNotGranted,
                    $"Section {node.Id} {Protection.Phrase(node.EncryptedMarker)}, and exporting from it needs a read grant for it held when the call starts, so nothing was exported.",
                    userAction: ReadFirstUserAction);
            }

            if (!warnedNotEncrypted)
            {
                warnedNotEncrypted = true;
                warnings.Add(new ToolWarning(
                    ToolWarnings.ExportNotEncrypted,
                    kind switch
                    {
                        TargetKind.Section => $"Section {objectId} {Protection.Phrase(node.EncryptedMarker)}; the exported {FormatName(format)} isn't encrypted.",
                        TargetKind.Notebook => $"Notebook {objectId} holds section {node.Id}, which {Protection.Phrase(node.EncryptedMarker)}; the exported {FormatName(format)} keeps it unencrypted.",
                        _ => $"Page {objectId} is in section {node.Id}, which {Protection.Phrase(node.EncryptedMarker)}; the exported {FormatName(format)} isn't encrypted.",
                    },
                    NotEncryptedUserAction));
            }
        }
    }
}
