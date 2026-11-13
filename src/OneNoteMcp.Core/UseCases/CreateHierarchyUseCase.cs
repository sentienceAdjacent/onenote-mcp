using System.Globalization;
using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Core.Writes;
using OneNoteMcp.Xml;

namespace OneNoteMcp.Core.UseCases;

/// <summary>
/// <c>OpenHierarchy</c> creates (story 5.3): a section, a section group or a notebook, each through the write pipeline
/// (AD-7) as a <c>Create</c>.
/// <list type="bullet">
/// <item><b>Section and section group</b>, by parent ID plus a relative name, never an absolute path. Step 1 checks the
/// name (<see cref="SectionFileNames"/>), then the write grant on the parent's notebook, the parent itself when it is a
/// notebook (with AD-6's grant dialog). Step 3 reads the parent at <c>Children</c>; step 4 refuses a parent that isn't a
/// live notebook or section group with <see cref="ErrorCode.ObjectNotFound"/>. A live child of the same kind and name
/// (ignoring case) is returned with the warning <c>alreadyExisted</c> and no COM call; one of the other kind is refused with
/// <see cref="ErrorCode.InvalidArgument"/> (Phill, 2026-11-13, 5.3 Q4). A section's name gets <c>.one</c> unless it ends
/// with it (Q2). The result is <c>meta.objectId</c> and <c>meta.parentId</c> (Q5).</item>
/// <item><b>Notebook</b>, in a local folder the user chose in a dialog (Q3): <c>&lt;folder&gt;\&lt;name&gt;</c>, made
/// canonical. It needs no grant, but <c>settings.json</c> must be writable. Under the lock the server checks the folder is
/// absent or empty (<see cref="ErrorCode.FileExists"/> otherwise), and not inside or around an open notebook or inside
/// OneNoteMcp's own folders (<see cref="ErrorCode.InvalidArgument"/>). The proposed folder, before the dialog, and the
/// folder the user accepted are also refused (<see cref="ErrorCode.InvalidArgument"/>) when they or a folder above them hold
/// a <c>*.onetoc2</c> file, which marks a closed notebook too. After the call, still under the lock, it reads the
/// notebook back at <c>Children</c> scope and writes its write grant (<c>grantedVia: created</c>) under
/// <c>Local\OneNoteMcp.Settings</c>, inside the write lock (AD-7), only when the read-back path's key equals the checked
/// folder's, the ID wasn't open before and the notebook holds no section or section group outside its recycle bin;
/// otherwise the create stands with the warning <c>createdNotGranted</c>. The result is <c>meta.objectId</c> and
/// <c>meta.path</c>. A OneDrive (URL) notebook is <see cref="ErrorCode.NotSupported"/> while
/// <see cref="CloudNotebookCreate"/> is closed.</item>
/// </list>
/// Messages and warnings carry IDs only, never a name or a path (AD-9, AD-13).
/// </summary>
public sealed class CreateHierarchyUseCase : IUseCase
{
    /// <summary>What to do when the parent isn't a live notebook or section group.</summary>
    public const string NotAContainerUserAction =
        "Pass the ID of a notebook or section group that isn't in the recycle bin. Read the hierarchy (get_hierarchy with scope sections) and use a one:Notebook or one:SectionGroup ID.";

    /// <summary>What to do about a name the server refuses.</summary>
    public const string NameUserAction =
        "Use a name of at most 128 characters with no path separators, no '..', no characters a Windows file name can't hold (< > : \" | ? *), no control, invisible or text-direction characters, no leading or trailing space or dot, and not a reserved name such as CON or OneNote_RecycleBin.";

    /// <summary>What to do when the client can't show the notebook-location dialog (story 5.3 build directions, an assumption).</summary>
    public const string NoDialogUserAction =
        "create_notebook asks the user where the notebook goes, and this client can't show that dialog. Ask the user to create the notebook in OneNote (File > New), or to use a client that shows dialogs (Claude Code does).";

    /// <summary>What to do when the user declined or dismissed the location dialog.</summary>
    public const string DeclinedUserAction =
        "The user didn't choose a location, so no notebook was created. Ask the user whether, and where, they want it before trying again.";

