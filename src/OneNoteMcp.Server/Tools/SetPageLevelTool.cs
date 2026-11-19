using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Server.Grants;

namespace OneNoteMcp.Server.Tools;

/// <summary>
/// Story 3.14's subpage level (E10). Thin (AD-5): the policy is in <see cref="SetPageLevelUseCase"/> and the write pipeline
/// (AD-7); the envelope comes from <see cref="ToolRunner"/>, so no tool throws.
/// </summary>
[McpServerToolType]
public sealed class SetPageLevelTool(ToolRunner runner, SetPageLevelUseCase useCase)
{
    public const string Name = "set_page_level";

    [McpServerTool(
        Name = Name,
        ReadOnly = false,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope))]
    [McpMeta(ToolMetaKeys.MaxResultSizeChars, ToolMetaKeys.MaxResultSizeCharsValue)]
    [Description(
        "Writes to OneNote: makes a page a subpage, or a subpage a page, by setting its level in its section: 1 is a top-level page, 2 a subpage, 3 a sub-subpage. Call it only when the user asks to indent, outdent or nest a page. To create a new page as a subpage, use create_page with parentPageId instead. "
        + "level is the absolute level wanted, from 1 to 3. A subpage belongs to the nearest page before it at a lower level, so indenting a page to 2 puts it under the page before it. The page's own subpages move with it by the same amount. "
        + "Refused with InvalidArgument: the section's first page at any level but 1, a level more than one deeper than the page before it, a move that would take one of the page's subpages outside 1 to 3, and an outdent that would leave the page after its subpages more than one level deeper than the page before it. To make such a change, change a neighbouring page's level first, or reorder the pages with reorder_hierarchy_children. "
        + "The same level changes nothing: ok with the warning unchanged. "
        + "Read the page's section first with get_hierarchy (scope children) and pass the page's lastModifiedTime as basis: {lastModifiedTime}; a page changed since fails with StaleWrite. The check is weak: straight after another hierarchy write a date can move a second late and give a spurious StaleWrite (re-read, then retry). Without basis the change runs with the warning noBasis. "
        + "The notebook needs the user's write grant (a dialog may ask the user to type the notebook's name; only the user can grant, never run the grant command yourself). In a password-protected section the user's read grant for that section is needed too, held when the call starts (ReadNotGranted without it; it is never asked for here). "
        + "Also refused before anything is written: a page in the recycle bin or an ID that isn't a live page (ObjectNotFound; a section or notebook ID is InvalidArgument), and a locked section (SectionLocked: the user unlocks it in OneNote). "
        + "Otherwise the section's hierarchy is first saved to the recovery journal (meta.journalEntryId; restore_from_journal can't restore it, the entry is for the user), and afterwards the section is re-read: readBackDiffers means OneNote kept other levels or another order, readBackFailed that the re-read failed. "
        + "If the call fails with outcomeUnknown: true, re-read the section before trying again. "
        + "Success: {ok:true, meta:{correlationId, objectId, parentId, journalEntryId?}, warnings:[]}. "
        + "Failure (isError): {ok:false, error:{code, hresult?, retryable, outcomeUnknown?, userAction, message}, meta:{correlationId}}.")]
    public Task<CallToolResult> SetPageLevel(
        [Description("OneNote ID of the page whose level to set.")] string pageId,
        [Description("The page's new level, from 1 (a top-level page) to 3; its subpages move by the same amount.")] int level,
        [Description("{lastModifiedTime} of the page, unchanged from its section's get_hierarchy read (the lastModifiedTime attribute of its one:Page element). Nothing else.")] PageBasisArgument? basis = null,
        CancellationToken cancellationToken = default,
        McpServer? server = null) =>
        runner.RunAsync(
            Name,
            useCase,
            (context, ct) => useCase.SetLevelAsync(context, pageId ?? string.Empty, level, basis?.ToCore(), ct),
            cancellationToken,
            ElicitationGrantPrompt.For(server));
}
