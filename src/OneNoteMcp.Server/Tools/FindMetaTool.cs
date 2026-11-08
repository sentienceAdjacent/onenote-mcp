using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OneNoteMcp.Core.UseCases;

namespace OneNoteMcp.Server.Tools;

/// <summary><c>FindMeta</c>. Thin (AD-5): the policy is in <see cref="FindMetaUseCase"/>.</summary>
[McpServerToolType]
public sealed class FindMetaTool(ToolRunner runner, FindMetaUseCase useCase)
{
    public const string Name = "find_meta";

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
        "Read-only. Finds the pages carrying a one:Meta element with the given name, and returns them as OneNote hierarchy XML in the OneNote 2013 schema. "
        + "Give exactly one of startNodeId or fromRoot: true; neither or both fails with InvalidQuery. "
        + "Unindexed pages are always searched. Recycle-bin pages and pages with no one:Meta of exactly that name (case-sensitive) are removed from the XML, and nothing else in it is changed; "
        + "each removed page is listed in meta.filtered as {pageId, reason}, reason recycleBin or noMatch. "
        + "Results are possibly incomplete (meta.possiblyIncomplete is always true): a notebook that is still syncing can lack pages. "
        + "Pages in password-protected sections the user hasn't granted a read of are never searched or listed (not even in meta.filtered); each such section in the searched scope has its ID in meta.withheld and a warning with code readNotGranted, whether or not it would have matched. "
        + ToolMetaKeys.LargeResultNote
        + "Success: {ok:true, xml, meta:{correlationId, filtered, possiblyIncomplete, withheld?}, warnings:[{code, message, userAction?}]}. "
        + "Failure (isError): {ok:false, error:{code, hresult?, retryable, outcomeUnknown?, userAction, message}, meta:{correlationId}}.")]
    public Task<CallToolResult> FindMeta(
        [Description("The one:Meta name to find, compared exactly.")] string name,
        [Description("OneNote ID of the notebook, section group or section to search under. Leave empty only with fromRoot: true.")] string startNodeId = "",
        [Description("True to search every open notebook. Only with an empty startNodeId. Defaults to false.")] bool fromRoot = false,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync(
            Name,
            useCase,
            (context, ct) => useCase.ExecuteAsync(context, name ?? string.Empty, startNodeId ?? string.Empty, fromRoot, ct),
            cancellationToken);
}