    /// <summary>How the user grants writes to a notebook that was created without its automatic grant (AD-6).</summary>
    public static string NotGrantedUserAction { get; } =
        "The notebook was created, but writing to it needs the user's permission, which you can't give. Ask the user whether to grant it: they can run "
        + GrantCli.Run("--write \"<notebook name>\"") + ", or accept the dialog the next write to it shows. Never run the grant command yourself.";

    private readonly IOneNoteGateway gateway;
    private readonly WriteAccess access;
    private readonly WritePipeline pipeline;
    private readonly Func<SettingsSnapshot> settings;
    private readonly SettingsWriter writer;
    private readonly TimeProvider clock;

    /// <param name="gateway">The gateway the creates and their reads go through.</param>
    /// <param name="access">Step 1's write-grant check for a section or group's parent.</param>
    /// <param name="pipeline">The AD-7 step runner.</param>
    /// <param name="settings">Reads <c>settings.json</c>: a notebook create needs it writable, and its folders are OneNoteMcp's own.</param>
    /// <param name="writer">Writes a new notebook's automatic grant.</param>
    /// <param name="clock">Dates the automatic grant.</param>
    public CreateHierarchyUseCase(IOneNoteGateway gateway, WriteAccess access, WritePipeline pipeline, Func<SettingsSnapshot> settings, SettingsWriter writer, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(clock);
        this.gateway = gateway;
        this.access = access;
        this.pipeline = pipeline;
        this.settings = settings;
        this.writer = writer;
        this.clock = clock;
    }

    public OperationClass Class => OperationClass.Create;

    /// <summary><c>create_section</c>: a section named <paramref name="name"/> under the notebook or section group <paramref name="parentId"/>.</summary>
    public Task<ToolSuccess> CreateSectionAsync(ToolCallContext context, string parentId, string name, CancellationToken ct) =>
        CreateInParentAsync(context, HierarchyKind.Section, parentId, name, ct);

    /// <summary><c>create_section_group</c>: a section group named <paramref name="name"/> under the notebook or section group <paramref name="parentId"/>.</summary>
    public Task<ToolSuccess> CreateSectionGroupAsync(ToolCallContext context, string parentId, string name, CancellationToken ct) =>
        CreateInParentAsync(context, HierarchyKind.SectionGroup, parentId, name, ct);

    private async Task<ToolSuccess> CreateInParentAsync(ToolCallContext context, HierarchyKind kind, string parentId, string name, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var operation = new CreateInParent(gateway, access, kind, parentId ?? string.Empty, name ?? string.Empty);
        var (id, existed) = await pipeline.RunAsync(context, operation, ct).ConfigureAwait(false);
        IReadOnlyList<ToolWarning> warnings = existed
            ? [new ToolWarning(ToolWarnings.AlreadyExisted, $"A {Noun(kind)} with this name already exists under {parentId}, so nothing was created: meta.objectId is {id}.")]
            : [];
        return new ToolSuccess(null, new ToolMeta(context.CorrelationId, ParentId: parentId, ObjectId: id), warnings);
    }

    /// <summary>
    /// <c>create_notebook</c>: a notebook named <paramref name="name"/>, in the folder the user chooses in
    /// <paramref name="prompt"/>'s dialog, prefilled with <paramref name="folder"/> or OneNote's default notebook folder.
    /// </summary>
    public async Task<ToolSuccess> CreateNotebookAsync(ToolCallContext context, string name, string? folder, INotebookLocationPrompt? prompt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Step 1, before the lock and outside the deadline: the name, settings writable (the auto-grant must be savable),
        // the proposed folder, then the user's choice of location.
        if (SectionFileNames.NameProblem(name) is { } problem)
        {
            throw BadName("notebook", problem);
        }

        settings().EnsureWritable();

        if (folder is not null)
        {
            // Refused before the dialog only when it could never be accepted; one that doesn't exist yet the user may change.
            CheckNotInANotebook(CheckFolderArgument(folder, "The proposed folder", mustExist: false), "The proposed folder");
        }

        if (prompt is null)
        {
            throw NoDialog();
        }

        var prefill = folder ?? await DefaultFolderAsync(ct).ConfigureAwait(false);
        var answer = await prompt.AskAsync(new NotebookLocationQuestion(name, prefill, CloudNotebookCreate.Enabled), ct).ConfigureAwait(false);
        var parent = Chosen(answer);
        var target = TargetFolder(parent, name);

        var created = await pipeline.RunAsync(context, new CreateNotebook(this, target), ct).ConfigureAwait(false);
        IReadOnlyList<ToolWarning> warnings = created.NotGranted is { } why
            ? [new ToolWarning(ToolWarnings.CreatedNotGranted, $"Notebook {created.Id} was created, but it has no write grant: {why}", NotGrantedUserAction)]
            : [];
        return new ToolSuccess(null, new ToolMeta(context.CorrelationId, Path: created.Path, ObjectId: created.Id), warnings);
    }

