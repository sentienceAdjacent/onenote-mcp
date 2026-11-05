using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OneNoteMcp.Core.UseCases;

namespace OneNoteMcp.Server.Tools;

/// <summary>
/// <c>GetHierarchy</c> at any scope. Thin (AD-5): the policy is in <see cref="GetHierarchyUseCase"/> and the
/// envelope comes from <see cref="ToolRunner"/>, so the tool never throws.
/// </summary>
[McpServerToolType]
public sealed class GetHierarchyTool(ToolRunner runner, GetHierarchyUseCase useCase)
{
    public const string Name = "get_hierarchy";

    [McpServerTool(
        Name = Name,
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope))]
    [Description(
        "Read-only. Returns OneNote hierarchy XML (notebooks, section groups, sections, pages) in the OneNote 2013 schema, unchanged. "
        + "Each password-locked section in it adds a warning with code sectionLocked naming the section's ID. "
        + "Success: {ok:true, xml, meta:{correlationId}, warnings:[{code, message, userAction?}]}. "
        + "Failure (isError): {ok:false, error:{code, hresult?, retryable, outcomeUnknown?, userAction, message}, meta:{correlationId}}.")]
    public Task<CallToolResult> GetHierarchy(
        [Description("OneNote object ID to start from. Omit or leave empty for all notebooks.")] string startNodeId = "",
        [Description("How deep to read: self (the start node only), children (its direct children), notebooks, sections or pages (down to that level). Defaults to notebooks.")]
        HierarchyScopeArgument scope = HierarchyScopeArgument.Notebooks,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync(
            Name,
            useCase,
            (context, ct) => useCase.ExecuteAsync(context, startNodeId ?? string.Empty, scope.ToCore(), ct),
            cancellationToken);
}
