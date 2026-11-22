using System.Globalization;
using System.Runtime.Versioning;
using System.Xml.Linq;
using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Core.Writes;
using OneNoteMcp.Xml;

namespace OneNoteMcp.Core.UseCases;

/// <summary>
/// <c>open_package</c> (story 3.8; AD-5 promoted task, AD-7 <c>ExternalWrite</c>): unpacks a OneNote package into a new
/// folder in the opened-copies folder (<see cref="OpenedCopies"/>; Phill's Q3 (a), 2026-11-18) and opens it, through the
/// write pipeline. Spike 3.1 (O1–O5, Implication 10) and the 3.8
/// sitting S0 (P1, P2): <c>OpenPackage</c> unpacks into the folder it is given and opens nothing; <c>OpenHierarchy</c> of
/// that folder then opens a copy with its own ID beside the original, and its sections and pages list a few seconds later.
/// <list type="number">
/// <item>Step 1, before the lock: <c>package</c> must be a fully qualified local path to an existing <c>.onepkg</c> file, in
/// any folder (Phill, Q3); anything else is <see cref="ErrorCode.InvalidArgument"/>. <c>settings.json</c> must be writable.
/// The destination is the caller's folder name or path inside the opened-copies folder (Q3 follow-up (i)), or by default
/// <c>&lt;package name&gt; &lt;yyyyMMdd-HHmmss&gt;</c> (local time) there (Q6), resolved by
/// <see cref="ExportTarget.ResolveFolder"/>; it must not exist. A destination inside a OneDrive folder (the opened-copies
/// folder itself, under a redirected AppData) is <see cref="ErrorCode.ExportPathNotAllowed"/>: OneDrive's client converts
/// a notebook folder unpacked through COM into an online notebook, and OneNote closes (the 3.8 crash probe).</item>
/// <item value="3">Steps 3 and 4, under the lock: the destination resolved again and identical, still absent; then the
/// folder is created and the marker file <see cref="ExportRootGuard.MarkerFileName"/> written into it, so the export-root
/// guard (entry 15, Phill's Q1) never counts the copy as the user's own notebook. A failure there removes the folder.</item>
/// <item value="7">Step 7: <c>OpenPackage</c>, then <c>OpenHierarchy</c> of the folder it reports (which must be the
/// destination), then the page wait: the copy is read at <c>pages</c> scope every poll for up to the wait (5 s; O1 and S0
/// saw 0 at once and all at 5 s), and with no page listed the result warns <c>pagesNotListed</c>, which is expected for a
/// package whose sections are all empty, locked or protected (O5). <c>OpenPackage</c> failing with nothing unpacked
/// removes the folder (only the marker in it); a timeout, or a partial unpack, keeps it and names it. <c>OpenHierarchy</c>
/// failing keeps the folder and says the unpack was done. Nothing is retried.</item>
/// </list>
/// The result is <c>meta.outputPath</c> (the folder) and <c>meta.objectId</c> (the copy's notebook ID). No write grant is
/// given; every section of the copy is protected by its path (entry 15), so reading it needs the user's read grant. The
/// user closes the copy in OneNote (Q13). Messages name IDs and paths (the caller's, or the resolved folder, as
/// <c>meta.outputPath</c> does; backlog 19), never names from the package.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class OpenPackageUseCase : IUseCase
{
    /// <summary>The tool's name (AD-14).</summary>
    public const string OperationName = "open_package";

    /// <summary>The package file's extension.</summary>
    public const string PackageExtension = ".onepkg";

    /// <summary>What to do about a package path this check refuses.</summary>
    public const string PackageUserAction =
        "Pass the full local path (C:\\…) of an existing .onepkg file, such as one publish wrote (its meta.outputPath). No network or device paths.";

    /// <summary>What to do after a step-7 failure that left the folder.</summary>
    public const string CheckTheFolderUserAction =
        "Tell the user the folder at the output path may hold a partly unpacked package. Another try needs a new destination (or none, for a new timestamped folder); the user can delete that folder.";

    /// <summary>What to tell the user on success (the description says it too).</summary>
    public const string CloseUserAction =
        "Tell the user the package is open in OneNote as a separate notebook (meta.objectId), unpacked into meta.outputPath. Its sections count as password-protected: reading them needs the user's read grant. The user closes it in OneNote when done.";

    private static readonly TimeSpan DefaultPoll = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(5);

    private readonly IOneNoteGateway gateway;
    private readonly Func<SettingsSnapshot> settings;
    private readonly WritePipeline pipeline;
    private readonly TimeProvider clock;
    private readonly TimeSpan pollInterval;
    private readonly TimeSpan listWait;
    private readonly Func<OneDriveFolders> oneDrive;

    /// <param name="gateway">Unpacks, opens and reads the copy.</param>
    /// <param name="settings">Reads <c>settings.json</c> once per call, for writability and the opened-copies folder.</param>
    /// <param name="pipeline">The AD-7 step runner.</param>
    /// <param name="clock">The clock the default folder's timestamp comes from (local time); the system clock when null.</param>
    /// <param name="pollInterval">The page wait's poll; 500 ms when null.</param>
    /// <param name="listWait">The page wait; 5 s when null.</param>
    /// <param name="oneDrive">The OneDrive folders on this machine (backlog 13's seam), asked at step 1 and under the lock; none known when null.</param>
    public OpenPackageUseCase(
        IOneNoteGateway gateway,
        Func<SettingsSnapshot> settings,
        WritePipeline pipeline,
        TimeProvider? clock = null,
        TimeSpan? pollInterval = null,
        TimeSpan? listWait = null,
        Func<OneDriveFolders>? oneDrive = null)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(pipeline);
        this.gateway = gateway;
        this.settings = settings;
        this.pipeline = pipeline;
        this.clock = clock ?? TimeProvider.System;
        this.pollInterval = pollInterval ?? DefaultPoll;
        this.listWait = listWait ?? DefaultWait;
        this.oneDrive = oneDrive ?? (() => OneDriveFolders.None);
    }

    public OperationClass Class => OperationClass.ExternalWrite;

    /// <summary>Opens the package at <paramref name="package"/> into <paramref name="destination"/> (or the default folder).</summary>
    public async Task<ToolSuccess> ExecuteAsync(ToolCallContext context, string package, string? destination, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var write = new OpenPackage(this, package ?? string.Empty, destination);
        var opened = await pipeline.RunAsync(context, write, ct).ConfigureAwait(false);
        return new ToolSuccess(null, new ToolMeta(context.CorrelationId, ObjectId: opened.NotebookId, OutputPath: write.Folder), opened.Warnings);
    }

    /// <summary>What step 7 opened: the copy's notebook ID and the warnings.</summary>
    private sealed record Opened(string NotebookId, IReadOnlyList<ToolWarning> Warnings);

    private sealed class OpenPackage(OpenPackageUseCase owner, string package, string? destination) : WriteOperation<Opened>
    {
        private SettingsSnapshot? snapshot;
        private string? packagePath;
        private string? raw;
        private string? folder;
        private bool unpacked;
        private string? openedId;

        public override OperationClass Class => OperationClass.ExternalWrite;

        /// <summary>The destination folder, canonical, once step 1 resolved it.</summary>
        public string? Folder => folder;

        public override string? FailureNote => folder is null
            ? null
            : openedId is not null
                ? $"The package was unpacked into {folder} and opened as notebook {openedId}."
                : unpacked
                    ? $"The package was unpacked into {folder}; opening it there failed."
                    : $"The output path is {folder}.";

        public override async Task AuthorizeAsync(CancellationToken ct)
        {
            packagePath = CheckPackage(package);
            snapshot = owner.settings();
            snapshot.EnsureWritable();
            raw = string.IsNullOrWhiteSpace(destination) ? DefaultName(packagePath) : destination;
            RefuseOneDrive(snapshot.OpenedCopies, snapshot.OpenedCopies);
            folder = ExportTarget.ResolveFolder(raw, snapshot.OpenedCopies);
            RefuseOneDrive(folder, snapshot.OpenedCopies);
            await RefuseInsideAnOpenNotebookAsync(ct).ConfigureAwait(false);
        }

        /// <summary>Phill's Q3 (a): nothing is unpacked inside a OneDrive folder, whose client would convert the copy.</summary>
        private void RefuseOneDrive(string path, string openedCopies)
        {
            if (owner.oneDrive().Holds(path))
            {
                throw new OneNoteException(
                    ErrorCode.ExportPathNotAllowed,
                    $"The destination {path} is inside OneDrive, whose sync client converts an unpacked notebook into an online one while OneNote has it open, and OneNote then closes; so nothing was opened.",
                    userAction: $"Copies are opened outside OneDrive only. The opened-copies folder is {openedCopies}; if that is inside OneDrive (a redirected AppData folder), tell the user open_package can't be used on this machine. They can open the package themselves in OneNote (File > Open), which uploads it as an online notebook.",
                    retryable: false);
            }
        }

        /// <summary>
        /// The 3.8 review: a destination inside an open notebook's folder (an earlier copy's, say) would unpack into that
        /// notebook, where OneNote reads a subfolder as a section group, so it is <see cref="ErrorCode.ExportPathNotAllowed"/>.
        /// A failed read of the notebooks gives no verdict: the call itself meets the same error later.
        /// </summary>
        private async Task RefuseInsideAnOpenNotebookAsync(CancellationToken ct)
        {
            IReadOnlyList<string> notebooks;
            try
            {
                var xml = await owner.gateway.GetHierarchyAsync(string.Empty, HierarchyScope.Notebooks, ct).ConfigureAwait(false);
                notebooks = [.. HierarchyNode.Notebooks(xml).Select(n => PathUnder.Full(n.Path)).OfType<string>()];
            }
            catch (Exception ex) when (ex is OneNoteException or OneNoteXmlException)
            {
                return;
            }

            if (notebooks.Any(n => PathUnder.IsUnder(folder, [n])))
            {
                throw new OneNoteException(
                    ErrorCode.ExportPathNotAllowed,
                    $"The destination {raw} is inside an open notebook's folder, so nothing was opened.",
                    userAction: "Choose a destination outside every open notebook's folder, or leave destination out for a new folder named after the package.",
                    retryable: false);
            }
        }

        public override Task ReadAsync(CancellationToken ct) => Task.CompletedTask;

        public override void Validate()
        {
            // The package may have gone since step 1; the folder must still be the same and still absent.
            CheckPackage(package);
            RefuseOneDrive(snapshot!.OpenedCopies, snapshot.OpenedCopies);
            var again = ExportTarget.ResolveFolder(raw!, snapshot.OpenedCopies);
            RefuseOneDrive(again, snapshot.OpenedCopies);
            if (!string.Equals(again, folder, StringComparison.OrdinalIgnoreCase))
            {
                throw new OneNoteException(
                    ErrorCode.ExportPathNotAllowed,
                    $"The destination {raw} now leads somewhere else than {folder}, so nothing was opened.",
                    userAction: ExportTarget.NotAllowedUserAction,
                    retryable: false);
            }

            // The folder, then the marker before anything is unpacked: the guard must never see the copy without it. A
            // folder that appeared since the re-resolve is never unpacked into (CreateNew fails on an existing marker).
            if (Directory.Exists(folder!) || File.Exists(folder!))
            {
                throw new OneNoteException(
                    ErrorCode.FileExists,
                    $"Something already exists at {folder}, so nothing was opened.",
                    userAction: "Nothing is opened into an existing folder: choose a new folder name, or leave destination out.",
                    retryable: false);
            }

            try
            {
                Directory.CreateDirectory(folder!);
                using (new FileStream(Path.Combine(folder!, ExportRootGuard.MarkerFileName), FileMode.CreateNew, FileAccess.Write))
                {
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                RemoveIfOnlyTheMarker();

                // No path and no inner exception: Internal errors are logged in full, and paths never reach the log (AD-13).
                throw new OneNoteException(
                    ErrorCode.Internal,
                    $"The destination folder couldn't be prepared ({ex.GetType().Name}), so nothing was opened.",
                    userAction: $"Check that the opened-copies folder ({snapshot.OpenedCopies}) exists or can be created, and that the user can write to it.");
            }
        }

        public override async Task<Opened> CallAsync(CancellationToken ct)
        {
            var target = folder ?? throw new InvalidOperationException("Step 1 hasn't resolved the folder.");
            string pathOut;
            try
            {
                pathOut = await owner.gateway.OpenPackageAsync(packagePath!, target, ct).ConfigureAwait(false);
            }
            catch (OneNoteException ex) when (!ex.OutcomeUnknown && !WritePipeline.IsAmbiguous(ex) && RemoveIfOnlyTheMarker())
            {
                // Nothing was unpacked: the folder was the pipeline's own, and it is gone again.
                throw new OneNoteException(ex.Code, $"{ex.Message} Nothing was unpacked; the empty folder {target} was removed.", ex.ComHResult, userAction: ex.UserAction, retryable: ex.Retryable);
            }
            catch (OneNoteException ex)
            {
                // A timeout, or a failure that left files: the folder stays, and the error names it (AD-11).
                throw new OneNoteException(ex.Code, $"{ex.Message} The output path is {target}; it may hold a partly unpacked package.", ex.ComHResult, ex, ex.OutcomeUnknown, CheckTheFolderUserAction, ex.Retryable);
            }

            unpacked = true;
            if (!string.Equals(pathOut?.TrimEnd('\\'), target.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                throw new OneNoteException(
                    ErrorCode.Internal,
                    $"OpenPackage reported a path other than the destination, so the copy wasn't opened. {FailureNote}",
                    userAction: CheckTheFolderUserAction);
            }

            string id;
            try
            {
                id = await owner.gateway.OpenHierarchyAsync(target, ct).ConfigureAwait(false);
            }
            catch (OneNoteException ex)
            {
                // The unpack is done; the gateway's own userAction (close a dialog, say) still fits, so it is kept.
                throw new OneNoteException(
                    ex.Code,
                    $"{ex.Message} The package was unpacked into {target}; opening it there failed.",
                    ex.ComHResult,
                    ex,
                    ex.OutcomeUnknown,
                    ex.UserAction ?? "The package is unpacked in the output path's folder; the user can open it in OneNote (File > Open), or delete the folder and try again with a new destination.",
                    ex.Retryable);
            }

            openedId = id;
            return new Opened(id, await PageWaitAsync(id, ct).ConfigureAwait(false));
        }

        /// <summary>The page wait (Design Notes): a <c>pages</c> read every poll until a page is listed or the wait ends.</summary>
        private async Task<IReadOnlyList<ToolWarning>> PageWaitAsync(string id, CancellationToken ct)
        {
            var started = owner.clock.GetTimestamp();
            while (true)
            {
                try
                {
                    var xml = await owner.gateway.GetHierarchyAsync(id, HierarchyScope.Pages, ct).ConfigureAwait(false);
                    if (XDocument.Parse(xml).Descendants(OneNoteXml.One + "Page").Any())
                    {
                        return [];
                    }
                }
                catch (Exception ex) when (ex is OneNoteException or System.Xml.XmlException)
                {
                    // A copy that can't be read yet counts as not listed; the open itself succeeded.
                }
                catch (OperationCanceledException)
                {
                    // The open succeeded: a cancel or the deadline during the wait only ends the wait (the 3.8 review).
                    return NotListed(id);
                }

                if (owner.clock.GetElapsedTime(started) >= owner.listWait)
                {
                    return
                    [
                        new ToolWarning(
                            ToolWarnings.PagesNotListed,
                            $"Notebook {id} is open, but no page was listed within {owner.listWait.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s. That is expected when every section is empty, locked or password-protected; otherwise its pages list a little later.",
                            "Read the notebook again with get_hierarchy in a few seconds; its sections need the user's read grant to show their pages."),
                    ];
                }

                try
                {
                    await Task.Delay(owner.pollInterval, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return NotListed(id);
                }
            }
        }

        private IReadOnlyList<ToolWarning> NotListed(string id) =>
        [
            new ToolWarning(
                ToolWarnings.PagesNotListed,
                $"Notebook {id} is open, but no page was listed within the wait. That is expected when every section is empty, locked or password-protected; otherwise its pages list a little later.",
                "Read the notebook again with get_hierarchy in a few seconds; its sections need the user's read grant to show their pages."),
        ];

        /// <summary>Removes the folder when it holds nothing but the marker (or nothing); true when it is gone.</summary>
        private bool RemoveIfOnlyTheMarker()
        {
            if (folder is null)
            {
                return false;
            }

            try
            {
                if (!Directory.Exists(folder))
                {
                    return true;
                }

                var entries = Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName).ToList();
                if (entries.All(e => string.Equals(e, ExportRootGuard.MarkerFileName, StringComparison.OrdinalIgnoreCase)))
                {
                    Directory.Delete(folder, recursive: true);
                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Kept: the error then names the folder, as for any partial unpack.
            }

            return false;
        }

        /// <summary>The package's canonical path, or <see cref="ErrorCode.InvalidArgument"/>.</summary>
        private static string CheckPackage(string package)
        {
            if (string.IsNullOrWhiteSpace(package))
            {
                throw InvalidPackage("The argument package is empty, so nothing was opened.");
            }

            if (!Path.IsPathFullyQualified(package) || package.StartsWith(@"\\", StringComparison.Ordinal) || package.StartsWith("//", StringComparison.Ordinal))
            {
                throw InvalidPackage($"The package {package} isn't a full local path (C:\\…), so nothing was opened.");
            }

            if (!package.EndsWith(PackageExtension, StringComparison.OrdinalIgnoreCase))
            {
                throw InvalidPackage($"The package {package} doesn't end in {PackageExtension}, so nothing was opened.");
            }

            if (FilePathGate.Canonical(package, out _) is not { } canonical)
            {
                throw InvalidPackage($"The package {package} isn't a local file path, or leads to a network or device path, so nothing was opened.");
            }

            if (!canonical.EndsWith(PackageExtension, StringComparison.OrdinalIgnoreCase) || !File.Exists(canonical))
            {
                throw InvalidPackage($"There is no package file at {package}, so nothing was opened.");
            }

            return canonical;
        }

        /// <summary>Phill's Q6: the package's name and a local timestamp, so a folder name is never reused.</summary>
        private string DefaultName(string canonicalPackage) =>
            $"{Path.GetFileNameWithoutExtension(canonicalPackage)} {owner.clock.GetLocalNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}";

        private static OneNoteException InvalidPackage(string message) =>
            new(ErrorCode.InvalidArgument, message, userAction: PackageUserAction, retryable: false);
    }
}
