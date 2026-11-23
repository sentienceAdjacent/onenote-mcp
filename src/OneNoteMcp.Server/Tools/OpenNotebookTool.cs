using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OneNoteMcp.Core.UseCases;

namespace OneNoteMcp.Server.Tools;

/// <summary><c>open_notebook</c> (story 4.5). Thin (AD-5): the policy is in <see cref="OpenNotebookUseCase"/>. User-visible (AD-11).</summary>
[McpServerToolType]
public sealed class OpenNotebookTool(ToolRunner runner, OpenNotebookUseCase useCase)
{
    public const string Name = OpenNotebookUseCase.OperationName;

    [McpServerTool(
        Name = Name,
        ReadOnly = false,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope))]
    [McpMeta(ToolMetaKeys.MaxResultSizeChars, ToolMetaKeys.MaxResultSizeCharsValue)]
    [Description(
        "User-visible: opens an existing local notebook in OneNote by its folder, which adds it to OneNote's notebook list. Call only when the user asks to open a notebook, for example one close_notebook closed (pass its meta.path). "
        + "It never creates anything: the path must be the full local path (C:\\…) of a folder that holds a notebook (a .onetoc2 file) and isn't inside another notebook's folder. A URL, a network, device or relative path, a missing path, a file (.one, .onepkg, .onetoc2 or any other), a folder inside a OneDrive-synced folder, a folder with no .onetoc2 and a section group's folder are InvalidArgument, and nothing is sent to OneNote; the userAction says what to do instead (a notebook stored online, or one in OneDrive, the user opens in OneNote; a package goes to open_package). "
        + "It needs no write permission, gives none and changes no content. A notebook already open at that path (or through a link to it) is answered with its current ID and the warning alreadyExisted, and nothing is opened. "
        + "The notebook gets new IDs each time it is opened: use meta.objectId, not IDs from before it was closed. Its password-protected sections, and any notebook under the server's own protected folders, still need the user's read grant. "
        + "It has a 120 s deadline. A timeout, a lost connection or a cancel while OneNote is opening it returns outcomeUnknown and is never retried: re-read the open notebooks (get_hierarchy with scope notebooks) before anything else. "
        + "Success: {ok:true, xml:\"<the opened notebook's one:Notebook element: name, path, ID>\" (absent when it couldn't be read back), meta:{correlationId, objectId, path}, warnings:[{code:\"notebookListMayLag\", message, userAction}]}: its sections may take a few seconds to list. meta.path is the folder as the server resolved it (links followed). "
        + "Failure (isError): {ok:false, error:{code, hresult?, retryable, outcomeUnknown?, userAction, message}, meta:{correlationId}}.")]
    public Task<CallToolResult> OpenNotebook(
        [Description("Full local path (C:\\…) of the notebook's folder, such as close_notebook's meta.path. Never a URL or a file.")] string path,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync(
            Name,
            useCase,
            (context, ct) => useCase.ExecuteAsync(context, path ?? string.Empty, ct),
            cancellationToken);
}
