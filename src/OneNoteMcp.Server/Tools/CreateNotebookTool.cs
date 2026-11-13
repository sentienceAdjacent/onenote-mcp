using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Server.Grants;

namespace OneNoteMcp.Server.Tools;

/// <summary><c>create_notebook</c>; see <see cref="CreateSectionTool"/>. It asks the user where the notebook goes.</summary>
[McpServerToolType]
public sealed class CreateNotebookTool(ToolRunner runner, CreateHierarchyUseCase useCase)
{
    public const string Name = "create_notebook";

    [McpServerTool(
        Name = Name,
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope))]
    [McpMeta(ToolMetaKeys.MaxResultSizeChars, ToolMetaKeys.MaxResultSizeCharsValue)]
    [Description(
        "Writes to OneNote: creates a new notebook, opens it in OneNote and returns its ID in meta.objectId and its path in meta.path. Call it only when the user asks for a new notebook. "
        + "The user is shown a dialog to choose where it goes, prefilled with folder (or OneNote's default notebook folder); the notebook is made in a new folder named name inside the folder they choose. "
        + "A client that can't show the dialog fails with NotSupported, and so does a OneDrive (URL) location, for now; a declined dialog fails with Cancelled. Nothing is created in those cases. "
        + "name is one name: no path separators, no '..', no < > : \" | ? *, no leading or trailing space or dot. "
        + "The notebook's folder must not exist yet or be empty (FileExists otherwise), and must not be on a network share or inside another notebook (InvalidArgument). "
        + "The new notebook gets the user's write grant automatically. If that can't be saved, the notebook is still created, with the warning createdNotGranted. "
        + "COM can't delete a notebook: only the user can close it in OneNote and delete its folder. "
        + "If the call fails with outcomeUnknown: true, the notebook may or may not exist: re-read the notebooks before trying again. "
        + "Success: {ok:true, meta:{correlationId, path, objectId}, warnings:[]}. "
        + "Failure (isError): {ok:false, error:{code, hresult?, retryable, outcomeUnknown?, userAction, message}, meta:{correlationId}}.")]
    public Task<CallToolResult> CreateNotebook(
        [Description("The new notebook's name, such as Trips. It is also the name of its folder.")] string name,
        [Description("Optional. A folder on this PC to propose to the user, such as C:\\Users\\me\\Documents\\OneNote Notebooks. The user can change it.")] string? folder = null,
        CancellationToken cancellationToken = default,
        McpServer? server = null) =>
        runner.RunAsync(
            Name,
            useCase,
            (context, ct) => useCase.CreateNotebookAsync(context, name ?? string.Empty, folder, ElicitationNotebookLocationPrompt.For(server), ct),
            // No write-grant dialog: a new notebook needs no grant (AD-6); it asks for its location instead.
            cancellationToken);
}
