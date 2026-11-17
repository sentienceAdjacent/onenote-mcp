using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Server.Grants;

namespace OneNoteMcp.Server.Tools;

/// <summary>
/// Story 3.4's page move (AD-5: the move is its own tool, so the model never writes XML spanning two notebooks). Thin
/// (AD-5): the policy is in <see cref="MoveHierarchyObjectUseCase"/> and the write pipeline (AD-7); the envelope comes from
/// <see cref="ToolRunner"/>, so no tool throws.
/// </summary>
[McpServerToolType]
public sealed class MoveHierarchyObjectTool(ToolRunner runner, MoveHierarchyObjectUseCase useCase)
{
    public const string Name = "move_hierarchy_object";

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
        "Writes to OneNote: moves a page into another section, in the same notebook or another open one. Call it only when the user asks to move a page. Pages only in this release: a section or section group fails with NotSupported (ask the user to move it in OneNote). To change a page's place within its own section, use reorder_hierarchy_children. "
        + "objectId is the page; parentId is the target section. position is where it goes among the target's pages, from 1: the page goes before the page now at that position, which must be a top-level page, not a subpage; leave it out to put the page last. "
        + "OneNote gives the moved page a new ID, and the old one stops working. meta.objectId is the page's current ID: its new ID after the move; the old ID, with readBackDiffers, when the re-read finds the page still in the old section; absent with readBackFailed. The page moves without its subpages: they stay in the old section as top-level pages, with the warning subpagesLeftBehind giving their count. "
        + "Read the page's section first with get_hierarchy (scope children) and pass the page's lastModifiedTime (its hierarchy date) as basis: {lastModifiedTime}; a page changed since fails with StaleWrite. The check is weak: straight after another hierarchy write a date can move a second late and give a spurious StaleWrite (re-read, then retry). Without basis the move runs with the warning noBasis. "
        + "Both notebooks need the user's write grant (a dialog may ask the user to type each notebook's name; only the user can grant, never run the grant command yourself). A target in a password-protected section needs only that, unless position is given: placing a page at a position there also needs the user's read grant for that section, held when the call starts (ReadNotGranted without it; leave position out to put the page last). "
        + "Refused before anything is moved: a page in a password-protected section, locked or not (NotSupported: the user moves it in OneNote), a page or target in the recycle bin (ObjectNotFound), a target that isn't a section or is the page's own section, or a position past the end or at a subpage (InvalidArgument), and a locked target (SectionLocked: the user unlocks it in OneNote). "
        + "Both sections' hierarchy is first saved to the recovery journal (meta.journalEntryId is the old section's entry; meta.journalEntryIds lists both, old then new; restore_from_journal can't restore them, they are for the user). Afterwards both sections are re-read: readBackDiffers means the move wasn't confirmed, readBackFailed that the re-read failed (find the page with get_hierarchy). "
        + "If the call fails with outcomeUnknown: true (a move can leave a partial copy, and the message says what a re-read found), re-read both sections before trying again. "
        + "Success: {ok:true, meta:{correlationId, objectId?, parentId, journalEntryId, journalEntryIds}, warnings:[]}. "
        + "Failure (isError): {ok:false, error:{code, hresult?, retryable, outcomeUnknown?, userAction, message}, meta:{correlationId}}.")]
    public Task<CallToolResult> MoveHierarchyObject(
        [Description("OneNote ID of the page to move.")] string objectId,
        [Description("OneNote ID of the section to move the page into.")] string parentId,
        [Description("Where the page goes among the target section's pages, from 1, before the page now there (a top-level page). Leave it out to put the page last; in a password-protected section a position needs a read grant.")] int? position = null,
        [Description("{lastModifiedTime} of the page, unchanged from its section's get_hierarchy read (the lastModifiedTime attribute of its one:Page element). Nothing else.")] PageBasisArgument? basis = null,
        CancellationToken cancellationToken = default,
        McpServer? server = null) =>
        runner.RunAsync(
            Name,
            useCase,
            (context, ct) => useCase.MoveAsync(context, objectId ?? string.Empty, parentId ?? string.Empty, position, basis?.ToCore(), ct),
            cancellationToken,
            ElicitationGrantPrompt.For(server));
}
