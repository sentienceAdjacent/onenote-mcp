using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OneNoteMcp.Core.Access;

namespace OneNoteMcp.Server.Grants;

/// <summary>
/// <c>create_notebook</c>'s location dialog over MCP elicitation (Phill, 2026-11-13, 5.3 plan Q3): the
/// <see cref="INotebookLocationPrompt"/> adapter, so Core never sees the SDK. The form names the notebook and offers a
/// <c>location</c> choice (a local folder always; the user's OneDrive only when the question allows it, which it doesn't
/// while <see cref="Core.CloudNotebookCreate"/> is closed) and an editable <c>folder</c> field, prefilled with the
/// question's folder. The user, not the model, decides where the notebook goes; that is what makes the new notebook's
/// automatic write grant safe. Supported means the client declared elicitation; an <see cref="InvalidOperationException"/>
/// from the SDK also counts as unsupported.
/// </summary>
public sealed class ElicitationNotebookLocationPrompt(McpServer server) : INotebookLocationPrompt
{
    public const string LocationField = "location";
    public const string FolderField = "folder";

    /// <summary>The prompt for a tool call: null when the SDK bound no server (a tool built by hand), so the call can't ask.</summary>
    public static INotebookLocationPrompt? For(McpServer? server) => server is null ? null : new ElicitationNotebookLocationPrompt(server);

    public async Task<NotebookLocationResult> AskAsync(NotebookLocationQuestion question, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(question);
        if (server.ClientCapabilities?.Elicitation is null)
        {
            return NotebookLocationResult.Unsupported;
        }

        ElicitResult result;
        try
        {
            result = await server.ElicitAsync(Request(question), ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            return NotebookLocationResult.Unsupported;
        }
        catch (McpException)
        {
            // The client answered the request with an error: nothing is created.
            return new NotebookLocationResult(GrantPromptStatus.Cancelled);
        }

        return Read(result);
    }

    /// <summary>The elicitation form. Public for tests, which have no in-process MCP client.</summary>
    public static ElicitRequestParams Request(NotebookLocationQuestion question)
    {
        ArgumentNullException.ThrowIfNull(question);
        List<ElicitRequestParams.EnumSchemaOption> options =
        [
            new() { Const = NotebookLocations.Local, Title = "A folder on this PC" },
        ];
        if (question.OfferOneDrive)
        {
            options.Add(new() { Const = NotebookLocations.OneDrive, Title = "My OneDrive" });
        }

        return new ElicitRequestParams
        {
            Message =
                $"Claude wants to create a new OneNote notebook named \"{question.NotebookName}\". Choose where it goes: "
                + $"it is created in a new folder named \"{question.NotebookName}\" inside the folder you enter, which must exist and must not be inside another notebook. "
                // A placeholder, never the name: the model chose it, and $(...) would run in PowerShell inside double quotes.
                + "Claude will be able to write to this notebook until you revoke it with " + GrantCli.Run("--revoke --write \"<notebook name>\"") + ". "
                + "Decline to create nothing. Only you choose the location: the model can't.",
            RequestedSchema = new ElicitRequestParams.RequestSchema
            {
                Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>
                {
                    [LocationField] = new ElicitRequestParams.TitledSingleSelectEnumSchema
                    {
                        Title = "Location",
                        Description = question.OfferOneDrive ? "Where the notebook is stored." : "Where the notebook is stored. Only a folder on this PC is available for now.",
                        OneOf = options,
                        Default = NotebookLocations.Local,
                    },
                    [FolderField] = new ElicitRequestParams.StringSchema
                    {
                        Title = "Folder",
                        Description = "The folder on this PC the notebook's folder is created in, such as C:\\Users\\you\\Documents\\OneNote Notebooks.",
                        Default = question.Folder,
                    },
                },
                Required = question.OfferOneDrive ? [LocationField] : [LocationField, FolderField],
            },
        };
    }

    /// <summary>
    /// The dialog's outcome from the client's answer: <c>oneDrive</c> for that location (which Core refuses while the cloud
    /// seam is closed, even if a client sends it unoffered); anything else means a local folder, read from the
    /// <c>folder</c> field. Public for tests.
    /// </summary>
    public static NotebookLocationResult Read(ElicitResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        switch (result.Action)
        {
            case "decline":
                return new NotebookLocationResult(GrantPromptStatus.Declined);
            case "accept":
                break;
            default:
                return new NotebookLocationResult(GrantPromptStatus.Cancelled);
        }

        string? location = null;
        string? folder = null;
        if (result.Content is { } content)
        {
            if (content.TryGetValue(LocationField, out var l) && l.ValueKind == JsonValueKind.String)
            {
                location = l.GetString();
            }

            if (content.TryGetValue(FolderField, out var f) && f.ValueKind == JsonValueKind.String)
            {
                folder = f.GetString();
            }
        }

        return location == NotebookLocations.OneDrive
            ? new NotebookLocationResult(GrantPromptStatus.Accepted, NotebookLocation.OneDrive)
            : new NotebookLocationResult(GrantPromptStatus.Accepted, NotebookLocation.Local, folder);
    }
}
