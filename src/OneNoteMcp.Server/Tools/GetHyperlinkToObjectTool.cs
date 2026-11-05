using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OneNoteMcp.Core.UseCases;

namespace OneNoteMcp.Server.Tools;

/// <summary><c>GetHyperlinkToObject</c>. Thin (AD-5): the policy is in <see cref="GetHyperlinkToObjectUseCase"/>.</summary>
[McpServerToolType]
public sealed class GetHyperlinkToObjectTool(ToolRunner runner, GetHyperlinkToObjectUseCase useCase)
{
    public const string Name = "get_hyperlink_to_object";

    [McpServerTool(
        Name = Name,
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope))]
    [Description(
        "Read-only. Returns, in meta.link, a onenote: link to a notebook, section group, section or page, or to one object on a page. It does not open the link. "
        + "Success: {ok:true, meta:{correlationId, link}, warnings:[]}. "
        + "Failure (isError): {ok:false, error:{code, hresult?, retryable, outcomeUnknown?, userAction, message}, meta:{correlationId}}.")]
    public Task<CallToolResult> GetHyperlinkToObject(
        [Description("OneNote ID of the notebook, section group, section or page.")] string hierarchyId,
        [Description("objectID of an element on that page, such as an outline or paragraph. Omit or leave empty to link to the hierarchy object itself.")]
        string pageContentObjectId = "",
        CancellationToken cancellationToken = default) =>
        runner.RunAsync(
            Name,
            useCase,
            (context, ct) => useCase.ExecuteAsync(context, hierarchyId ?? string.Empty, pageContentObjectId ?? string.Empty, ct),
            cancellationToken);
}
