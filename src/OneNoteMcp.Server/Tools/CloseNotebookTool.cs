using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OneNoteMcp.Core.UseCases;

namespace OneNoteMcp.Server.Tools;

/// <summary><c>CloseNotebook</c> (story 4.4). Thin (AD-5): the policy is in <see cref="CloseNotebookUseCase"/>. User-visible (AD-11).</summary>
[McpServerToolType]
public sealed class CloseNotebookTool(ToolRunner runner, CloseNotebookUseCase useCase)
{
    public const string Name = "close_notebook";

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
        "User-visible: closes a notebook in OneNote, which removes it from OneNote's notebook list; if OneNote was showing it, the view may go blank, and for a notebook stored online OneNote may show a \"syncing changes\" box until it is done. Call only when the user asks to close a notebook. "
        + "It takes an open notebook's ID only: a section, section group or page ID is InvalidArgument, and an ID no open notebook has (unknown, or a closed notebook's old one) is ObjectNotFound; re-read the open notebooks (get_hierarchy with scope notebooks) for a current ID. Nothing is changed then. "
        + "It needs no write permission and changes no content. It never forces the close: first it asks OneNote to sync the notebook and waits about 10 s, so a write just made isn't lost, then it closes it. "
        + "Every ID in the notebook changes when it is opened again: don't reuse the old ones. Its read and write grants are kept, matched by its path. "
        + "It has a 120 s deadline. If the sync or the wait before the close fails, times out or is cancelled, the notebook stays open and the error is never outcomeUnknown. A timeout, a lost connection or a cancel while OneNote is closing it returns outcomeUnknown and is never retried: re-read the open notebooks before anything else. When OneNote refuses the close, let the notebook finish syncing and retry, or ask the user to close it in OneNote. "
        + "Success: {ok:true, xml:\"<the closed notebook's one:Notebook element: name, path, ID>\", meta:{correlationId, objectId, path}, warnings:[{code:\"notebookClosed\", message, userAction}]}: tell the user it is closed, and use the warning's userAction to say how to reopen it. "
        + "Failure (isError): {ok:false, error:{code, hresult?, retryable, outcomeUnknown?, userAction, message}, meta:{correlationId}}.")]
    public Task<CallToolResult> CloseNotebook(
        [Description("OneNote ID of the open notebook to close, as get_hierarchy lists it now.")] string notebookId,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync(
            Name,
            useCase,
            (context, ct) => useCase.ExecuteAsync(context, notebookId ?? string.Empty, ct),
            cancellationToken);
}
