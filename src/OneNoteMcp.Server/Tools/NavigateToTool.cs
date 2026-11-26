using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OneNoteMcp.Core.UseCases;

namespace OneNoteMcp.Server.Tools;

/// <summary><c>NavigateTo</c>. Thin (AD-5): the policy is in <see cref="NavigateToUseCase"/>. User-visible (AD-11).</summary>
[McpServerToolType]
public sealed class NavigateToTool(ToolRunner runner, NavigateToUseCase useCase)
{
    public const string Name = "navigate_to";

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
        "User-visible: changes what OneNote shows, and may bring OneNote forward. Call only when the user explicitly asks. "
        + "Shows a notebook, section group, section or page in OneNote and, with pageContentObjectId, selects that object on the page. It changes no content and returns none. "
        + "Without window it uses OneNote's first open window. With window, a handle from get_windows, only that window moves; a handle no open window has fails with InvalidArgument and nothing is shown. "
        + "Showing a page makes OneNote download the page's images that aren't on this PC, so after the user agrees, a get_binary_page_content that gave binaryUnavailable can be retried. "
        + "It has a 120 s deadline. A timeout, a lost connection or a cancel while OneNote is handling the call returns outcomeUnknown: OneNote may or may not show the object, so ask the user what OneNote shows before calling it again. "
        + "Success: {ok:true, meta:{correlationId}, warnings:[]}. "
        + "Failure (isError): {ok:false, error:{code, hresult?, retryable, outcomeUnknown?, userAction, message}, meta:{correlationId}}.")]
    public Task<CallToolResult> NavigateTo(
        [Description("OneNote ID of the notebook, section group, section or page to show.")] string hierarchyId,
        [Description("objectID of an element on that page to select, such as an outline, paragraph or image. Omit or leave empty to show the hierarchy object itself.")]
        string pageContentObjectId = "",
        [Description("Handle of the OneNote window to show it in, exactly as get_windows returns it. Omit to use OneNote's first window.")]
        int? window = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync(
            Name,
            useCase,
            (context, ct) => useCase.ExecuteAsync(context, hierarchyId ?? string.Empty, pageContentObjectId ?? string.Empty, window, ct),
            cancellationToken);
}
