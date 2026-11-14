using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Server.Grants;

namespace OneNoteMcp.Server.Tools;

/// <summary>
/// <c>DeletePageContent</c>. Thin (AD-5): the policy is in <see cref="DeletePageContentUseCase"/> and the write pipeline
/// (AD-7); the envelope comes from <see cref="ToolRunner"/>, so the tool never throws.
/// </summary>
[McpServerToolType]
public sealed class DeletePageContentTool(ToolRunner runner, DeletePageContentUseCase useCase)
{
    public const string Name = "delete_page_content";

    [McpServerTool(
        Name = Name,
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope))]
    [McpMeta(ToolMetaKeys.MaxResultSizeChars, ToolMetaKeys.MaxResultSizeCharsValue)]
    [Description(
        "Writes to OneNote: deletes one whole Outline (a block of content) from a page. Call it only when the user asks to remove a block of a page. "
        + "Read the page first with get_page_content and pass its meta.basis unchanged as basis: an Outline changed since that read fails with StaleWrite (re-read, then decide again). Without basis the delete runs with the warning noBasis. "
        + "objectId must be a one:Outline's objectID from that read; an OE, the title, an image or ink fails with InvalidArgument (to delete an OE, use update_page_content with removeObjectIds). "
        + "The page's notebook needs the user's write grant (a dialog may ask the user to type the notebook's name; only the user can grant, never run the grant command yourself). "
        + "In a password-protected section, the section's read grant is needed too: read the page with get_page_content first, or the call fails with ReadNotGranted. "
        + "Every delete first saves the page to the recovery journal: meta.journalEntryId is that entry, which restore_from_journal can bring back as a new page if the user asks. Afterwards the page is re-read: readBackDiffers means the Outline is still there, readBackFailed a re-read that failed. "
        + "meta.basis is the page's new basis for the next write. If the call fails with outcomeUnknown: true, re-read the page before trying again. "
        + "Success: {ok:true, meta:{correlationId, basis?, journalEntryId}, warnings:[]}. "
        + "Failure (isError): {ok:false, error:{code, hresult?, retryable, outcomeUnknown?, userAction, message}, meta:{correlationId}}.")]
    public Task<CallToolResult> DeletePageContent(
        [Description("OneNote ID of the page that holds the Outline.")] string pageId,
        [Description("objectID of the one:Outline to delete, from get_page_content.")] string objectId,
        [Description("meta.basis from the get_page_content read the delete is based on, unchanged.")] PageBasisArgument? basis = null,
        CancellationToken cancellationToken = default,
        McpServer? server = null) =>
        runner.RunAsync(
            Name,
            useCase,
            (context, ct) => useCase.ExecuteAsync(context, pageId ?? string.Empty, objectId ?? string.Empty, basis?.ToCore(), ct),
            cancellationToken,
            ElicitationGrantPrompt.For(server));
}
