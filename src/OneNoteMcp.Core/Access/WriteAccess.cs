using OneNoteMcp.Core.Settings;
using OneNoteMcp.Xml;

namespace OneNoteMcp.Core.Access;

/// <summary>The notebook a write targets, as AD-7 step 1 resolved it: its ID and its <c>NotebookKey</c>.</summary>
public sealed record WriteTarget(string NotebookId, string NotebookKey);

/// <summary>
/// One write grant AD-7 step 1 checked (story 3.12): the object it walked from, whether that object may be the notebook
/// itself (<see cref="WriteAccess.CheckContainerAsync"/>), and the notebook it resolved to.
/// </summary>
public sealed record GrantedNotebook(string ObjectId, bool Container, WriteTarget Target);

/// <summary>
/// AD-7 step 1 for a permanent delete (story 5.6): the write grant's target, whether the permanent-delete grant is stored
/// beside it, and the notebook's displayed name for the one-call confirmation (never put in a result, AD-9).
/// </summary>
public sealed record PermanentDeleteAccess(WriteTarget Target, bool PermanentDeleteGranted, string NotebookName);

/// <summary>
/// AD-7 step 1 for writes (AD-6): <c>settings.json</c> must be writable (<see cref="SettingsSnapshot.EnsureWritable"/>),
/// and the notebook that owns the target must hold a write grant. The notebook is resolved through the gateway, never
/// from the ID string: a <c>GetHierarchyParent</c> chain to the first <c>one:Notebook</c>, whose <c>path</c> comes live
/// from <c>GetHierarchy</c> and becomes the key through <see cref="GrantKeys.NotebookKey"/>. Settings are re-read at
/// every check (AD-10). It never asks the user and never involves a read grant: a write in a password-protected section
/// needs the write grant alone (AD-6, AD-21). A missing grant on a notebook with a path is <see cref="WriteGrantMissing"/>,
/// which <see cref="Writes.WritePipeline"/> may answer with the grant dialog (<see cref="WriteGrantDialog"/>) outside the
/// deadline. Messages carry IDs only (AD-9).
/// </summary>
public sealed class WriteAccess
{
    private readonly IOneNoteGateway gateway;
    private readonly Func<SettingsSnapshot> settings;
    private readonly HierarchyWalk walk;

