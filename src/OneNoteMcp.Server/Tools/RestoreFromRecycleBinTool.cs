using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Server.Grants;

namespace OneNoteMcp.Server.Tools;

/// <summary>
/// <c>restore_from_recycle_bin</c> (story 3.5; AD-8). Thin (AD-5): the policy is in
/// <see cref="RestoreFromRecycleBinUseCase"/> and the write pipeline (AD-7); the envelope comes from
/// <see cref="ToolRunner"/>, so the tool never throws.
/// </summary>
[McpServerToolType]
public sealed class RestoreFromRecycleBinTool(ToolRunner runner, RestoreFromRecycleBinUseCase useCase)
{
    public const string Name = "restore_from_recycle_bin";

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
        "Writes to OneNote: brings a soft-deleted page or section back out of its notebook's recycle bin. Call it only when the user asks to restore something deleted. "
        + "objectId is the item's ID in the recycle bin: a page in the notebook's Deleted Pages, or a section directly in the recycle bin (get_hierarchy with scope pages shows both). A section group isn't restored whole (OneNote bins its sections one by one: restore each), and a page inside a recycled section comes back with its section. "
        + "Where it goes: parentId when given (a section for a page; a notebook or section group for a section); otherwise the place it was deleted from, found in its delete_hierarchy_object journal entry by its recycle-bin ID or section ID, else by name (warning restoredByName). Without a journal entry (deleted in OneNote, deleted with its section group, or older than the journal's retention period) or with its original parent gone, it is refused: ask the user where it should go and pass parentId. "
        + "If several journal entries match (same-named items), nothing is restored and the error lists each candidate's entry ID, deletion time (UTC) and original parent, marking one an earlier restore already used \"already restored\": ask the user which one, then retry with entryId. "
        + "position is where it goes, from 1: among the target section's pages for a page (before the page now there, a top-level page), among the target's sections for a section; leave it out to put it last. "
        + "A section sent to another notebook is first restored in its own notebook (under its original parent, which must be known), then moved there as move_hierarchy_object moves it, with every move rule and the grant dialog; meta.journalEntryIds lists the restore's journal entry, then the move's. If that move is refused or fails, the section stays restored in its own notebook and the result carries the warning restoredNotMoved with the move's error: tell the user, and move it with move_hierarchy_object if they still want it there. If the move's outcome is unknown (a timeout or a cancel), restoredNotMoved says it may or may not have moved and meta.parentId is absent: re-read both notebooks before doing anything else. "
        + "A restored page gets a NEW ID (meta.objectId; the recycle-bin ID stops working) and comes back as a top-level page; a section keeps its ID. "
        + "A password-protected section goes back only to the place it was deleted from, still protected and locked, with no unlock and no read grant needed; a parentId elsewhere is refused (InvalidArgument), and if that place is gone the user restores it in OneNote (ObjectNotFound). "
        + "The notebooks need the user's write grant (a dialog may ask the user to type each notebook's name; only the user can grant, never run the grant command yourself). Placing a page at a position in a password-protected section also needs the user's read grant for it, held when the call starts (ReadNotGranted; leave position out to put it last). "
        + "Refused before anything is restored: an item that isn't in the recycle bin, the recycle bin or Deleted Pages itself, a target of the wrong kind or in the recycle bin, a target already holding a section of that name, a position past the end or at a subpage, or a section that would land where another section's read grant is stored (InvalidArgument); a locked target section (SectionLocked: the user unlocks it in OneNote). "
        + "The target's hierarchy is first saved to the recovery journal (meta.journalEntryId). A section in a cloud (OneDrive or SharePoint) notebook waits 10 s before re-reading, with the warning settled: ask the user to check it synced. Afterwards the target is re-read: readBackDiffers means it wasn't confirmed where it was sent, readBackFailed that the re-read failed (find it with get_hierarchy). "
        + "If the call fails with outcomeUnknown: true, re-read the target and the recycle bin before trying again. "
        + "Success: {ok:true, meta:{correlationId, objectId?, parentId, journalEntryId, journalEntryIds?}, warnings:[]}. "
        + "Failure (isError): {ok:false, error:{code, hresult?, retryable, outcomeUnknown?, userAction, message}, meta:{correlationId}}.")]
    public Task<CallToolResult> RestoreFromRecycleBin(
        [Description("OneNote ID of the page in Deleted Pages, or of the section in the recycle bin.")] string objectId,
        [Description("OneNote ID of where it goes: a section for a page; a notebook or section group for a section (in another notebook, it is restored in its own notebook and then moved). Leave it out to restore it where it was deleted from; a password-protected section always goes there.")] string? parentId = null,
        [Description("The journal entry ID (a GUID) to restore from, from the candidates a refusal listed. Leave it out otherwise.")] string? entryId = null,
        [Description("Where the item goes, from 1: among the target section's pages for a page (before the page now there, a top-level page), among the target's sections for a section. Leave it out to put it last; in a password-protected section a page's position needs a read grant.")] int? position = null,
        CancellationToken cancellationToken = default,
        McpServer? server = null) =>
        runner.RunAsync(
            Name,
            useCase,
            (context, ct) => useCase.RestoreAsync(context, objectId ?? string.Empty, parentId, entryId, position, ct),
            cancellationToken,
            ElicitationGrantPrompt.For(server));
}
