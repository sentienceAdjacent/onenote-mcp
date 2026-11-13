using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Server.Grants;

namespace OneNoteMcp.Server.Tools;

/// <summary>
/// <c>DeleteHierarchy</c> with <c>permanent=true</c> (story 5.6; 5.5 plan Q1 b: soft and permanent deletes are separate
/// tools). Thin (AD-5): the policy is in <see cref="PermanentDeleteUseCase"/> and the write pipeline (AD-7); the envelope
/// comes from <see cref="ToolRunner"/>, so the tool never throws.
/// </summary>
[McpServerToolType]
public sealed class PermanentlyDeleteHierarchyObjectTool(ToolRunner runner, PermanentDeleteUseCase useCase)
{
    public const string Name = "permanently_delete_hierarchy_object";

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
        "Writes to OneNote: deletes a page, a section or an empty section group permanently, skipping OneNote's recycle bin. Call it only when the user explicitly asks to delete something for good. "
        + "Pass basis from your read: for a page {lastModifiedTime, pageHash} from get_page_content's meta.basis; for a section or section group {lastModifiedTime} from its element in get_hierarchy; never elementHashes (InvalidArgument). Anything changed since that read fails with StaleWrite (re-read, then ask again). Without basis the delete runs with the warning noBasis. "
        + "It needs the notebook's write grant and the user's permission to delete permanently: unless the user has allowed permanent deletes in that notebook, a dialog asks them to type the object's name for this one delete. If they don't, or the client can't show it, the call fails with PermanentDeleteNotGranted; only the user can grant (never run the grant command yourself). "
        + "Before deleting, the server saves a copy: a page to the recovery journal (meta.journalEntryId; restore_from_journal brings it back as a new page); a section as a OneNote backup file (meta.backupFile, meta.journalEntryId) plus one journal entry per page (meta.pageEntryIds, each restorable with restore_from_journal); an empty section group as a journal record. If the backup fails (BackupFailed), nothing is deleted. "
        + "Refused with NotSupported: a notebook, a password-protected section, a section group that holds anything, a page already in the recycle bin, and Deleted Pages (the user empties the recycle bin in OneNote). A page with subpages fails with InvalidArgument. In a password-protected section without its read grant, a page's basis may carry only lastModifiedTime (ReadNotGranted otherwise). "
        + "If the call fails with outcomeUnknown: true, re-read the hierarchy before trying again. "
        + "Success: {ok:true, meta:{correlationId, objectId, journalEntryId, backupFile?, pageEntryIds?}, warnings:[]}. "
        + "Failure (isError): {ok:false, error:{code, hresult?, retryable, outcomeUnknown?, userAction, message}, meta:{correlationId}}.")]
    public Task<CallToolResult> PermanentlyDeleteHierarchyObject(
        [Description("OneNote ID of the page, section or section group to delete permanently.")] string objectId,
        [Description("The read the delete is based on: {lastModifiedTime, pageHash} from get_page_content's meta.basis for a page, {lastModifiedTime} from get_hierarchy for a section or section group.")] PageBasisArgument? basis = null,
        CancellationToken cancellationToken = default,
        McpServer? server = null) =>
        runner.RunAsync(
            Name,
            useCase,
            (context, ct) => useCase.ExecuteAsync(context, objectId ?? string.Empty, basis?.ToCore(), ct),
            cancellationToken,
            ElicitationGrantPrompt.For(server));
}
