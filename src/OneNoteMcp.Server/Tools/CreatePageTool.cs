using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Server.Grants;

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
        + "To make the new page a subpage, pass parentPageId, a page in that section at level 1 or 2: the new page goes right after the parent's existing subpages, one level deeper than the parent. A parent that isn't a live page of the section, or is at level 3, fails with InvalidArgument and no page is created. With parentPageId the section's hierarchy is first saved to the recovery journal (meta.journalEntryId, for the user), and in a password-protected section the user's read grant for that section is needed too, held when the call starts (ReadNotGranted without it; leave parentPageId out to add a top-level page). If the page is created but can't be made a subpage, the call still succeeds, with the warning subpageNotPlaced: the page is last in the section at level 1 (move it with reorder_hierarchy_children, then set_page_level). readBackDiffers means OneNote kept it elsewhere; readBackFailed that the re-read failed. To change an existing page's level, use set_page_level. "
        + "The section's notebook needs the user's write grant. Without one, the user may be shown a dialog asking them to type the notebook's name to grant it; if they don't, or the client can't show it, the call fails with WriteNotGranted, and only the user can grant it (never run the grant command yourself). "
        + "An ID that isn't a section, or a section in the recycle bin, fails with ObjectNotFound; a locked section fails with SectionLocked. "
        + "If the call fails with outcomeUnknown: true, the page may or may not exist: re-read the section's pages before trying again. "
        + "Success: {ok:true, meta:{correlationId, pageId, journalEntryId?}, warnings:[]}. "
        + "Failure (isError): {ok:false, error:{code, hresult?, retryable, outcomeUnknown?, userAction, message}, meta:{correlationId}}.")]
    public Task<CallToolResult> CreatePage(
        [Description("OneNote ID of the section to add the page to.")] string sectionId,
        [Description("The new page's layout: blankPageWithTitle (an empty page with a title line; the default), blankPageNoTitle (no title line), or default (the section's own page template, which may add content).")]
        PageStyleArgument style = PageStyleArgument.BlankPageWithTitle,
        CancellationToken cancellationToken = default,
        McpServer? server = null,
        [Description("OneNote ID of a page in the section (level 1 or 2) to add the new page under as a subpage. Leave it out for a top-level page.")]
        string? parentPageId = null) =>
        runner.RunAsync(
            Name,
            useCase,
            (context, ct) => useCase.ExecuteAsync(context, sectionId ?? string.Empty, style.ToCore(), parentPageId, ct),
            cancellationToken,
            ElicitationGrantPrompt.For(server));
}
