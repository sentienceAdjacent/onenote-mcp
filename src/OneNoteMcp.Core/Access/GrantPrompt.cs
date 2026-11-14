namespace OneNoteMcp.Core.Access;

/// <summary>How long a read grant lasts, chosen by the user when granting (AD-21).</summary>
public enum ReadGrantLifetime
{
    /// <summary>
    /// Every read of the section, target-scoped and broad, for <c>readGrants.onceMinutes</c> (default 5) from the user's
    /// answer, plus <c>read_result</c> parts of results made under it (AD-20). Memory only, never stored.
    /// </summary>
    Once,

    /// <summary>1 to 168 hours. Stored with <c>expiresAt</c> in UTC.</summary>
    Hours,

    /// <summary>Stored until <c>grant --revoke</c>.</summary>
    UntilRevoked,
}

/// <summary>The wire and <c>settings.json</c> names of <see cref="ReadGrantLifetime"/>.</summary>
public static class ReadGrantLifetimes
{
    public const string Once = "once";
    public const string Hours = "hours";
    public const string UntilRevoked = "untilRevoked";

    /// <summary>The shortest and longest <c>hours</c> grant, and the default the dialog offers (AD-21).</summary>
    public const int MinHours = 1;
    public const int MaxHours = 168;
    public const int DefaultHours = 8;

    public static string Name(ReadGrantLifetime lifetime) => lifetime switch
    {
        ReadGrantLifetime.Once => Once,
        ReadGrantLifetime.Hours => Hours,
        ReadGrantLifetime.UntilRevoked => UntilRevoked,
        _ => throw new ArgumentOutOfRangeException(nameof(lifetime), lifetime, "Unknown lifetime."),
    };

    public static ReadGrantLifetime? Parse(string? name) => name switch
    {
        Once => ReadGrantLifetime.Once,
        Hours => ReadGrantLifetime.Hours,
        UntilRevoked => ReadGrantLifetime.UntilRevoked,
        _ => null,
    };
}

/// <summary>How a grant was created, as <c>settings.json</c> records it (AD-6, AD-10).</summary>
public static class GrantedVia
{
    public const string Elicitation = "elicitation";
    public const string Cli = "cli";

    /// <summary>A write grant made automatically for a notebook the server created (AD-6, AD-7 <c>Create</c>). Never a read grant.</summary>
    public const string Created = "created";
}

/// <summary>
/// What the grant dialog shows the user (AD-21): the section and its notebook, by name, and the <c>once</c> window in
/// minutes. The names go to the user's client only, never into a tool result.
/// </summary>
public sealed record GrantQuestion(string SectionId, string SectionName, string NotebookName, IReadOnlyList<string> SectionGroups, int OnceMinutes = 5);

/// <summary>How the dialog ended.</summary>
public enum GrantPromptStatus
{
    /// <summary>The client doesn't declare elicitation, or refused the request as unsupported.</summary>
    Unsupported,

    /// <summary>The user declined.</summary>
    Declined,

    /// <summary>The user dismissed the dialog, or the client cancelled it (headless runs do).</summary>
    Cancelled,

    /// <summary>The user submitted the form. The typed name still has to match (AD-21).</summary>
    Accepted,
}

/// <summary>
/// The dialog's outcome. With <see cref="GrantPromptStatus.Accepted"/>, the typed text and, for a read grant, the chosen
/// lifetime.
/// </summary>
public sealed record GrantPromptResult(GrantPromptStatus Status, string? TypedName = null, ReadGrantLifetime? Lifetime = null, int? Hours = null)
{
    public static GrantPromptResult Unsupported { get; } = new(GrantPromptStatus.Unsupported);
}

/// <summary>
/// What the write-grant dialog shows the user (AD-6): the notebook by its displayed name (<c>nickname</c>, else
/// <c>name</c>) and its live path. The name and path go to the user's client only, never into a tool result (AD-9).
/// Write grants have no lifetime (AD-10): the dialog says the grant lasts until revoked.
/// </summary>
public sealed record WriteGrantQuestion(string NotebookId, string NotebookName, string NotebookPath);

