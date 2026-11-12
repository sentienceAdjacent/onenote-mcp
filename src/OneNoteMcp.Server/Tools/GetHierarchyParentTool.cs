using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Server.Grants;

namespace OneNoteMcp.Server.Tools;

/// <summary><c>GetHierarchyParent</c>. Thin (AD-5): the policy is in <see cref="GetHierarchyParentUseCase"/>.</summary>
[McpServerToolType]
public sealed class GetHierarchyParentTool(ToolRunner runner, GetHierarchyParentUseCase useCase)
{
    public const string Name = "get_hierarchy_parent";

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
        "Read-only. Returns the ID of a OneNote object's parent in meta.parentId: a page's section, a section's section group or notebook. "
        + "For a notebook, meta.parentId is the empty string: the top of the hierarchy. One step only; call it again to walk further up. "
        + "A page in a password-protected section needs the user's read grant: the user may be shown a dialog to grant it, and without a grant the call fails with ReadNotGranted, whose userAction says how the user grants it (you can't grant it yourself). "
        + "Success: {ok:true, meta:{correlationId, parentId}, warnings:[]}. "
        + "Failure (isError): {ok:false, error:{code, hresult?, retryable, outcomeUnknown?, userAction, message}, meta:{correlationId}}.")]
    public Task<CallToolResult> GetHierarchyParent(
        [Description("OneNote ID of the page, section, section group or notebook.")] string objectId,
        CancellationToken cancellationToken = default,
        McpServer? server = null) =>
        runner.RunAsync(
            Name,
            useCase,
            (context, ct) => useCase.ExecuteAsync(context, objectId ?? string.Empty, ct),
            cancellationToken,
            ElicitationGrantPrompt.For(server));
}
