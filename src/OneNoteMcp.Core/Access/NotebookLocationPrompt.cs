namespace OneNoteMcp.Core.Access;

/// <summary>Where the user chose to put a new notebook (Phill, 2026-11-13, 5.3 plan Q3).</summary>
public enum NotebookLocation
{
    /// <summary>A folder on this PC.</summary>
    Local,

    /// <summary>The user's OneDrive, as a cloud notebook. Offered only while <see cref="CloudNotebookCreate.Enabled"/>.</summary>
    OneDrive,
}

/// <summary>The wire names of <see cref="NotebookLocation"/> in the dialog.</summary>
public static class NotebookLocations
{
    public const string Local = "local";
    public const string OneDrive = "oneDrive";
}

/// <summary>
/// What the notebook-location dialog shows the user (story 5.3): the new notebook's name, the folder to prefill (the
/// caller's proposal, else OneNote's default notebook folder when that is local; null for a blank field), and whether to
/// offer OneDrive. The name and folder go to the user's client only, never into a tool result or a log (AD-9, AD-13).
/// </summary>
public sealed record NotebookLocationQuestion(string NotebookName, string? Folder, bool OfferOneDrive);

/// <summary>
/// How the location dialog ended. With <see cref="GrantPromptStatus.Accepted"/>, where the user chose and, for
/// <see cref="NotebookLocation.Local"/>, the folder the notebook goes in (its parent folder, as typed).
/// </summary>
public sealed record NotebookLocationResult(GrantPromptStatus Status, NotebookLocation Location = NotebookLocation.Local, string? Folder = null)
{
    public static NotebookLocationResult Unsupported { get; } = new(GrantPromptStatus.Unsupported);
}

/// <summary>
/// The port through which <c>create_notebook</c> asks the user where a new notebook goes (Phill, 2026-11-13, 5.3 plan Q3).
/// The user picks the location, which is what makes the new notebook's no-dialog write grant (AD-6 <c>grantedVia:
/// created</c>) safe. The Server adapts MCP elicitation to it, so Core stays free of the SDK. The caller holds no STA call,
/// no write lock and no settings mutex while it waits, and the wait counts against no timeout. A client that can't show
/// the dialog gives <see cref="GrantPromptStatus.Unsupported"/>.
/// </summary>
public interface INotebookLocationPrompt
{
    Task<NotebookLocationResult> AskAsync(NotebookLocationQuestion question, CancellationToken ct);
}
