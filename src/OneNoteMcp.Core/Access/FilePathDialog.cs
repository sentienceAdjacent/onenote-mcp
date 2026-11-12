using System.Globalization;

namespace OneNoteMcp.Core.Access;

/// <summary>
/// AD-7's user-gated file paths (Phill, 2026-11-12, story 2.9 security review): a page write whose XML names new file
/// paths, each canonical and local, throws this from step 1. <see cref="Writes.WritePipeline"/> then raises the
/// file-path dialog (<see cref="FilePathDialog"/>) with the deadline paused, and on confirmation calls
/// <see cref="Confirm"/> and runs step 1 once more. It is <see cref="ErrorCode.WriteNotGranted"/>: uncaught (no way to
/// ask), it is not retryable, since a client that can't show the dialog can never confirm a path; a decline or a cancel
/// is retryable once the user agrees (<see cref="Because"/>). The message gives a count only; the paths go to the
/// dialog, never into a result or a log (AD-9, AD-13).
/// </summary>
public sealed class FilePathsUnconfirmed : OneNoteException
{
    /// <summary>What to do when no dialog can ask the user: there is no other way to confirm a path.</summary>
    public const string NoDialogUserAction =
        "This client can't show the dialog in which the user confirms a file path, so the write can't name a file on this PC. "
        + "Send the image as inline one:Data (base64) instead, or ask the user to insert the file in OneNote themselves.";

    /// <summary>What to do when the user declined or dismissed the dialog.</summary>
    public const string DeclinedUserAction =
        "The user didn't confirm the file path(s). Ask the user whether they want the file read into the page, and retry only if they agree; "
        + "or send the image as inline one:Data (base64) instead, or ask the user to insert the file in OneNote themselves.";

    private readonly Action confirm;

    public FilePathsUnconfirmed(string pageId, IReadOnlyList<string> paths, Action confirm)
        : this(pageId, paths, confirm, null, NoDialogUserAction, retryable: false)
    {
    }

    private FilePathsUnconfirmed(string pageId, IReadOnlyList<string> paths, Action confirm, string? why, string userAction, bool retryable)
        : base(
            ErrorCode.WriteNotGranted,
            string.Create(CultureInfo.InvariantCulture, $"The XML for page {pageId} names {paths?.Count ?? 0} file path(s) the user hasn't confirmed")
                + (why is null ? ", so nothing was written." : $": {why}, so nothing was written."),
            userAction: userAction,
            retryable: retryable)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(confirm);
        PageId = pageId ?? string.Empty;
        Paths = paths;
        this.confirm = confirm;
    }

    public string PageId { get; }

    /// <summary>The canonical paths to confirm, for the dialog only.</summary>
    public IReadOnlyList<string> Paths { get; }

    /// <summary>Records that the user confirmed <see cref="Paths"/>, on the write that threw this.</summary>
    public void Confirm() => confirm();

    /// <summary>The refusal after the dialog: <paramref name="unsupported"/> keeps it final; a decline or a cancel is retryable.</summary>
    internal FilePathsUnconfirmed Because(string why, bool unsupported, string? userAction = null) =>
        unsupported
            ? new(PageId, Paths, confirm, why, NoDialogUserAction, retryable: false)
            : new(PageId, Paths, confirm, why, userAction ?? DeclinedUserAction, retryable: true);

    /// <summary>What to do when a path became new after the user confirmed the others (the page changed meanwhile).</summary>
    public const string ChangedUserAction =
        "The page changed while the user was confirming file paths, so another path became new. Re-read the page and retry: the user is asked to confirm each new path.";
}

/// <summary>
/// The file-path dialog (AD-7): asks the user, through <see cref="IGrantPrompt.AskFilePathsAsync"/>, to accept or
/// decline the paths a <see cref="FilePathsUnconfirmed"/> names, each canonical. There is nothing to type: AD-6's Limits
/// apply, so a client-side hook could answer it. Returns only when the user accepted; a decline, a cancel or a client
/// without elicitation throws <see cref="FilePathsUnconfirmed"/> (<see cref="ErrorCode.WriteNotGranted"/>), saying why.
/// The caller holds no STA call, no write lock and no settings mutex while the user decides, and passes the client's
/// token, never the deadline's. Nothing is stored: a confirmation covers this call only.
/// </summary>
public static class FilePathDialog
{
    public static async Task AskAsync(IGrantPrompt prompt, FilePathsUnconfirmed unconfirmed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(unconfirmed);

        GrantPromptResult answer;
        try
        {
            answer = await prompt.AskFilePathsAsync(new FilePathQuestion(unconfirmed.PageId, unconfirmed.Paths), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            answer = new GrantPromptResult(GrantPromptStatus.Cancelled);
        }

        switch (answer.Status)
        {
            case GrantPromptStatus.Accepted:
                return;
            case GrantPromptStatus.Unsupported:
                throw unconfirmed.Because("this client can't show the dialog that asks the user", unsupported: true);
            case GrantPromptStatus.Declined:
                throw unconfirmed.Because("the user declined them", unsupported: false);
            default:
                throw unconfirmed.Because("the dialog was cancelled", unsupported: false);
        }
    }
}
