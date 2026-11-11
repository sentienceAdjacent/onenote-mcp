using OneNoteMcp.Core.Settings;

namespace OneNoteMcp.Core.Access;

/// <summary>The notebook a write targets, as AD-7 step 1 resolved it: its ID and its <c>NotebookKey</c>.</summary>
public sealed record WriteTarget(string NotebookId, string NotebookKey);

/// <summary>
/// AD-7 step 1 for writes (AD-6): <c>settings.json</c> must be writable (<see cref="SettingsSnapshot.EnsureWritable"/>),
/// and the notebook that owns the target must hold a write grant. The notebook is resolved through the gateway, never
/// from the ID string: a <c>GetHierarchyParent</c> chain to the first <c>one:Notebook</c>, whose <c>path</c> comes live
/// from <c>GetHierarchy</c> and becomes the key through <see cref="GrantKeys.NotebookKey"/>. Settings are re-read at
/// every check (AD-10). It never asks the user and never involves a read grant: a write in a password-protected section
/// needs the write grant alone (AD-6, AD-21). Messages carry IDs only (AD-9).
/// </summary>
public sealed class WriteAccess
{
    private readonly Func<SettingsSnapshot> settings;
    private readonly HierarchyWalk walk;

    /// <param name="gateway">The gateway the owning notebook is resolved through.</param>
    /// <param name="settings">Reads <c>settings.json</c>; called once per check.</param>
    public WriteAccess(IOneNoteGateway gateway, Func<SettingsSnapshot> settings)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(settings);
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
        if (notebook.Path.Length == 0)
        {
            throw new OneNoteException(
                ErrorCode.WriteNotGranted,
                $"OneNote reports no path for notebook {notebook.Id}, so it can't hold a write grant.",
                userAction: NotGrantedUserAction);
        }

        var key = GrantKeys.NotebookKey(notebook.Path);
        if (!snapshot.Notebooks.TryGetValue(key, out var grant) || !grant.Write)
        {
            throw new OneNoteException(
                ErrorCode.WriteNotGranted,
                $"Notebook {notebook.Id} has no write grant from the user.",
                userAction: NotGrantedUserAction);
        }

        return new WriteTarget(notebook.Id, key);
    }
}
