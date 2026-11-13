using OneNoteMcp.Core.Settings;
using OneNoteMcp.Xml;

namespace OneNoteMcp.Core.Access;

/// <summary>The notebook a write targets, as AD-7 step 1 resolved it: its ID and its <c>NotebookKey</c>.</summary>
public sealed record WriteTarget(string NotebookId, string NotebookKey);

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
    /// Step 1 for a write to <paramref name="objectId"/> (a section, for <c>create_page</c>). Throws
    /// <see cref="ErrorCode.SettingsInvalid"/> when <c>settings.json</c> is invalid or newer, an ordinary error when the
    /// chain can't be read (<see cref="ErrorCode.ObjectNotFound"/> for an unknown ID), and
    /// <see cref="ErrorCode.WriteNotGranted"/> when the notebook has no write grant.
    /// </summary>
    /// <param name="notInsideANotebook">The error for an object no notebook owns (it is a notebook itself).</param>
    public async Task<WriteTarget> CheckAsync(string objectId, Func<OneNoteException> notInsideANotebook, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(notInsideANotebook);
        var snapshot = settings();
        snapshot.EnsureWritable();

        var notebook = await walk.OwningNotebookAsync(objectId ?? string.Empty, ct).ConfigureAwait(false) ?? throw notInsideANotebook();
        return Granted(snapshot, notebook.Id, notebook.DisplayName, notebook.Path);
    }

    /// <summary>
    /// Step 1 for a write under <paramref name="parentId"/>, a parent that may be the notebook itself (story 5.3: a new
    /// section or section group's parent), with <see cref="CheckAsync"/>'s rules and errors. The grant is the owning
    /// notebook's, found by the parent walk; when the walk ends at once (the parent has no parent), the parent itself is
    /// read at <c>self</c>, and it is the notebook when it is a <c>one:Notebook</c>. Anything else is
    /// <paramref name="notInsideANotebook"/>. Whether the parent is the right kind is AD-7 step 4's to decide.
    /// </summary>
    public async Task<WriteTarget> CheckContainerAsync(string parentId, Func<OneNoteException> notInsideANotebook, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(notInsideANotebook);
        var snapshot = settings();
        snapshot.EnsureWritable();

        var id = parentId ?? string.Empty;
        var notebook = await walk.OwningNotebookAsync(id, ct).ConfigureAwait(false);
        if (notebook is null)
        {
            var xml = await gateway.GetHierarchyAsync(id, HierarchyScope.Self, ct).ConfigureAwait(false);
            var self = XmlReads.Readable(() => HierarchyNode.Root(xml), "GetHierarchy");
            notebook = self.Kind == HierarchyKind.Notebook ? self : throw notInsideANotebook();
        }

        return Granted(snapshot, notebook.Id, notebook.DisplayName, notebook.Path);
    }

    /// <summary>
    /// Step 1 for a write to an open notebook already found by its key (<c>restore_from_journal</c>, story 2.11), with
    /// <see cref="CheckAsync"/>'s rules and errors: settings writable, a path, a write grant on that path's key. No walk.
    /// </summary>
    public WriteTarget CheckNotebook(OpenNotebook notebook)
    {
        ArgumentNullException.ThrowIfNull(notebook);
        var snapshot = settings();
        snapshot.EnsureWritable();
        return Granted(snapshot, notebook.Id, notebook.DisplayName, notebook.Path);
    }

    private static WriteTarget Granted(SettingsSnapshot snapshot, string notebookId, string displayName, string path)
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

        return new WriteTarget(notebookId, key);
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
