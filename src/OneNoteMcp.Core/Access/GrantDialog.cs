using OneNoteMcp.Core.LargeResults;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Xml;

namespace OneNoteMcp.Core.Access;

/// <summary>
/// The grant dialog of a target-scoped read (AD-21): asks the user through <see cref="IGrantPrompt"/>, then opens a
/// <c>once</c> window or stores an <c>hours</c> or <c>untilRevoked</c> grant. The section's location is read before the
/// dialog, so no gateway call is in flight and no settings lock is held while the user decides. A grant that can't be
/// saved opens a <c>once</c> window instead, with <c>grantNotSaved</c>.
/// </summary>
internal sealed class GrantDialog(HierarchyWalk walk, OnceWindows windows, SettingsWriter? writer, TimeProvider clock)
{
    internal async Task<TargetAccess> AskAsync(ToolCallContext context, HierarchyNode section, string key, ReadGrantSettings readGrants, CancellationToken ct)
    {
        if (context.Prompt is not { } prompt)
        {
            throw ReadAccess.NotGranted(section.Id, "This client can't show the grant dialog.");
        }

        // An empty path gives an empty key, which settings.json can't hold (AD-21): no grant, and no dialog.
        if (key.Length == 0)
        {
            throw ReadAccess.NotGranted(section.Id, "OneNote reports no path for this section, so no grant can be made for it.");
        }

        // Read before the dialog: no gateway call is in flight and no lock is held while the user decides.
        var (notebook, groups) = await walk.LocationAsync(section.Id, ct).ConfigureAwait(false);

        // Every name is model-writable (5.6 follow-up): the dialog shows each cleaned, and the user types the shown name.
        var question = new GrantQuestion(
            section.Id,
            DialogText.Shown(section.Name, out var expected),
            DialogText.Shown(notebook?.DisplayName, out _),
            [.. groups.Select(g => DialogText.Shown(g, out _))],
            expected,
            readGrants.OnceMinutes);
        GrantPromptResult answer;
        try
        {
            answer = await prompt.AskAsync(question, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            answer = new GrantPromptResult(GrantPromptStatus.Cancelled);
        }

        switch (answer.Status)
        {
            case GrantPromptStatus.Unsupported:
                throw ReadAccess.NotGranted(section.Id, "This client can't show the grant dialog.");
            case GrantPromptStatus.Declined:
                throw ReadAccess.NotGranted(section.Id, "The user declined the grant dialog.");
            case GrantPromptStatus.Cancelled:
                throw ReadAccess.NotGranted(section.Id, "The grant dialog was cancelled.");
        }

        // The trimmed typed text must equal the section's name as the dialog showed it, ignoring case; anything else makes
        // no grant (AD-21). The grant itself is keyed by the section's path, never by the shown text.
        if (!DialogText.Matches(answer.TypedName, question.ExpectedText))
        {
            throw ReadAccess.NotGranted(section.Id, "The name typed in the grant dialog didn't match the section's name, so nothing was granted.");
        }

        var notebookPath = notebook?.Path ?? string.Empty;
        var lifetime = answer.Lifetime ?? ReadGrantLifetime.Once;
        if (lifetime == ReadGrantLifetime.Once)
        {
            return new TargetAccess(OpenWindow(key, section, notebookPath, readGrants), []);
        }

        var hours = answer.Hours ?? ReadGrantLifetimes.DefaultHours;
        if (lifetime == ReadGrantLifetime.Hours && hours is < ReadGrantLifetimes.MinHours or > ReadGrantLifetimes.MaxHours)
        {
            throw ReadAccess.NotGranted(section.Id, $"The grant dialog's hours must be from {ReadGrantLifetimes.MinHours} to {ReadGrantLifetimes.MaxHours}, so nothing was granted.");
        }

        var now = clock.GetUtcNow();
        var grant = ProtectedSectionGrant.Create(
            key,
            section.Name,
            section.Id,
            notebookPath,
            now,
            GrantedVia.Elicitation,
            lifetime == ReadGrantLifetime.Hours ? now.AddHours(hours) : null);

        var notSaved = Save(grant, section.Id, readGrants);
        if (notSaved is not null)
        {
            // Works like once: a window in memory only (AD-21).
            return new TargetAccess(OpenWindow(key, section, notebookPath, readGrants), [notSaved]);
        }

        return new TargetAccess(ReadAccess.Allowing(grant, section.Id, wholeResult: true), []);
    }

    /// <summary>Opens (or reopens) <paramref name="key"/>'s window from now, the moment the user's answer arrived.</summary>
    private ResultGrant OpenWindow(string key, HierarchyNode section, string notebookPath, ReadGrantSettings readGrants)
    {
        var now = clock.GetUtcNow();
        windows.Open(ProtectedSectionGrant.Create(key, section.Name, section.Id, notebookPath, now, GrantedVia.Elicitation, now + readGrants.OnceWindow));
        return new ResultGrant(key, section.Id, ReadGrantLifetime.Once, null, WholeResult: true);
    }

    /// <summary>Stores the grant; returns the <c>grantNotSaved</c> warning when it can't, or null.</summary>
    private ToolWarning? Save(ProtectedSectionGrant grant, string sectionId, ReadGrantSettings readGrants)
    {
        string reason;
        if (writer is null)
        {
            reason = "this server has nowhere to store grants";
        }
        else
        {
            try
            {
                writer.SaveReadGrant(grant);
                return null;
            }
            catch (OneNoteException ex)
            {
                reason = ex.Message;
            }
        }

        return new ToolWarning(
            ToolWarnings.GrantNotSaved,
            $"The read grant for section {sectionId} couldn't be saved ({reason}), so it works like a one-time grant: it covers that section for {readGrants.OnceMinutes} minute(s), in this server only.",
            "Fix settings.json (or update OneNoteMcp if a newer version wrote it), then grant again with " + GrantCli.Run("--read-section \"<section name>\"") + ".");
    }
}
