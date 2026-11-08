using System.Globalization;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OneNoteMcp.Core.Access;

namespace OneNoteMcp.Server.Grants;

/// <summary>
/// AD-21's grant dialog over MCP elicitation: the <see cref="IGrantPrompt"/> adapter, so Core never sees the SDK. The form
/// names the section and its notebook, asks the user to type the section's name, and offers one list of lifetimes
/// (Phill, 2026-11-07): the <c>once</c> window, named with its minutes; 1, 8 or 24 hours or 7 days; or until revoked. Other
/// hour counts go through the CLI. The typed-name hint names the section. The typed-name field gets a new name on every request
/// (<c>sectionName_</c> plus 8 random hex digits), so a client can't pre-fill it from an earlier answer, and the typed
/// name is read only from that field (Phill, 2026-11-07). Supported means the client declared elicitation; an
/// <see cref="InvalidOperationException"/> from the SDK also counts as unsupported. Core matches the typed name, never
/// this adapter.
/// </summary>
public sealed class ElicitationGrantPrompt(McpServer server) : IGrantPrompt
{
    /// <summary>The prefix of the typed-name field's per-request name.</summary>
    public const string SectionNameField = "sectionName";
    public const string LifetimeField = "lifetime";
    /// <summary>The hour counts the dialog's one lifetime list offers (Phill, 2026-11-07); other counts go through the CLI.</summary>
    public static IReadOnlyList<(int Hours, string Title)> HourChoices { get; } = [(1, "1 hour"), (8, "8 hours"), (24, "24 hours"), (168, "7 days")];

    /// <summary>The const of an <c>hours</c> option: <c>hours:</c> and the count.</summary>
    public static string HoursConst(int hours) => string.Create(CultureInfo.InvariantCulture, $"{ReadGrantLifetimes.Hours}:{hours}");

    /// <summary>The prompt for a tool call: null when the SDK bound no server (a tool built by hand), so the call can't ask.</summary>
    public static IGrantPrompt? For(McpServer? server) => server is null ? null : new ElicitationGrantPrompt(server);

    public async Task<GrantPromptResult> AskAsync(GrantQuestion question, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(question);
        if (server.ClientCapabilities?.Elicitation is null)
        {
            return GrantPromptResult.Unsupported;
        }

        var field = NewSectionNameField();
        ElicitResult result;
        try
        {
            result = await server.ElicitAsync(Request(question, field), ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            return GrantPromptResult.Unsupported;
        }
        catch (McpException)
        {
            // The client answered the request with an error: no grant.
            return new GrantPromptResult(GrantPromptStatus.Cancelled);
        }

        return Read(result, field);
    }

    /// <summary>A fresh typed-name field name: <c>sectionName_</c> plus 8 random lowercase hex digits.</summary>
    public static string NewSectionNameField() =>
        SectionNameField + "_" + System.Security.Cryptography.RandomNumberGenerator.GetHexString(8, lowercase: true);

    /// <summary>The title of the <c>once</c> option, which names its window (AD-21).</summary>
    public static string OnceTitle(int minutes) =>
        string.Create(CultureInfo.InvariantCulture, $"This section, for {minutes} minute{(minutes == 1 ? string.Empty : "s")}");

    /// <summary>The elicitation form, with the typed-name field named <paramref name="sectionNameField"/>. Public for tests, which have no in-process MCP client.</summary>
    public static ElicitRequestParams Request(GrantQuestion question, string sectionNameField)
    {
        ArgumentNullException.ThrowIfNull(question);
        var where = string.IsNullOrEmpty(question.NotebookName) ? string.Empty : $" in the notebook \"{question.NotebookName}\"";
        if (question.SectionGroups.Count > 0)
        {
            where += $" (section group {string.Join(" > ", question.SectionGroups.Select(g => $"\"{g}\""))})";
        }

        return new ElicitRequestParams
        {
            Message =
                $"Claude wants to read the password-protected OneNote section \"{question.SectionName}\"{where}. "
                + "To allow it, type the section's name exactly as it appears in OneNote and choose for how long. "
                + "Decline to keep it private. Only you can grant this: the model can't.",
            RequestedSchema = new ElicitRequestParams.RequestSchema
            {
                Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>
                {
                    [sectionNameField] = new ElicitRequestParams.StringSchema
                    {
                        Title = "Section name",
                        Description = $"Type \"{question.SectionName}\" to confirm.",
                        MinLength = 1,
                    },
                    [LifetimeField] = new ElicitRequestParams.TitledSingleSelectEnumSchema
                    {
                        Title = "Allow for",
                        Description = "How long Claude may read this section.",
                        OneOf =
                        [
                            new ElicitRequestParams.EnumSchemaOption { Const = ReadGrantLifetimes.Once, Title = OnceTitle(question.OnceMinutes) },
                            .. HourChoices.Select(c => new ElicitRequestParams.EnumSchemaOption { Const = HoursConst(c.Hours), Title = c.Title }),
                            new ElicitRequestParams.EnumSchemaOption { Const = ReadGrantLifetimes.UntilRevoked, Title = "Until I revoke it" },
                        ],
                        Default = ReadGrantLifetimes.Once,
                    },
                },
                Required = [sectionNameField, LifetimeField],
            },
        };
    }

    /// <summary>
    /// The dialog's outcome from the client's answer. The typed name is read only from <paramref name="sectionNameField"/>,
    /// the field named in that request; any other key gives no typed name, so a mismatch. Public for tests.
    /// </summary>
    public static GrantPromptResult Read(ElicitResult result, string sectionNameField)
    {
        ArgumentNullException.ThrowIfNull(result);
        switch (result.Action)
        {
            case "decline":
                return new GrantPromptResult(GrantPromptStatus.Declined);
            case "accept":
                break;
            default:
                return new GrantPromptResult(GrantPromptStatus.Cancelled);
        }

        var content = result.Content;
        string? typed = null;
        string? lifetimeName = null;
        if (content is not null)
        {
            if (content.TryGetValue(sectionNameField, out var name) && name.ValueKind == JsonValueKind.String)
            {
                typed = name.GetString();
            }

            if (content.TryGetValue(LifetimeField, out var lifetime) && lifetime.ValueKind == JsonValueKind.String)
            {
                lifetimeName = lifetime.GetString();
            }

        }

        // One of the offered options; anything else is never widened: it grants the narrowest lifetime, the once window.
        if (lifetimeName == ReadGrantLifetimes.UntilRevoked)
        {
            return new GrantPromptResult(GrantPromptStatus.Accepted, typed, ReadGrantLifetime.UntilRevoked);
        }

        foreach (var (hours, _) in HourChoices)
        {
            if (lifetimeName == HoursConst(hours))
            {
                return new GrantPromptResult(GrantPromptStatus.Accepted, typed, ReadGrantLifetime.Hours, hours);
            }
        }

        return new GrantPromptResult(GrantPromptStatus.Accepted, typed, ReadGrantLifetime.Once);
    }
}
