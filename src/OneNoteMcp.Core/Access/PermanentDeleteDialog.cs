namespace OneNoteMcp.Core.Access;

/// <summary>
/// AD-7 step 1 for a permanent delete whose notebook has a write grant but no stored permanent-delete grant (Phill,
/// 2026-11-13, 5.6 plan Q1 c): <see cref="ErrorCode.PermanentDeleteNotGranted"/>. <see cref="Writes.WritePipeline"/>
/// answers it with the one-call confirmation (<see cref="PermanentDeleteDialog"/>), with the deadline paused, and on a
/// match calls <see cref="Confirm"/> and runs step 1 once more. Nothing is stored: the model can never cause a lasting
/// permission for this write. Uncaught (no way to ask), it is retryable once the user grants it with the CLI. Its message
/// carries IDs only; the names go to the dialog (AD-9).
/// </summary>
public sealed class PermanentDeleteUnconfirmed : OneNoteException
{
    private readonly Action confirm;

    public PermanentDeleteUnconfirmed(PermanentDeleteQuestion question, string notebookId, Action confirm)
        : this(question, notebookId, confirm, null)
    {
    }

    private PermanentDeleteUnconfirmed(PermanentDeleteQuestion question, string notebookId, Action confirm, string? why)
        : base(
            ErrorCode.PermanentDeleteNotGranted,
            $"Notebook {notebookId} has no permanent-delete grant from the user"
                + (why is null ? string.Empty : $", and {why}")
                + $", so {question?.ObjectId} wasn't deleted.",
            userAction: WriteAccess.PermanentDeleteNotGrantedUserAction)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(confirm);
        Question = question;
        NotebookId = notebookId ?? string.Empty;
        this.confirm = confirm;
    }

    /// <summary>What the confirmation shows, for the dialog only.</summary>
    public PermanentDeleteQuestion Question { get; }

    public string NotebookId { get; }

    /// <summary>Records that the user confirmed this one delete, on the write that threw this.</summary>
    public void Confirm() => confirm();

    /// <summary>The refusal after the dialog, saying <paramref name="why"/>.</summary>
    internal PermanentDeleteUnconfirmed Because(string why) => new(Question, NotebookId, confirm, why);
}

/// <summary>
/// The permanent-delete confirmation (5.6 plan Q1 c): asks the user, through
/// <see cref="IGrantPrompt.AskPermanentDeleteAsync"/>, to type the name of the object that will go. The trimmed typed
/// text must equal <see cref="PermanentDeleteQuestion.ExpectedText"/>, ignoring case, as AD-21's typed-name check does.
/// Returns only on a match; a decline, a cancel, a mismatch or a client without elicitation throws
/// <see cref="PermanentDeleteUnconfirmed"/> (<see cref="ErrorCode.PermanentDeleteNotGranted"/>), saying why. Nothing is
/// stored: a confirmation covers this call only, and AD-6's Limits apply (an elicitation hook can answer it). The caller
/// holds no STA call, no write lock and no settings mutex while the user decides, and passes the client's token.
/// </summary>
public static class PermanentDeleteDialog
{
    public static async Task AskAsync(IGrantPrompt prompt, PermanentDeleteUnconfirmed unconfirmed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(unconfirmed);

        GrantPromptResult answer;
        try
        {
            answer = await prompt.AskPermanentDeleteAsync(unconfirmed.Question, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            answer = new GrantPromptResult(GrantPromptStatus.Cancelled);
        }

        switch (answer.Status)
        {
            case GrantPromptStatus.Unsupported:
                throw unconfirmed.Because("this client can't show the dialog in which the user confirms it");
            case GrantPromptStatus.Declined:
                throw unconfirmed.Because("the user declined the confirmation");
            case GrantPromptStatus.Cancelled:
                throw unconfirmed.Because("the confirmation was cancelled");
        }

        if (!DialogText.Matches(answer.TypedName, unconfirmed.Question.ExpectedText))
        {
            throw unconfirmed.Because("the name typed in the confirmation didn't match");
        }
    }
}
