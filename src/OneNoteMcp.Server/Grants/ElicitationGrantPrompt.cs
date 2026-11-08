using System.Globalization;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OneNoteMcp.Core.Access;

namespace OneNoteMcp.Server.Grants;

/// <summary>
/// AD-21's grant dialog over MCP elicitation: the <see cref="IGrantPrompt"/> adapter, so Core never sees the SDK. The form
/// names the section and its notebook, asks the user to type the section's name, and offers the three lifetimes, with
/// the hours as a number; the <c>once</c> option names its window. The typed-name field gets a new name on every request
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
    public const string HoursField = "hours";

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
                        Description = "Type the protected section's name to confirm.",
                        MinLength = 1,
                    },
                    [LifetimeField] = new ElicitRequestParams.TitledSingleSelectEnumSchema
                    {
                        Title = "Allow for",
                        Description = "How long Claude may read this section.",
                        OneOf =
                        [
                            new ElicitRequestParams.EnumSchemaOption { Const = ReadGrantLifetimes.Once, Title = OnceTitle(question.OnceMinutes) },
                            new ElicitRequestParams.EnumSchemaOption { Const = ReadGrantLifetimes.Hours, Title = "A number of hours" },
                            new ElicitRequestParams.EnumSchemaOption { Const = ReadGrantLifetimes.UntilRevoked, Title = "Until I revoke it" },
                        ],
                        Default = ReadGrantLifetimes.Once,
                    },
                    [HoursField] = new ElicitRequestParams.NumberSchema
                    {
                        Type = "integer",
                        Title = "Hours",
                        Description = string.Create(
                            CultureInfo.InvariantCulture,
                            $"Used with \"A number of hours\": {ReadGrantLifetimes.MinHours} to {ReadGrantLifetimes.MaxHours}."),
                        Minimum = ReadGrantLifetimes.MinHours,
                        Maximum = ReadGrantLifetimes.MaxHours,
                        Default = ReadGrantLifetimes.DefaultHours,
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
        int? hours = null;
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

            if (content.TryGetValue(HoursField, out var h) && h.ValueKind == JsonValueKind.Number)
            {
                // A fraction is out of range, so Core grants nothing rather than rounding.
                hours = h.TryGetInt32(out var whole) ? whole : -1;
            }
        }

        // An unknown lifetime is never widened: it grants the narrowest lifetime, the once window.
        var parsed = ReadGrantLifetimes.Parse(lifetimeName) ?? ReadGrantLifetime.Once;
        return new GrantPromptResult(GrantPromptStatus.Accepted, typed, parsed, hours);
    }
}
