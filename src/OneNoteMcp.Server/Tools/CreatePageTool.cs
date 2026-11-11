using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OneNoteMcp.Core.UseCases;

namespace OneNoteMcp.Server.Tools;

/// <summary>
/// <c>CreateNewPage</c>, the first write tool. Thin (AD-5): the policy is in <see cref="CreatePageUseCase"/> and the
/// write pipeline (AD-7); the envelope comes from <see cref="ToolRunner"/>, so the tool never throws.
/// </summary>
[McpServerToolType]
public sealed class CreatePageTool(ToolRunner runner, CreatePageUseCase useCase)
{
    public const string Name = "create_page";

    [McpServerTool(
        Name = Name,
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope))]
    [McpMeta(ToolMetaKeys.MaxResultSizeChars, ToolMetaKeys.MaxResultSizeCharsValue)]
    [Description(
        "Writes to OneNote: adds an empty page at the end of a section and returns its ID in meta.pageId. Call it only when the user asks for a new page. "
        + "Each call adds another page; it is never undone automatically. "
        + "The section's notebook needs the user's write grant: without one the call fails with WriteNotGranted, and only the user can grant it (never run the grant command yourself). "
        + "An ID that isn't a section, or a section in the recycle bin, fails with ObjectNotFound; a locked section fails with SectionLocked. "
        + "If the call fails with outcomeUnknown: true, the page may or may not exist: re-read the section's pages before trying again. "
        + "Success: {ok:true, meta:{correlationId, pageId}, warnings:[]}. "
        + "Failure (isError): {ok:false, error:{code, hresult?, retryable, outcomeUnknown?, userAction, message}, meta:{correlationId}}.")]
    public Task<CallToolResult> CreatePage(
        [Description("OneNote ID of the section to add the page to.")] string sectionId,
        [Description("The new page's layout: blankPageWithTitle (an empty page with a title line; the default), blankPageNoTitle (no title line), or default (the section's own page template, which may add content).")]
        PageStyleArgument style = PageStyleArgument.BlankPageWithTitle,
        CancellationToken cancellationToken = default,
        RequestContext<CallToolRequestParams>? request = null) =>
        runner.RunAsync(
            Name,
            useCase,
            (context, ct) => useCase.ExecuteAsync(context, sectionId ?? string.Empty, style.ToCore(), ct),
            cancellationToken,
            request: request);
}