    /// <summary>OneNote's default notebook folder when it is a local path, for the dialog's prefill; null (a blank field) otherwise.</summary>
    private async Task<string?> DefaultFolderAsync(CancellationToken ct)
    {
        var path = await gateway.GetSpecialLocationAsync(SpecialLocation.DefaultNotebookFolder, ct).ConfigureAwait(false);
        return NotebookFolders.Normal(path) is not null && FilePathGate.Canonical(path, out _) is not null ? path : null;
    }

    /// <summary>The folder the user chose, checked; it throws for a declined dialog, OneDrive while the seam is closed, or a bad folder.</summary>
    private static string Chosen(NotebookLocationResult answer)
    {
        switch (answer.Status)
        {
            case GrantPromptStatus.Accepted:
                break;
            case GrantPromptStatus.Unsupported:
                throw NoDialog();
            default:
                throw new OneNoteException(
                    ErrorCode.Cancelled,
                    $"The user {(answer.Status == GrantPromptStatus.Declined ? "declined" : "dismissed")} the notebook-location dialog, so no notebook was created.",
                    userAction: DeclinedUserAction,
                    retryable: false);
        }

        if (answer.Location == NotebookLocation.OneDrive && !CloudNotebookCreate.Enabled)
        {
            throw CloudClosed();
        }

        if (string.IsNullOrWhiteSpace(answer.Folder))
        {
            throw new OneNoteException(ErrorCode.InvalidArgument, "No folder was chosen for the new notebook, so nothing was created.", userAction: "Retry, and have the user enter a folder on this PC.", retryable: false);
        }

        // The dialog's folder field is editable, so the folder the user accepted is checked again.
        return CheckNotInANotebook(CheckFolderArgument(answer.Folder, "The chosen folder", mustExist: true), "The chosen folder");
    }

    /// <summary>
    /// <paramref name="folder"/> (canonical) when neither it nor any folder above it holds a <c>*.onetoc2</c> file, so it
    /// isn't inside a notebook, open or closed (security review, Phill, 2026-11-13); otherwise
    /// <see cref="ErrorCode.InvalidArgument"/>, as for a folder inside an open notebook. The message never names the path.
    /// </summary>
    private static string CheckNotInANotebook(string folder, string what) =>
        NotebookFolders.InsideANotebookFolder(folder)
            ? throw new OneNoteException(
                ErrorCode.InvalidArgument,
                $"{what} is inside a notebook's folder (it, or a folder above it, holds a notebook's .onetoc2 file), so no notebook was created.",
                userAction: InsideANotebookUserAction,
                retryable: false)
            : folder;

    /// <summary>What to do about a folder inside a notebook, open or closed.</summary>
    private const string InsideANotebookUserAction =
        "Choose a folder outside every notebook, open or closed (no folder on the way to it may hold a .onetoc2 file), and outside OneNoteMcp's own folders.";

    /// <summary>
    /// <paramref name="folder"/> (a parent folder) made canonical; a URL is <see cref="ErrorCode.NotSupported"/> while
    /// the cloud seam is closed, and a UNC, device or relative path, or (with <paramref name="mustExist"/>) a folder that
    /// doesn't exist, is <see cref="ErrorCode.InvalidArgument"/>. The message names what was checked, never the path.
    /// </summary>
    private static string CheckFolderArgument(string folder, string what, bool mustExist)
    {
        if (CloudNotebookCreate.IsUrl(folder))
        {
            throw CloudClosed();
        }

        var canonical = FilePathGate.Canonical(folder, out var refusal)
            ?? throw new OneNoteException(
                ErrorCode.InvalidArgument,
                $"{what} {RefusalPhrase(refusal)}, so no notebook was created.",
                userAction: "Choose a folder on this PC (C:\\…): not a network share, a mapped network drive or a device path.",
                retryable: false);
        if (mustExist && !Directory.Exists(canonical))
        {
            throw new OneNoteException(
                ErrorCode.InvalidArgument,
                $"{what} doesn't exist or isn't a folder, so no notebook was created.",
                userAction: "Choose an existing folder on this PC; the notebook is created in a new folder inside it.",
                retryable: false);
        }

        return canonical;
    }

