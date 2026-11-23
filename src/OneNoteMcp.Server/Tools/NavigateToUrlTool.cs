using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OneNoteMcp.Core.UseCases;

namespace OneNoteMcp.Server.Tools;

/// <summary>
/// <c>navigate_to_url</c> (story 4.7; AD-14). Thin (AD-5): the policy is in <see cref="NavigateToUrlUseCase"/>. User-visible
/// (AD-11); its annotations are <c>navigate_to</c>'s.
/// </summary>
[McpServerToolType]
public sealed class NavigateToUrlTool(ToolRunner runner, NavigateToUrlUseCase useCase)
{
    public const string Name = NavigateToUrlUseCase.OperationName;

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
        "User-visible: changes what OneNote shows, and may bring OneNote to the front. Call only when the user explicitly asks to follow a OneNote link. "
        + "Follows a onenote: link, as get_hyperlink_to_object returns it, to a page, section or notebook stored online (OneDrive or SharePoint) that is open in OneNote. It changes no content and returns none. "
        + "Without window, OneNote moves the window it chooses. With window, a handle from get_windows, only that window moves; a handle no open window has fails with InvalidArgument and nothing is shown. "
        + "Refused with InvalidArgument before anything is shown: a web link (https://…) or any link that isn't onenote:; a link into a notebook on this PC or a network share (call navigate_to with the object's ID instead); a link into a notebook that isn't open (it never opens one); a .one that isn't an open section; a section group's folder (call navigate_to with the group's ID); and a link with a query, a backslash, control characters or parts OneNote's own links don't carry. "
        + "OneNote can return ok without reaching the target (a deleted page), so afterwards the server reads OneNote's current window and compares it with the link (with window, that window; without, OneNote's current window): the warning landed means it matches, landedElsewhere that it doesn't (or a locked section shows no page), landingUnconfirmed that the check couldn't run; the call is ok in each case. Each says \"OneNote's current page is …\", by ID, and meta.windows holds the window read, as get_windows lists it (no pageId for a page of a password-protected section the user hasn't granted reads, with readNotGranted). "
        + "ObjectNotFound can mean the link names something gone, or that navigate_to_url was called again within about a second: follow its userAction. "
        + "It has a 120 s deadline. A timeout, a lost connection or a cancel while OneNote is handling the call returns outcomeUnknown: ask the user what OneNote shows before calling it again. "
        + "Success: {ok:true, meta:{correlationId, windows?}, warnings:[{code, message, userAction?}]}. "
        + "Failure (isError): {ok:false, error:{code, hresult?, retryable, outcomeUnknown?, userAction, message}, meta:{correlationId}}.")]
    public Task<CallToolResult> NavigateToUrl(
        [Description("The onenote: link to follow, exactly as get_hyperlink_to_object returned it (onenote:https://…).")] string url,
        [Description("Handle of the OneNote window to follow it in, exactly as get_windows returns it. Omit to let OneNote choose.")]
        int? window = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync(
            Name,
            useCase,
            (context, ct) => useCase.ExecuteAsync(context, url ?? string.Empty, window, ct),
            cancellationToken);
}