    /// <param name="gateway">The gateway the owning notebook is resolved through.</param>
    /// <param name="settings">Reads <c>settings.json</c>; called once per check.</param>
    public WriteAccess(IOneNoteGateway gateway, Func<SettingsSnapshot> settings)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(settings);
        this.gateway = gateway;
        this.settings = settings;
        walk = new HierarchyWalk(gateway);
    }

    /// <summary>
    /// How the user grants a write (AD-6). The model can't grant it, and the text says never to run the command. The
    /// notebook is named by a placeholder, since messages never carry names (AD-9).
    /// </summary>
    public static string NotGrantedUserAction { get; } =
        "Writing to this notebook needs the user's permission, which you can't give. Ask the user whether to grant it: they can run "
        + GrantCli.Run("--write \"<notebook name>\"") + ". Then retry. Never run the grant command yourself.";

    /// <summary>
    /// What to do about a write in or to a section open outside any notebook (backlog 12): a <c>.one</c> file opened by hand
    /// sits under <c>one:OpenSections</c>, where no write grant can apply (AD-6 grants are per notebook).
    /// </summary>
    public const string LooseSectionUserAction =
        "Edit it in OneNote, or move the section into a notebook in OneNote and retry. Reading it works as for any section; a password-protected one, or one under an export root or the backups folder, needs a read grant.";

    /// <summary>
    /// Step 1 for a write to <paramref name="objectId"/> (a section, for <c>create_page</c>). Throws
    /// <see cref="ErrorCode.SettingsInvalid"/> when <c>settings.json</c> is invalid or newer, an ordinary error when the
    /// chain can't be read (<see cref="ErrorCode.ObjectNotFound"/> for an unknown ID), and
    /// <see cref="ErrorCode.WriteNotGranted"/> when the notebook has no write grant. An object in, or that is, a section open
    /// outside any notebook is <see cref="ErrorCode.NotSupported"/> naming the section (backlog 12).
    /// </summary>
    /// <param name="notInsideANotebook">The error for an object no notebook and no section owns (a notebook itself).</param>
    /// <param name="subjectId">The page the write is for, when <paramref name="objectId"/> is its section (a page move,
    /// <c>set_page_level</c>), so a loose section's refusal names the page.</param>
    public async Task<WriteTarget> CheckAsync(string objectId, Func<OneNoteException> notInsideANotebook, CancellationToken ct, string? subjectId = null)
    {
        ArgumentNullException.ThrowIfNull(notInsideANotebook);
        var snapshot = settings();
        snapshot.EnsureWritable();

        var notebook = await NotebookOrLooseAsync(objectId ?? string.Empty, notInsideANotebook, ct, subjectId).ConfigureAwait(false);
        return Granted(snapshot, notebook.Id, notebook.DisplayName, notebook.Path, objectId ?? string.Empty, container: false);
    }

    /// <summary>
    /// Step 1 for a write under <paramref name="parentId"/>, a parent that may be the notebook itself (story 5.3: a new
    /// section or section group's parent), with <see cref="CheckAsync"/>'s rules and errors. The grant is the owning
    /// notebook's, found by the parent walk; when the walk ends at once (the parent has no parent), the parent itself is
    /// read at <c>self</c>, and it is the notebook when it is a <c>one:Notebook</c>. A section open outside any notebook is
    /// <see cref="ErrorCode.NotSupported"/> naming it (backlog 12); anything else is <paramref name="notInsideANotebook"/>. Whether the parent is the right kind is AD-7 step 4's to decide.
    /// </summary>
    public async Task<WriteTarget> CheckContainerAsync(string parentId, Func<OneNoteException> notInsideANotebook, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(notInsideANotebook);
        var snapshot = settings();
        snapshot.EnsureWritable();

        var id = parentId ?? string.Empty;
        var (notebook, section) = await walk.OwnersAsync(id, ct).ConfigureAwait(false);
        if (notebook is null)
        {
            var xml = await gateway.GetHierarchyAsync(id, HierarchyScope.Self, ct).ConfigureAwait(false);
            var self = XmlReads.Readable(() => HierarchyNode.Root(xml), "GetHierarchy");
            notebook = self.Kind == HierarchyKind.Notebook ? self
                : Loose(id, section ?? (self.Kind == HierarchyKind.Section ? self : null)) is { } loose ? throw loose
                : throw notInsideANotebook();
        }

        return Granted(snapshot, notebook.Id, notebook.DisplayName, notebook.Path, id, container: true);
    }

    /// <summary>
    /// Step 1 for a permanent delete of <paramref name="objectId"/> (AD-6; story 5.6), with <see cref="CheckAsync"/>'s
    /// walk, rules and errors (the write grant first, as <see cref="WriteGrantMissing"/>), from one read of
    /// <c>settings.json</c>; then whether the same notebook entry also holds the permanent-delete grant, which counts only
    /// beside its write grant. It never asks: what a missing permanent-delete grant leads to is the caller's (5.6 plan Q1 c,
    /// a one-call confirmation). Also returns the notebook's displayed name, for that confirmation only (never a result).
    /// </summary>
    public async Task<PermanentDeleteAccess> CheckPermanentDeleteAsync(string objectId, Func<OneNoteException> notInsideANotebook, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(notInsideANotebook);
        var snapshot = settings();
        snapshot.EnsureWritable();

        var notebook = await NotebookOrLooseAsync(objectId ?? string.Empty, notInsideANotebook, ct).ConfigureAwait(false);
        var target = Granted(snapshot, notebook.Id, notebook.DisplayName, notebook.Path, objectId ?? string.Empty, container: false);
        var granted = snapshot.Notebooks.TryGetValue(target.NotebookKey, out var grant) && grant.Write && grant.PermanentDelete;
        return new PermanentDeleteAccess(target, granted, notebook.DisplayName);
    }

    /// <summary>
    /// The notebook that owns <paramref name="objectId"/>. With none, a write in or to a section open outside any notebook
    /// is <see cref="ErrorCode.NotSupported"/> naming it (backlog 12); anything else is <paramref name="notInsideANotebook"/>,
    /// as before. Only when the chain finds no section is the object itself read, to tell a loose section from a notebook.
    /// </summary>
    private async Task<HierarchyNode> NotebookOrLooseAsync(string objectId, Func<OneNoteException> notInsideANotebook, CancellationToken ct, string? subjectId = null)
    {
        var (notebook, section) = await walk.OwnersAsync(objectId, ct).ConfigureAwait(false);
        if (notebook is not null)
        {
            return notebook;
        }

        throw Loose(subjectId ?? objectId, section ?? await SectionItselfAsync(objectId, ct).ConfigureAwait(false)) ?? notInsideANotebook();
    }

    /// <summary>
    /// D182: refuses a write in or to a section open outside any notebook (backlog 12) with
    /// <see cref="ErrorCode.NotSupported"/> and <see cref="LooseSectionUserAction"/>, before a write's own section checks
    /// (locked, read grant, subpages), so a protected or subpaged loose section gets the same answer as any loose one. Does
    /// nothing for an object a notebook owns, or one whose chain finds no section; the caller's checks then run as before.
    /// </summary>
    /// <param name="subjectId">The page the write is for, when <paramref name="objectId"/> is its section, so the refusal names the page.</param>
    public async Task RefuseLooseAsync(string objectId, CancellationToken ct, string? subjectId = null)
    {
        var id = objectId ?? string.Empty;
        var (notebook, section) = await walk.OwnersAsync(id, ct).ConfigureAwait(false);
        if (notebook is null && Loose(subjectId ?? id, section ?? await SectionItselfAsync(id, ct).ConfigureAwait(false)) is { } loose)
        {
            throw loose;
        }
    }

    /// <summary>
    /// <paramref name="objectId"/> itself when it reads as a <c>one:Section</c>; null otherwise. Read only when the chain found
    /// no section, to tell a loose section from a notebook; when the read fails, the caller's own error stands, as before.
    /// </summary>
    private async Task<HierarchyNode?> SectionItselfAsync(string objectId, CancellationToken ct)
    {
        try
        {
            var xml = await gateway.GetHierarchyAsync(objectId, HierarchyScope.Self, ct).ConfigureAwait(false);
            var self = XmlReads.Readable(() => HierarchyNode.Root(xml), "GetHierarchy");
            return self.Kind == HierarchyKind.Section ? self : null;
        }
        catch (OneNoteException)
        {
            return null;
        }
    }

    /// <summary>The refusal for a write in or to the loose <paramref name="section"/>; null when there is none. IDs only (AD-9).</summary>
    private static OneNoteException? Loose(string objectId, HierarchyNode? section) => section is null
        ? null
        : new OneNoteException(
            ErrorCode.NotSupported,
            string.Equals(section.Id, objectId, StringComparison.OrdinalIgnoreCase)
                ? $"Section {objectId} is open in OneNote outside any notebook (a .one file opened by hand), and the server can't write there, so nothing was written."
                : $"Page {objectId} is in section {section.Id}, which is open in OneNote outside any notebook (a .one file opened by hand), and the server can't write there, so nothing was written.",
            userAction: LooseSectionUserAction,
            retryable: false);

    /// <summary>
    /// The stored grants of the notebook keyed <paramref name="notebookKey"/>, from a fresh read of <c>settings.json</c>
    /// (AD-10): its write grant, and its permanent-delete grant, which counts only beside the write grant. A permanent
    /// delete checks them again just before its COM call (story 5.6). An invalid or newer file counts as no grants.
    /// </summary>
    public (bool Write, bool PermanentDelete) StoredGrants(string notebookKey)
    {
        var snapshot = settings();
        if (snapshot.Status is not (SettingsStatus.Ok or SettingsStatus.Absent)
            || !snapshot.Notebooks.TryGetValue(notebookKey ?? string.Empty, out var grant))
        {
            return (false, false);
        }

        return (grant.Write, grant.Write && grant.PermanentDelete);
    }

    /// <summary>
    /// How the user grants permanent deletes (AD-6; 5.6 plan Q1 c), for <see cref="ErrorCode.PermanentDeleteNotGranted"/>.
    /// The model can't grant it, and the text says never to run the command. The notebook is named by a placeholder, since
    /// messages never carry names (AD-9).
    /// </summary>
    public static string PermanentDeleteNotGrantedUserAction { get; } =
        "Deleting permanently needs the user's permission, which you can't give. Ask the user whether they want this deleted for good. "
        + "They can confirm this one delete in the dialog it shows (typing the object's name), or allow permanent deletes in the notebook by running "
        + GrantCli.Run("--permanent-delete \"<notebook name>\"") + " (it needs the notebook's write grant). Then retry. Never run the grant command yourself.";

    /// <summary>
    /// Step 1 for a write to an open notebook already found by its key (<c>restore_from_journal</c>, story 2.11), with
    /// <see cref="CheckAsync"/>'s rules and errors: settings writable, a path, a write grant on that path's key. No walk.
    /// </summary>
    public WriteTarget CheckNotebook(OpenNotebook notebook)
    {
        ArgumentNullException.ThrowIfNull(notebook);
        var snapshot = settings();
        snapshot.EnsureWritable();
        return Granted(snapshot, notebook.Id, notebook.DisplayName, notebook.Path, notebook.Id, container: true);
    }

    private static WriteTarget Granted(SettingsSnapshot snapshot, string notebookId, string displayName, string path, string objectId, bool container)
    {
        if (path.Length == 0)
        {
            throw new OneNoteException(
                ErrorCode.WriteNotGranted,
                $"OneNote reports no path for notebook {notebookId}, so it can't hold a write grant.",
                userAction: NotGrantedUserAction);
        }

        var key = GrantKeys.NotebookKey(path);
        if (!snapshot.Notebooks.TryGetValue(key, out var grant) || !grant.Write)
        {
            throw new WriteGrantMissing(notebookId, displayName, path, key);
        }

        var target = new WriteTarget(notebookId, key);
        Ledger.Value?.Add(new GrantedNotebook(objectId, container, target));
        return target;
    }

    /// <summary>What to do when a write's notebook changed under the lock (AD-7; story 3.12).</summary>
    public const string NotebookChangedUserAction =
        "The object moved to another notebook while this call ran. Re-read the hierarchy (get_hierarchy) and retry; the retry checks the write grant of the notebook it is in now.";

    /// <summary>
    /// The grants this call's step 1 checked, when the write pipeline is recording them (<see cref="BeginLedger"/>): each
    /// successful check adds one. An async-local, so it follows the call's own flow and nothing else.
    /// </summary>
    private static readonly AsyncLocal<List<GrantedNotebook>?> Ledger = new();

    /// <summary>
    /// Starts recording, for the calling flow, every write grant a check here passes (story 3.12; AD-7 "a write's
    /// notebook is re-resolved under the lock"). The pipeline calls it once per write, clears the list before each try of
    /// step 1, and re-resolves what it holds after taking the lock (<see cref="ReResolveAsync"/>).
    /// </summary>
    internal static List<GrantedNotebook> BeginLedger()
    {
        var list = new List<GrantedNotebook>();
        Ledger.Value = list;
        return list;
    }

    /// <summary>
    /// AD-7's re-resolve, under the lock (story 3.12): each of <paramref name="granted"/> walked to its notebook again, as
    /// step 1 walked it, whose <c>NotebookKey</c> must be the one step 1 granted. Otherwise, or when the object no longer
    /// sits in any notebook, the write is <see cref="ErrorCode.WriteNotGranted"/>, retryable, before COM, so a section
    /// moved to another notebook after step 1 can't take the first notebook's grant (epic 2's create-page race). The
    /// walk's own errors (an ID that is gone is <see cref="ErrorCode.ObjectNotFound"/>) pass through. IDs only (AD-9).
    /// </summary>
    internal static async Task ReResolveAsync(IOneNoteGateway gateway, IReadOnlyList<GrantedNotebook> granted, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(granted);
        var walk = new HierarchyWalk(gateway);
        foreach (var each in granted.Distinct())
        {
            var notebook = await walk.OwningNotebookAsync(each.ObjectId, ct).ConfigureAwait(false);
            if (notebook is null && each.Container)
            {
                var xml = await gateway.GetHierarchyAsync(each.ObjectId, HierarchyScope.Self, ct).ConfigureAwait(false);
                var self = XmlReads.Readable(() => HierarchyNode.Root(xml), "GetHierarchy");
                notebook = self.Kind == HierarchyKind.Notebook ? self : null;
            }

            var key = notebook is null || notebook.Path.Length == 0 ? null : GrantKeys.NotebookKey(notebook.Path);
            if (key is null || !GrantKeys.Comparer.Equals(key, each.Target.NotebookKey))
            {
                throw new OneNoteException(
                    ErrorCode.WriteNotGranted,
                    $"Object {each.ObjectId} is no longer in notebook {each.Target.NotebookId}, whose write grant this call checked, so nothing was written.",
                    userAction: NotebookChangedUserAction,
                    retryable: true);
            }
        }
    }

    /// <summary>
    /// <see cref="ErrorCode.WriteNotGranted"/> for <paramref name="notebookId"/>, saying <paramref name="why"/>, with
    /// <see cref="NotGrantedUserAction"/>. Messages carry IDs only (AD-9).
    /// </summary>
    internal static OneNoteException NotGranted(string notebookId, string why) =>
        new(ErrorCode.WriteNotGranted, $"Notebook {notebookId} has no write grant from the user: {why}", userAction: NotGrantedUserAction);
}

/// <summary>
/// AD-7 step 1's <see cref="ErrorCode.WriteNotGranted"/> for a notebook that has a path but no write grant: the one case
/// the write pipeline may answer with the grant dialog (AD-6). It carries the notebook as step 1 resolved it, so the
/// dialog names it without another walk; its message and <c>userAction</c> are the plain refusal's, and carry the ID only
/// (AD-9). Uncaught, it reaches the tool runner as an ordinary <see cref="OneNoteException"/>.
/// </summary>
internal sealed class WriteGrantMissing(string notebookId, string displayName, string path, string key)
    : OneNoteException(ErrorCode.WriteNotGranted, $"Notebook {notebookId} has no write grant from the user.", userAction: WriteAccess.NotGrantedUserAction)
{
    public string NotebookId { get; } = notebookId;

    /// <summary>The notebook's <c>nickname</c>, else its <c>name</c>; shown in the dialog only.</summary>
    public string DisplayName { get; } = displayName;

    /// <summary>The live <c>one:Notebook@path</c>; never empty.</summary>
    public string Path { get; } = path;

    /// <summary><see cref="GrantKeys.NotebookKey"/> of <see cref="Path"/>.</summary>
    public string Key { get; } = key;
}
