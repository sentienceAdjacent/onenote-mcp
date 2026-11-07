using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OneNoteMcp.Core.UseCases;

namespace OneNoteMcp.Server.Tools;

/// <summary><c>FindPages</c>. Thin (AD-5): the policy is in <see cref="FindPagesUseCase"/>.</summary>
[McpServerToolType]
public sealed class FindPagesTool(ToolRunner runner, FindPagesUseCase useCase)
{
    public const string Name = "find_pages";

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
        "Read-only. Searches page text and returns the matching pages as OneNote hierarchy XML in the OneNote 2013 schema. "
        + "Give exactly one of startNodeId or fromRoot: true; neither or both fails with InvalidQuery. "
        + "A page matches when every whitespace-separated term of query appears, ignoring case, in its title or its text. "
        + "Unindexed pages are always searched. Recycle-bin pages and pages that don't match are removed from the XML, and nothing else in it is changed; "
        + "each removed page is listed in meta.filtered as {pageId, reason}, reason recycleBin, noMatch, or notFound (deleted during the search). "
        + "Results are possibly incomplete (meta.possiblyIncomplete is always true): a notebook that is still syncing can lack pages. "
        + "The server remembers page text between searches, keyed by each page's lastModifiedTime, so repeat searches are faster; a page another add-in changed through COM while it wasn't open in OneNote can still be matched against its old text until it next changes (setting search.cacheMaxMB: 0 turns this off). "
        + ToolMetaKeys.LargeResultNote
        + "Success: {ok:true, xml, meta:{correlationId, filtered, possiblyIncomplete}, warnings:[{code, message, userAction?}]}. "
        + "Failure (isError): {ok:false, error:{code, hresult?, retryable, outcomeUnknown?, userAction, message}, meta:{correlationId}}.")]
    public Task<CallToolResult> FindPages(
        [Description("The words to search for, separated by spaces. Every word must appear on a page for it to match.")] string query,
        [Description("OneNote ID of the notebook, section group or section to search under. Leave empty only with fromRoot: true.")] string startNodeId = "",
        [Description("True to search every open notebook. Only with an empty startNodeId. Defaults to false.")] bool fromRoot = false,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync(
            Name,
            useCase,
            (context, ct) => useCase.ExecuteAsync(context, query ?? string.Empty, startNodeId ?? string.Empty, fromRoot, ct),
            cancellationToken);
}