    /// <summary>The new notebook's own folder: <paramref name="parent"/>\<paramref name="name"/>, canonical (links followed).</summary>
    private static string TargetFolder(string parent, string name) =>
        FilePathGate.Canonical(Path.Combine(parent, name), out var refusal)
            ?? throw new OneNoteException(
                ErrorCode.InvalidArgument,
                $"The new notebook's folder {RefusalPhrase(refusal)}, so no notebook was created.",
                userAction: "Choose a folder on this PC (C:\\…): not a network share, a mapped network drive or a device path.",
                retryable: false);

    private static string RefusalPhrase(FilePathRefusal refusal) => refusal switch
    {
        FilePathRefusal.Network => "is on a network share or a mapped network drive, or leads to one",
        FilePathRefusal.Device => "is a device path",
        FilePathRefusal.InvalidCharacters => "contains characters a Windows path can't hold",
        FilePathRefusal.TooManyLinks => "goes through too many links",
        _ => "isn't a fully qualified local path",
    };

    private static OneNoteException NoDialog() =>
        new(ErrorCode.NotSupported, "create_notebook needs the user to choose the notebook's location in a dialog, and this client can't show one, so nothing was created.", userAction: NoDialogUserAction, retryable: false);

    private static OneNoteException CloudClosed() =>
        new(ErrorCode.NotSupported, "Creating a notebook in OneDrive (by URL) isn't supported yet, so nothing was created.", userAction: CloudNotebookCreate.ClosedUserAction, retryable: false);

    private static OneNoteException BadName(string noun, string problem) =>
        new(ErrorCode.InvalidArgument, $"The new {noun}'s name {problem}, so nothing was created.", userAction: NameUserAction, retryable: false);

    private static string Noun(HierarchyKind kind) => HierarchyRules.Noun(kind);

    private static OneNoteException NotAContainer(string parentId, string why) =>
        new(ErrorCode.ObjectNotFound, $"Object {parentId} {why}, so nothing was created.", userAction: NotAContainerUserAction);

    /// <summary>The <c>Create</c> class's steps for one section or section group (story 5.3).</summary>
    private sealed class CreateInParent(IOneNoteGateway gateway, WriteAccess access, HierarchyKind kind, string parentId, string name) : WriteOperation<(string Id, bool Existed)>
    {
        private HierarchyParent? parent;
        private string? existingId;

        public override OperationClass Class => OperationClass.Create;

        /// <summary>The name as OneNote shows it: a section's file name without <c>.one</c>.</summary>
        private string ShownName => kind == HierarchyKind.Section ? SectionFileNames.SectionName(name) : name;

        public override Task AuthorizeAsync(CancellationToken ct)
        {
            // The name first, before settings or the notebook are read (AD-7 step 1).
            var problem = SectionFileNames.NameProblem(name);
            if (problem is null && kind == HierarchyKind.Section)
            {
                problem = SectionFileNames.Problem(SectionFileNames.ToSectionFileName(name));
            }

            if (problem is not null)
            {
                throw BadName(Noun(kind), problem);
            }

            return access.CheckContainerAsync(parentId, () => NotAContainer(parentId, "isn't inside a notebook"), ct);
        }

        public override async Task ReadAsync(CancellationToken ct)
        {
            var xml = await gateway.GetHierarchyAsync(parentId, HierarchyScope.Children, ct).ConfigureAwait(false);
            parent = XmlReads.Readable(() => HierarchyChildren.Read(xml), "GetHierarchy");
        }

