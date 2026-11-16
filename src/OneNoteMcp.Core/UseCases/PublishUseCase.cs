using System.Runtime.Versioning;
using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Core.Writes;
using OneNoteMcp.Xml;

namespace OneNoteMcp.Core.UseCases;

/// <summary>
/// <c>publish</c> (story 3.3, epic 3's tracer): <c>Publish</c> of a page as PDF to a new file under an export root, through
/// the write pipeline (AD-7) as one <c>ExternalWrite</c>. No write grant and no dialog of any kind.
/// <list type="number">
/// <item>Step 1, before the lock (<see cref="PublishPage.AuthorizeAsync"/>): an empty <c>objectId</c> is
/// <see cref="ErrorCode.InvalidArgument"/>; any format but PDF is <see cref="ErrorCode.NotSupported"/>. The target is read
/// at <c>self</c>, and anything but a page is <see cref="ErrorCode.NotSupported"/> ("pages only in this release").
/// <c>settings.json</c> must be writable (<see cref="SettingsSnapshot.EnsureWritable"/>). The path is resolved and checked
/// by <see cref="ExportTarget.Resolve"/>. The page's owning section is resolved through the walk; in a password-protected
/// section a read grant held at the call's start is required, never prompted for (AD-21 Exports), else
/// <see cref="ErrorCode.ReadNotGranted"/>.</item>
/// <item value="3">Steps 3 and 4, under the lock: the page is walked to its section again (locked is
/// <see cref="ErrorCode.SectionLocked"/>; protected since step 1 needs the read grant too), the path is resolved again and
/// must come out the same, with nothing at it (<see cref="ErrorCode.FileExists"/>), and the target's folder is created,
/// because <c>Publish</c> into a missing folder raises a modal and blocks.</item>
/// <item value="7">Step 7: <c>Publish(pfPDF)</c>, then the landing wait (<see cref="PublishedFile"/>) within
/// <c>timeouts.publishSeconds</c> of the call's start, so the write counts as done only once the file is on disk. A wait
/// that runs out is <see cref="ErrorCode.Timeout"/>, which the pipeline makes <c>outcomeUnknown</c>. Every step-7
/// failure names the output path (AD-11), through <see cref="WriteOperation{TResult}.FailureNote"/>.</item>
/// </list>
/// The result is <c>meta.objectId</c> and <c>meta.outputPath</c> (canonical), with the warning <c>exportNotEncrypted</c> for
/// a page in a protected section. Messages name IDs and the caller's own output path, never a name or content (AD-9, AD-21).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PublishUseCase : IUseCase
{
    /// <summary>The tool's name (AD-14: the COM method's).</summary>
    public const string OperationName = "publish";

    /// <summary>What to do with an ID that isn't a page.</summary>
    public const string PagesOnlyUserAction =
        "Pass a page's ID (get_hierarchy with scope pages lists them). To export a section, export its pages one by one.";

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

    /// <summary>Exports page <paramref name="objectId"/> in <paramref name="format"/> to <paramref name="path"/>.</summary>
    public async Task<ToolSuccess> ExecuteAsync(ToolCallContext context, string objectId, string path, ExportFormat format, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var write = new PublishPage(this, objectId ?? string.Empty, path ?? string.Empty, format, readAccess.Now());
        var warnings = await pipeline.RunAsync(context, write, ct).ConfigureAwait(false);
        return new ToolSuccess(null, new ToolMeta(context.CorrelationId, ObjectId: objectId, OutputPath: write.OutputPath), warnings);
    }

    /// <summary>One page's export as an <c>ExternalWrite</c> (AD-7), in the order the class summary gives.</summary>
    private sealed class PublishPage(PublishUseCase owner, string pageId, string path, ExportFormat format, DateTimeOffset callStart)
        : WriteOperation<IReadOnlyList<ToolWarning>>
    {
        private readonly List<ToolWarning> warnings = [];
        private SettingsSnapshot? snapshot;
        private HierarchyNode? section;
        private HierarchyNode? current;
        private string? target;

        public override OperationClass Class => OperationClass.ExternalWrite;

        /// <summary>The canonical output path, once step 1 resolved it.</summary>
        public string? OutputPath => target;

        /// <summary>Every step-7 failure, the pipeline's own deadline and cancel included, names the output path (AD-11).</summary>
        public override string? FailureNote => target is null ? null : $"The output path is {target}.";

        public override async Task AuthorizeAsync(CancellationToken ct)
        {
            if (pageId.Length == 0)
            {
                throw new OneNoteException(
                    ErrorCode.InvalidArgument,
                    "The argument objectId is empty, so nothing was read or exported.",
                    userAction: PagesOnlyUserAction,
                    retryable: false);
            }

            if (format != ExportFormat.Pdf)
            {
                throw new OneNoteException(
                    ErrorCode.NotSupported,
                    "Only PDF exports are supported in this release, so nothing was exported.",
                    userAction: "Export the page as PDF (format pdf).",
                    retryable: false);
            }

            // The target's kind: pages only in this release (Never: no section, section-group or notebook scope).
            var selfXml = await owner.gateway.GetHierarchyAsync(pageId, HierarchyScope.Self, ct).ConfigureAwait(false);
            var self = XmlReads.Readable(() => HierarchyNode.Root(selfXml), "GetHierarchy");
            if (self.Kind != HierarchyKind.Page)
            {
                throw new OneNoteException(
                    ErrorCode.NotSupported,
                    $"Object {pageId} isn't a page, and publish exports pages only in this release, so nothing was exported.",
                    userAction: PagesOnlyUserAction,
                    retryable: false);
            }

            // settings.json must be writable (AD-10), and the export roots come from the same read.
            snapshot = owner.settings();
            snapshot.EnsureWritable();
            target = ExportTarget.Resolve(path, snapshot.ExportRoots);

            // The owning section, through the walk (AD-6), and AD-21 Exports: its read grant, held now, never prompted.
            section = await owner.readAccess.OwningSectionAsync(pageId, ct).ConfigureAwait(false)
                ?? throw new OneNoteException(
                    ErrorCode.ObjectNotFound,
                    $"Page {pageId} isn't inside a section, so nothing was exported.",
                    userAction: "Re-read the hierarchy (get_hierarchy) for a current page ID.");
            CheckReadGrant(section);
        }

        public override async Task ReadAsync(CancellationToken ct)
        {
            // The page is walked again under the lock: it may have moved to another section since step 1.
            current = await owner.readAccess.OwningSectionAsync(pageId, ct).ConfigureAwait(false)
                ?? throw new OneNoteException(
                    ErrorCode.ObjectNotFound,
                    $"Page {pageId} isn't inside a section, so nothing was exported.",
                    userAction: "Re-read the hierarchy (get_hierarchy) for a current page ID.");
        }

        public override void Validate()
        {
            var now = current ?? throw new InvalidOperationException("Validate runs after ReadAsync.");

            // A section protected since step 1 needs the grant too; the protected check comes before the locked one (AD-21).
            CheckReadGrant(now);
            if (now.Locked)
            {
                throw new OneNoteException(ErrorCode.SectionLocked, $"Section {now.Id} is locked, so page {pageId} wasn't exported.");
            }

            // The path again, under the lock: still inside a root, still the same file, still nothing there.
            var again = ExportTarget.Resolve(path, snapshot!.ExportRoots);
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
                    $"The output file's folder couldn't be created ({ex.GetType().Name}), so page {pageId} wasn't exported.",
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
                await owner.gateway.PublishAsync(pageId, file, ExportFormat.Pdf, ct).ConfigureAwait(false);

                // Done means on disk (AD-7): within publishSeconds of the call's start, or Timeout (outcomeUnknown).
                if (await PublishedFile.WaitAsync(file, started, limit, owner.pollInterval, owner.clock, ct).ConfigureAwait(false) is { } failure)
                {
                    throw new OneNoteException(
                        ErrorCode.Timeout,
                        $"OneNote's PDF of page {pageId} didn't land within timeouts.publishSeconds: {PublishedFile.Describe(failure)}.",
                        userAction: CheckTheFileUserAction);
                }
            }).ConfigureAwait(false);
            return warnings;
        }

        /// <summary>
        /// In a protected section, the read grant held at the call's start (AD-21 Exports), never prompted for: without it
        /// <see cref="ErrorCode.ReadNotGranted"/>; with it, the <c>exportNotEncrypted</c> warning, once.
        /// </summary>
        private void CheckReadGrant(HierarchyNode node)
        {
            if (!node.Encrypted)
            {
                return;
            }

            if (!owner.readAccess.HoldsReadGrant(node, callStart))
            {
                throw new OneNoteException(
                    ErrorCode.ReadNotGranted,
                    $"Section {node.Id} is password-protected, and exporting from it needs a read grant for it held when the call starts, so nothing was exported.",
                    userAction: ReadFirstUserAction);
            }

            if (warnings.Count == 0)
            {
                warnings.Add(new ToolWarning(
                    ToolWarnings.ExportNotEncrypted,
                    $"Page {pageId} is in password-protected section {node.Id}; the exported PDF isn't encrypted.",
                    NotEncryptedUserAction));
            }
        }
    }
}
