using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OneNoteMcp.Core.UseCases;

namespace OneNoteMcp.Server.Tools;

/// <summary><c>GetSpecialLocation</c>. Thin (AD-5): the policy is in <see cref="GetSpecialLocationUseCase"/>.</summary>
[McpServerToolType]
public sealed class GetSpecialLocationTool(ToolRunner runner, GetSpecialLocationUseCase useCase)
{
    public const string Name = "get_special_location";

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
        "Read-only. Returns, in meta.path, the path OneNote keeps for a special location: its backup folder, its unfiled-notes (Quick Notes) section, or its default folder for new notebooks. "
        + "Success: {ok:true, meta:{correlationId, path}, warnings:[]}. "
        + "Failure (isError): {ok:false, error:{code, hresult?, retryable, outcomeUnknown?, userAction, message}, meta:{correlationId}}.")]
    public Task<CallToolResult> GetSpecialLocation(
        [Description("Which location: backUpFolder, unfiledNotesSection or defaultNotebookFolder.")] SpecialLocationArgument location,
        CancellationToken cancellationToken = default,
        RequestContext<CallToolRequestParams>? request = null) =>
        runner.RunAsync(
            Name,
            useCase,
            (context, ct) => useCase.ExecuteAsync(context, location.ToCore(), ct),
            cancellationToken,
            request: request);
}