        public override void Validate()
        {
            var node = parent ?? throw new InvalidOperationException("Validate runs after ReadAsync.");
            if (HierarchyRules.RecycleBinProblem(node.IsRecycleBin, node.InRecycleBin) is { } binned)
            {
                throw NotAContainer(parentId, binned);
            }

            if (node.Kind is not (HierarchyKind.Notebook or HierarchyKind.SectionGroup))
            {
                throw NotAContainer(parentId, "isn't a notebook or section group");
            }

            // Shared with story 5.4's rename (HierarchyRules): live children only, ignoring case.
            var named = HierarchyRules.Named(node, ShownName);
            if (named.FirstOrDefault(c => c.Kind == kind) is { } same)
            {
                existingId = same.Id;
                return;
            }

            if (named.FirstOrDefault() is { } child)
            {
                // Phill, 2026-11-13, 5.3 Q4: a section and a section group never share a name under one parent.
                throw new OneNoteException(
                    ErrorCode.InvalidArgument,
                    $"A {Noun(child.Kind)} with this name ({child.Id}) already exists under {parentId}, so no {Noun(kind)} was created.",
                    userAction: "Choose another name: a section and a section group can't share a name under one parent.",
                    retryable: false);
            }
        }

        public override async Task<(string Id, bool Existed)> CallAsync(CancellationToken ct)
        {
            // An existing name makes no COM call (AD-7 Create).
            if (existingId is { } existing)
            {
                return (existing, true);
            }

            var id = kind == HierarchyKind.Section
                ? await gateway.CreateSectionAsync(parentId, SectionFileNames.ToSectionFileName(name), ct).ConfigureAwait(false)
                : await gateway.CreateSectionGroupAsync(parentId, name, ct).ConfigureAwait(false);
            return (id, false);
        }
    }

    /// <summary>What a notebook create made: its ID, its read-back path (null when unread), and why it has no grant (null when granted).</summary>
    private sealed record NotebookCreated(string Id, string? Path, string? NotGranted);

    /// <summary>The <c>Create</c> class's steps for one notebook (story 5.3), with the auto-grant after the call.</summary>
    private sealed class CreateNotebook(CreateHierarchyUseCase owner, string target) : WriteOperation<NotebookCreated>
    {
        /// <summary>The sentence a step-7 failure adds (story 5.3): a notebook made this way has no grant yet.</summary>
        private const string NoGrantNote = "If a notebook was created, it has no write grant yet.";

        private HashSet<string>? openIds;
        private string? readBackPath;
        private IReadOnlyList<string> openPaths = [];
        private FolderState state;

        public override OperationClass Class => OperationClass.Create;

        public override string? FailureNote => NoGrantNote;

        public override Task AuthorizeAsync(CancellationToken ct)
        {
            // No grant: a new notebook gets one after the call. Settings must still be writable so it can be saved.
            owner.settings().EnsureWritable();
            return Task.CompletedTask;
        }

        public override async Task ReadAsync(CancellationToken ct)
        {
            var xml = await owner.gateway.GetHierarchyAsync(string.Empty, HierarchyScope.Notebooks, ct).ConfigureAwait(false);
            var open = OpenNotebooks.Find(xml);
            openIds = new HashSet<string>(open.Select(n => n.Id), StringComparer.Ordinal);
            openPaths = NotebookFolders.WithCanonical(open.Select(n => n.Path));
            state = NotebookFolders.State(target);
        }

        public override void Validate()
        {
            if (openPaths.Any(p => NotebookFolders.Same(target, p)))
            {
                throw new OneNoteException(ErrorCode.FileExists, "The new notebook's folder is an open notebook's folder, so nothing was created.", userAction: FolderTakenUserAction, retryable: false);
            }

            if (openPaths.Any(p => NotebookFolders.Inside(target, p) || NotebookFolders.Inside(p, target)))
            {
                throw new OneNoteException(ErrorCode.InvalidArgument, "The new notebook's folder is inside an open notebook's folder, or holds one, so nothing was created.", userAction: FolderRefusedUserAction, retryable: false);
            }

            if (NotebookFolders.OwnFolders(owner.settings()).Any(f => NotebookFolders.Same(target, f) || NotebookFolders.Inside(target, f)))
            {
                throw new OneNoteException(ErrorCode.InvalidArgument, "The new notebook's folder is inside one of OneNoteMcp's own folders, so nothing was created.", userAction: FolderRefusedUserAction, retryable: false);
            }

            switch (state)
            {
                case FolderState.NotEmpty:
                case FolderState.File:
                    throw new OneNoteException(ErrorCode.FileExists, "The new notebook's folder already exists and isn't empty, so nothing was created.", userAction: FolderTakenUserAction, retryable: false);
                case FolderState.Unreadable:
                    throw new OneNoteException(ErrorCode.FileExists, "The new notebook's folder exists and couldn't be checked for files, so nothing was created.", userAction: FolderTakenUserAction, retryable: false);
            }
        }

