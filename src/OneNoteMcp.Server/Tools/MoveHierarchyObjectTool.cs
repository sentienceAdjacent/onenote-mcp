using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Server.Grants;

namespace OneNoteMcp.Server.Tools;

/// <summary>
/// Stories 3.4 and 3.12: the move of a page, section or section group (AD-5: the move is its own tool, so the model never writes XML spanning two notebooks). Thin
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
        "Writes to OneNote: moves a page into another section, or a section or section group into another notebook or section group, in the same notebook or another open one. Call it only when the user asks to move something. To change an item's place among its siblings, use reorder_hierarchy_children. "
        + "objectId is the page, section or section group; parentId is the new parent: a section for a page, a notebook or section group for a section or section group. position is where it goes, from 1: among the target's pages for a page (before the page now at that position, which must be a top-level page, not a subpage), among the new parent's sections for a section, among its section groups for a group; leave it out to put it last. "
        + "OneNote gives a moved page a new ID, and the old one stops working; a section or section group keeps its ID. meta.objectId is the item's current ID: a page's new ID after the move; the old ID, with readBackDiffers, when the re-read finds the page still in the old section; absent with readBackFailed for a page. A page moves without its subpages: they stay in the old section as top-level pages, with the warning subpagesLeftBehind giving their count. "
        + "Read the item first with get_hierarchy and pass its lastModifiedTime (a page's hierarchy date, from its section's children) as basis: {lastModifiedTime}; an item changed since fails with StaleWrite. The check is weak: straight after another hierarchy write a date can move a second late and give a spurious StaleWrite (re-read, then retry). Without basis the move runs with the warning noBasis. "
        + "Both notebooks need the user's write grant (a dialog may ask the user to type each notebook's name; only the user can grant, never run the grant command yourself). A page moving into a password-protected section needs only that, unless position is given: placing a page at a position there also needs the user's read grant for that section, held when the call starts (ReadNotGranted without it; leave position out to put the page last). "
        + "Refused before anything is moved: a page in a password-protected section, a password-protected section, or a section group holding one, locked or not (NotSupported: the user moves it in OneNote); anything in the recycle bin, or a target in it (ObjectNotFound); a target of the wrong kind, the item's own parent, a section group moved into itself, a target already holding a section or group of the same name, a position past the end or at a subpage, or a section that would land where a read grant is stored (InvalidArgument); a section or group leaving a cloud (OneDrive or SharePoint) notebook for another notebook's section group (NotSupported: move it to that notebook's root first); and a locked target section (SectionLocked: the user unlocks it in OneNote). "
        + "Both parents' hierarchy is first saved to the recovery journal (meta.journalEntryId is the old parent's entry; meta.journalEntryIds lists both, old then new; restore_from_journal can't restore them, they are for the user). A section or group move that touches a cloud (OneDrive or SharePoint) notebook waits 10 s before re-reading, with the warning settled: ask the user to check it synced. Afterwards both parents are re-read: readBackDiffers means the move wasn't confirmed, readBackFailed that the re-read failed (find the item with get_hierarchy); readGrantAtLocation that a moved section landed where a read grant is stored, which now covers it: tell the user. "
        + "If the call fails with outcomeUnknown: true (a move can leave a partial copy, and the message says what a re-read found), re-read both parents before trying again. A section group OneNote can't move there fails with NotSupported: move its sections one by one, or ask the user. "
        + "Success: {ok:true, meta:{correlationId, objectId?, parentId, journalEntryId, journalEntryIds}, warnings:[]}. "
        + "Failure (isError): {ok:false, error:{code, hresult?, retryable, outcomeUnknown?, userAction, message}, meta:{correlationId}}.")]
    public Task<CallToolResult> MoveHierarchyObject(
        [Description("OneNote ID of the page, section or section group to move.")] string objectId,
        [Description("OneNote ID of the new parent: a section for a page; a notebook or section group for a section or section group.")] string parentId,
        [Description("Where the item goes, from 1: among the target section's pages for a page (before the page now there, a top-level page), among the new parent's sections or section groups for a section or group. Leave it out to put it last; in a password-protected section a page's position needs a read grant.")] int? position = null,
        [Description("{lastModifiedTime} of the item, unchanged from a get_hierarchy read (the lastModifiedTime attribute of its one:Page, one:Section or one:SectionGroup element). Nothing else.")] PageBasisArgument? basis = null,
        CancellationToken cancellationToken = default,
        McpServer? server = null) =>
        runner.RunAsync(
            Name,
            useCase,
            (context, ct) => useCase.MoveAsync(context, objectId ?? string.Empty, parentId ?? string.Empty, position, basis?.ToCore(), ct),
            cancellationToken,
            ElicitationGrantPrompt.For(server));
}
