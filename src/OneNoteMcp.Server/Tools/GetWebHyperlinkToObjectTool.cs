using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Server.Grants;

namespace OneNoteMcp.Server.Tools;

/// <summary><c>GetWebHyperlinkToObject</c>. Thin (AD-5): the policy is in <see cref="GetWebHyperlinkToObjectUseCase"/>.</summary>
[McpServerToolType]
public sealed class GetWebHyperlinkToObjectTool(ToolRunner runner, GetWebHyperlinkToObjectUseCase useCase)
{
    public const string Name = "get_web_hyperlink_to_object";

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
        "Read-only. Returns, in meta.link, a web (https) link to a notebook, section group, section or page, or to one object on a page, for notebooks stored on OneDrive or SharePoint. It does not open the link. "
        + "For a notebook stored only on this computer, meta.link is the empty string and a warning with code webLinkUnavailable is added. "
        + "A page (or an object on a page) in a password-protected section needs the user's read grant: the user may be shown a dialog to grant it, and without a grant the call fails with ReadNotGranted, whose userAction says how the user grants it (you can't grant it yourself). "
        + "Success: {ok:true, meta:{correlationId, link}, warnings:[{code, message, userAction?}]}. "
        + "Failure (isError): {ok:false, error:{code, hresult?, retryable, outcomeUnknown?, userAction, message}, meta:{correlationId}}.")]
    public Task<CallToolResult> GetWebHyperlinkToObject(
        [Description("OneNote ID of the notebook, section group, section or page.")] string hierarchyId,
        [Description("objectID of an element on that page, such as an outline or paragraph. Omit or leave empty to link to the hierarchy object itself.")]
        string pageContentObjectId = "",
        CancellationToken cancellationToken = default,
        McpServer? server = null,
        RequestContext<CallToolRequestParams>? request = null) =>
        runner.RunAsync(
            Name,
            useCase,
            (context, ct) => useCase.ExecuteAsync(context, hierarchyId ?? string.Empty, pageContentObjectId ?? string.Empty, ct),
            cancellationToken,
            ElicitationGrantPrompt.For(server),
            request: request);
}