        public override async Task<NotebookCreated> CallAsync(CancellationToken ct)
        {
            try
            {
                var id = await owner.gateway.CreateNotebookAsync(target, ct).ConfigureAwait(false);
                return new NotebookCreated(id, null, "the notebook wasn't read back.");
            }
            catch (OneNoteException ex) when (WritePipeline.IsAmbiguous(ex))
            {
                // The pipeline still decides outcomeUnknown (AD-9); the message says the grant isn't there.
                throw new OneNoteException(ex.Code, $"{ex.Message} {NoGrantNote}", ex.ComHResult, ex, ex.OutcomeUnknown, ex.UserAction, ex.Retryable);
            }
        }

        /// <summary>
        /// Between 7 and 8, under the write lock: read the notebook back and write its grant (AD-6 <c>grantedVia:
        /// created</c>) under the settings mutex, which <see cref="SettingsWriter"/> takes inside the write lock (AD-7).
        /// </summary>
        public override async Task<NotebookCreated> AfterCallAsync(NotebookCreated result, CancellationToken ct)
        {
            // At Children scope, so one read gives the path and whether the notebook already holds anything.
            var xml = await owner.gateway.GetHierarchyAsync(result.Id, HierarchyScope.Children, ct).ConfigureAwait(false);
            var node = XmlReads.Readable(() => HierarchyNode.Root(xml), "GetHierarchy");
            var contents = XmlReads.Readable(() => HierarchyChildren.Read(xml), "GetHierarchy");
            if (node.Kind != HierarchyKind.Notebook || node.Path.Length == 0)
            {
                return result with { NotGranted = "OneNote didn't report a notebook with a path for it." };
            }

            var key = GrantKeys.NotebookKey(node.Path);
            if (!GrantKeys.Comparer.Equals(key, GrantKeys.NotebookKey(target)))
            {
                // meta.path is only ever the folder the caller and the user chose (AD-9), so a path elsewhere isn't reported.
                return result with { NotGranted = "OneNote put it somewhere other than the folder the server checked." };
            }

            readBackPath = node.Path;
            var read = result with { Path = node.Path };

            if (openIds?.Contains(result.Id) == true)
            {
                return read with { NotGranted = "OneNote returned a notebook that was already open." };
            }

            if (contents.Children.Count > 0)
            {
                // Security review, Phill, 2026-11-13: only an empty notebook is surely new. The recycle bin and what is in
                // it don't count (HierarchyChildren skips them).
                return read with { NotGranted = "It already holds sections or section groups, so it may be an existing notebook OneNote opened rather than a new one." };
            }

            ct.ThrowIfCancellationRequested();
            owner.writer.SaveWriteGrant(new NotebookGrant(key, node.DisplayName, result.Id, Write: true, PermanentDelete: false, owner.clock.GetUtcNow(), GrantedVia.Created));
            return read with { NotGranted = null };
        }

        /// <summary>The read-back or the grant save failed (<see cref="ErrorCode.ComBusy"/>, IO, the deadline): the create stands, ungranted.</summary>
        public override NotebookCreated AfterCallFailed(NotebookCreated result, Exception exception) =>
            result with
            {
                Path = readBackPath,
                NotGranted = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{(readBackPath is null ? "it couldn't be read back" : "its grant couldn't be saved")} ({(exception is OneNoteException one ? one.Code.ToString() : exception.GetType().Name)})."),
            };

        private const string FolderTakenUserAction =
            "Choose a name or folder where nothing exists yet: a new notebook goes in a new or empty folder.";

        private const string FolderRefusedUserAction =
            "Choose a folder outside every open notebook and outside OneNoteMcp's own folders.";
    }
}
