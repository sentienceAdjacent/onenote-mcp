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
        "User-visible: takes keyboard focus and changes what OneNote shows. Call only when the user explicitly asks. "
        + "Shows a notebook, section group, section or page in OneNote's first open window and, with pageContentObjectId, selects that object on the page. It changes no content and returns none. "
        + "It only works with OneNote open in a window: when OneNote runs without one, it still returns ok but shows and downloads nothing. "
        + "Showing a page makes OneNote download the page's images that aren't on this PC, so after the user agrees, a get_binary_page_content that gave binaryUnavailable can be retried. "
        + "Success: {ok:true, meta:{correlationId}, warnings:[]}. "
        + "Failure (isError): {ok:false, error:{code, hresult?, retryable, outcomeUnknown?, userAction, message}, meta:{correlationId}}.")]
    public Task<CallToolResult> NavigateTo(
        [Description("OneNote ID of the notebook, section group, section or page to show.")] string hierarchyId,
        [Description("objectID of an element on that page to select, such as an outline, paragraph or image. Omit or leave empty to show the hierarchy object itself.")]
        string pageContentObjectId = "",
        CancellationToken cancellationToken = default,
        RequestContext<CallToolRequestParams>? request = null) =>
        runner.RunAsync(
            Name,
            useCase,
            (context, ct) => useCase.ExecuteAsync(context, hierarchyId ?? string.Empty, pageContentObjectId ?? string.Empty, ct),
            cancellationToken,
            request: request);
}
