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

/// <summary>The dialog's outcome. With <see cref="GrantPromptStatus.Accepted"/>, the typed text and the chosen lifetime.</summary>
public sealed record GrantPromptResult(GrantPromptStatus Status, string? TypedName = null, ReadGrantLifetime? Lifetime = null, int? Hours = null)
{
    public static GrantPromptResult Unsupported { get; } = new(GrantPromptStatus.Unsupported);
}

/// <summary>
/// The port through which a target-scoped read asks the user for a read grant (AD-21). The Server adapts MCP
/// elicitation to it, so Core stays free of the SDK. The caller holds no STA call and no settings mutex while it waits,
/// and the wait counts against no timeout.
/// </summary>
public interface IGrantPrompt
{
    Task<GrantPromptResult> AskAsync(GrantQuestion question, CancellationToken ct);
}
