using OneNoteMcp.Core.Settings;

namespace OneNoteMcp.Core.Access;

/// <summary>
/// The write-grant dialog (AD-6), raised by AD-7 step 1 through <see cref="Writes.WritePipeline"/> when a write's
/// notebook has a path but no write grant (<see cref="WriteGrantMissing"/>). It asks the user through
/// <see cref="IGrantPrompt.AskWriteAsync"/> to type the notebook's name, then re-reads the open notebooks
/// (<c>GetHierarchy("", sections)</c>) and matches the typed text with <see cref="GrantKeys.NameMatches"/> against each
/// displayed name. Exactly one match, and that match the target notebook, stores <c>{write: true, grantedVia:
/// elicitation}</c> through <see cref="SettingsWriter.SaveWriteGrant"/>, which keeps an existing <c>permanentDelete</c>.
/// Anything else makes no grant and is <see cref="ErrorCode.WriteNotGranted"/>, retryable, with
/// <see cref="WriteAccess.NotGrantedUserAction"/>; the message says why and carries the notebook's ID only (AD-9). A save
/// that fails keeps its own error (<see cref="ErrorCode.SettingsInvalid"/> or <see cref="ErrorCode.ComBusy"/>); there is
/// no <c>once</c> fallback for writes. The caller holds no STA call, no write lock and no settings mutex while the user
/// decides (AD-6, AD-10, AD-18), and passes the client's token, never the deadline's.
/// </summary>
public sealed class WriteGrantDialog
{
    private readonly IOneNoteGateway gateway;
    private readonly SettingsWriter writer;
    private readonly TimeProvider clock;

    /// <param name="gateway">Re-reads the open notebooks after the user answers.</param>
    /// <param name="writer">Stores the grant.</param>
    /// <param name="clock">Gives <c>grantedAt</c>.</param>
    public WriteGrantDialog(IOneNoteGateway gateway, SettingsWriter writer, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(writer);
        this.gateway = gateway;
        this.writer = writer;
        this.clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Asks for the grant <paramref name="missing"/> lacks and stores it, or throws. Returns only when the grant was
    /// stored; the caller then runs step 1 once more, with no second dialog. A cancel of <paramref name="ct"/> (the
    /// client's) stays an <see cref="OperationCanceledException"/>; any other cancellation of the dialog is a cancel.
    /// </summary>
    internal async Task AskAsync(IGrantPrompt prompt, WriteGrantMissing missing, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(missing);

        GrantPromptResult answer;
        try
        {
            answer = await prompt.AskWriteAsync(new WriteGrantQuestion(missing.NotebookId, missing.DisplayName, missing.Path), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            answer = new GrantPromptResult(GrantPromptStatus.Cancelled);
        }

        switch (answer.Status)
        {
            case GrantPromptStatus.Unsupported:
                throw WriteAccess.NotGranted(missing.NotebookId, "this client can't show the grant dialog.");
            case GrantPromptStatus.Declined:
                throw WriteAccess.NotGranted(missing.NotebookId, "the user declined the grant dialog.");
            case GrantPromptStatus.Cancelled:
                throw WriteAccess.NotGranted(missing.NotebookId, "the grant dialog was cancelled.");
        }

        // The typed text is matched against every open notebook's displayed name, read again now (AD-6).
        var notebooks = OpenNotebooks.Find(await gateway.GetHierarchyAsync(string.Empty, HierarchyScope.Sections, ct).ConfigureAwait(false));
        var matches = notebooks.Where(n => GrantKeys.NameMatches(answer.TypedName, n.DisplayName)).ToList();
        switch (matches.Count)
        {
            case 0:
                throw WriteAccess.NotGranted(missing.NotebookId, "the name typed in the grant dialog matched no open notebook, so nothing was granted.");
            case > 1:
                throw WriteAccess.NotGranted(missing.NotebookId, "the name typed in the grant dialog matched more than one open notebook, so nothing was granted. The user can grant it with the grant command, which lists them.");
        }

        // One match, but another notebook: no grant (Phill, 2026-11-12, 2.6 plan Q2).
        var match = matches[0];
        if (!GrantKeys.Comparer.Equals(match.Key, missing.Key))
        {
            throw WriteAccess.NotGranted(missing.NotebookId, "the name typed in the grant dialog didn't match this notebook, so nothing was granted.");
        }

        writer.SaveWriteGrant(new NotebookGrant(
            missing.Key,
            match.DisplayName,
            match.Id,
            Write: true,
            PermanentDelete: false,
            clock.GetUtcNow(),
            GrantedVia.Elicitation));
    }
}
