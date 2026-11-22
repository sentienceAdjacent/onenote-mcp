using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OneNoteMcp.Core.UseCases;

namespace OneNoteMcp.Server.Tools;

/// <summary><c>SyncHierarchy</c> (story 4.3). Thin (AD-5): the policy is in <see cref="SyncHierarchyUseCase"/>. Not user-visible, but only on the user's request (AD-11).</summary>
[McpServerToolType]
public sealed class SyncHierarchyTool(ToolRunner runner, SyncHierarchyUseCase useCase)
{
    public const string Name = "sync_hierarchy";

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
        "Asks OneNote to sync a notebook, section group, section or page with where it is stored. Call only when the user asks for a sync; never on your own, after a write or otherwise. "
        + "It changes no content and returns none; it doesn't take focus or change what OneNote shows. "
        + "ok means OneNote accepted the request, not that a sync ran or finished: OneNote returns before any sync is done and offers no way to see one complete, so every ok carries the warning syncRequested. Tell the user the sync was requested; they can check it in OneNote under File > Info > View Sync Status. "
        + "An unknown ID, or a closed notebook's old ID, fails with ObjectNotFound: re-read the hierarchy and use a current ID. "
        + "It has a 120 s deadline. A timeout, a lost connection or a cancel while OneNote is handling the call returns outcomeUnknown and is never retried; a modal dialog open in OneNote is ModalDialogOpen, retryable once the user closes it. "
        + "Success: {ok:true, meta:{correlationId}, warnings:[{code:\"syncRequested\", message}]}. "
        + "Failure (isError): {ok:false, error:{code, hresult?, retryable, outcomeUnknown?, userAction, message}, meta:{correlationId}}.")]
    public Task<CallToolResult> SyncHierarchy(
        [Description("OneNote ID of the notebook, section group, section or page to sync.")] string hierarchyId,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync(
            Name,
            useCase,
            (context, ct) => useCase.ExecuteAsync(context, hierarchyId ?? string.Empty, ct),
            cancellationToken);
}
