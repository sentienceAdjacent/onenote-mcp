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

        if (!Matches(answer.TypedName, unconfirmed.Question.ExpectedText))
        {
            throw unconfirmed.Because("the name typed in the confirmation didn't match");
        }
    }

    /// <summary>How many characters of a name the confirmation shows, the ellipsis included.</summary>
    public const int MaxNameLength = 80;

    /// <summary>How many characters of the notebook's path the confirmation shows, the ellipsis included.</summary>
    public const int MaxPathLength = 260;

    /// <summary>
    /// <paramref name="text"/> as the confirmation may show it (a review finding: the names are model-writable, so they mustn't
    /// be able to forge the dialog's sentence or hide what goes): control, format (Unicode Cf, bidi controls included), line
    /// and paragraph separator characters dropped; <c>"</c> replaced by <c>'</c>; trimmed; and cut to
    /// <paramref name="max"/> characters, ending in <c>…</c>, without splitting a surrogate pair. <paramref name="typed"/> is
    /// what the user types to confirm it: the same text, without the ellipsis when it was cut.
    /// </summary>
    public static string Shown(string? text, out string typed, int max = MaxNameLength)
    {
        var clean = new System.Text.StringBuilder();
        foreach (var rune in (text ?? string.Empty).EnumerateRunes())
        {
            var category = System.Text.Rune.GetUnicodeCategory(rune);
            if (category is System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.Format
                or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator)
            {
                continue;
            }

            clean.Append(rune.Value == '"' ? "'" : rune.ToString());
        }

        var shown = clean.ToString().Trim();
        if (shown.Length <= max)
        {
            typed = shown;
            return shown;
        }

        var cut = max - 1;
        if (char.IsHighSurrogate(shown[cut - 1]))
        {
            cut--;
        }

        typed = shown[..cut].TrimEnd();
        return typed + "…";
    }

    /// <summary>The typed-text check: trimmed, compared ignoring case; an empty answer never matches.</summary>
    public static bool Matches(string? typed, string expected) =>
        typed?.Trim() is { Length: > 0 } text && string.Equals(text, expected?.Trim(), StringComparison.OrdinalIgnoreCase);
}
