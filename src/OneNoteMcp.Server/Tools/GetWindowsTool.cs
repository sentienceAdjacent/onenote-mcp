using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OneNoteMcp.Core.UseCases;

namespace OneNoteMcp.Server.Tools;

/// <summary>
/// <c>get_windows</c> (story 4.6; AD-14 names it by intent). Thin (AD-5): the policy is in <see cref="GetWindowsUseCase"/>.
/// Not user-visible (AD-11, spike 4.12 W2), but only on the user's request.
/// </summary>
[McpServerToolType]
public sealed class GetWindowsTool(ToolRunner runner, GetWindowsUseCase useCase)
{
    public const string Name = "get_windows";

    [McpServerTool(
        Name = Name,
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope))]
    [McpMeta(ToolMetaKeys.MaxResultSizeChars, ToolMetaKeys.MaxResultSizeCharsValue)]
    [Description(
        "Read-only. Lists OneNote's open windows and what each shows, in meta.windows. Call it only when the user asks, or refers to what they're looking at in OneNote (\"this page\", \"the page I'm on\"); never on your own. "
        + "It doesn't take focus or change what OneNote shows. "
        + "Each window: {handle, current, active, fullPageView, dockedLocation (default, none, left, right, top or bottom), sideNote, notebookId?, sectionGroupId?, sectionId?, pageId?}. current marks OneNote's current window; sideNote a quick note, which can be on a page of a notebook the user never named. "
        + "An ID OneNote doesn't report is left out: no pageId in a locked section. A window on a page of a password-protected section the user hasn't granted you reads reports the section without its pageId, with the warning readNotGranted, whose userAction says how the user grants it. "
        + "Pass a window's handle as navigate_to's window (or navigate_to_url's) to show something in that window. With no OneNote window open, meta.windows is empty. "
        + "Success: {ok:true, meta:{correlationId, windows}, warnings:[]}. "
        + "Failure (isError): {ok:false, error:{code, hresult?, retryable, outcomeUnknown?, userAction, message}, meta:{correlationId}}.")]
    public Task<CallToolResult> GetWindows(CancellationToken cancellationToken = default) =>
        runner.RunAsync(
            Name,
            useCase,
            (context, ct) => useCase.ExecuteAsync(context, ct),
            cancellationToken);
}