/// <summary>
/// The port through which the server asks the user for a grant: a read grant for a target-scoped read (AD-21), or a
/// write grant at AD-7 step 1 (AD-6). The Server adapts MCP elicitation to it, so Core stays free of the SDK. The caller
/// holds no STA call, no write lock and no settings mutex while it waits, and the wait counts against no timeout.
/// </summary>
public interface IGrantPrompt
{
    Task<GrantPromptResult> AskAsync(GrantQuestion question, CancellationToken ct);

    /// <summary>
    /// Asks for a write grant on a notebook (AD-6). An <see cref="GrantPromptStatus.Accepted"/> result carries only the
    /// typed name: <see cref="GrantPromptResult.Lifetime"/> is null, since write grants last until revoked.
    /// </summary>
    Task<GrantPromptResult> AskWriteAsync(WriteGrantQuestion question, CancellationToken ct);

    /// <summary>
    /// Asks the user to confirm that a page write may read the files <see cref="FilePathQuestion.Paths"/> into a page
    /// (AD-7 user-gated file paths; Phill, 2026-11-12, story 2.9 security review). <see cref="GrantPromptStatus.Accepted"/>
    /// means the user confirmed every path; nothing is stored. A prompt that can't ask returns
    /// <see cref="GrantPromptStatus.Unsupported"/>, the default.
    /// </summary>
    Task<GrantPromptResult> AskFilePathsAsync(FilePathQuestion question, CancellationToken ct) => Task.FromResult(GrantPromptResult.Unsupported);

    /// <summary>
    /// Asks the user to confirm one permanent delete by typing the object's name (AD-6; Phill, 2026-11-13, 5.6 plan Q1 c),
    /// when its notebook has no stored permanent-delete grant. An <see cref="GrantPromptStatus.Accepted"/> result carries
    /// the typed text only; nothing is stored. A prompt that can't ask returns <see cref="GrantPromptStatus.Unsupported"/>,
    /// the default.
    /// </summary>
    Task<GrantPromptResult> AskPermanentDeleteAsync(PermanentDeleteQuestion question, CancellationToken ct) => Task.FromResult(GrantPromptResult.Unsupported);
}

/// <summary>
/// What the permanent-delete confirmation shows the user (5.6 plan Q1 c): what will go, by its kind (<c>page</c>,
/// <c>section</c> or <c>section group</c>) and name, and where it is: its notebook by name and by
/// <see cref="NotebookPath"/> (the grant key, which no tool can change), a page's <see cref="SectionName"/>, a section's
/// <see cref="PageCount"/>, and a page's <see cref="LastModified"/> date (UTC, to the second) when another page in its section
/// shows the same name, so two such dialogs differ; plus <see cref="ExpectedText"/>, what the user must type: the object's name, or <c>delete</c>
/// for an object with no name (an untitled page). Every text is already made safe to show
/// (<see cref="PermanentDeleteDialog.Shown"/>), and <see cref="ExpectedText"/> comes from the same text as
/// <see cref="ObjectName"/>. The names go to the user's client only, never into a tool result (AD-9).
/// </summary>
public sealed record PermanentDeleteQuestion(
    string ObjectId,
    string Kind,
    string ObjectName,
    string NotebookName,
    string ExpectedText,
    string NotebookPath = "",
    string? SectionName = null,
    int? PageCount = null,
    string? LastModified = null);

/// <summary>
/// What the file-path dialog shows the user (AD-7): the page's ID and each new path, canonical (links and junctions
/// resolved). The paths go to the user's client only, never into a tool result or a log (AD-9, AD-13).
/// </summary>
public sealed record FilePathQuestion(string PageId, IReadOnlyList<string> Paths);
