using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OneNoteMcp.Core.UseCases;

namespace OneNoteMcp.Server.Tools;

/// <summary>
/// <c>GetHierarchy</c> at notebooks scope. Thin (AD-5): the policy is in <see cref="GetHierarchyUseCase"/> and the
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
    [Description("Read-only. Returns the OneNote notebooks list as OneNote 2013-schema hierarchy XML, unchanged.")]
    public Task<CallToolResult> GetHierarchy(
        [Description("OneNote object ID to start from. Omit or leave empty for all notebooks.")] string startNodeId = "",
        CancellationToken cancellationToken = default) =>
        runner.RunAsync(
            Name,
            useCase,
            (context, ct) => useCase.ExecuteAsync(context, startNodeId ?? string.Empty, ct),
            cancellationToken);
}
