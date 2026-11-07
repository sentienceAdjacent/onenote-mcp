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
/// the hours as a number. Supported means the client declared elicitation; an <see cref="InvalidOperationException"/>
/// from the SDK also counts as unsupported. Core matches the typed name, never this adapter.
/// </summary>
public sealed class ElicitationGrantPrompt(McpServer server) : IGrantPrompt
{
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

        ElicitResult result;
        try
        {
            result = await server.ElicitAsync(Request(question), ct).ConfigureAwait(false);
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

        return Read(result);
    }

    /// <summary>The elicitation form. Public for tests, which have no in-process MCP client.</summary>
    public static ElicitRequestParams Request(GrantQuestion question)
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
                    [SectionNameField] = new ElicitRequestParams.StringSchema
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
                            new ElicitRequestParams.EnumSchemaOption { Const = ReadGrantLifetimes.Once, Title = "This read only" },
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
                Required = [SectionNameField, LifetimeField],
            },
        };
    }

    /// <summary>The dialog's outcome from the client's answer. Public for tests.</summary>
    public static GrantPromptResult Read(ElicitResult result)
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
            if (content.TryGetValue(SectionNameField, out var name) && name.ValueKind == JsonValueKind.String)
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

        // An unknown lifetime is never widened: it grants this read only.
        var parsed = ReadGrantLifetimes.Parse(lifetimeName) ?? ReadGrantLifetime.Once;
        return new GrantPromptResult(GrantPromptStatus.Accepted, typed, parsed, hours);
    }
}
