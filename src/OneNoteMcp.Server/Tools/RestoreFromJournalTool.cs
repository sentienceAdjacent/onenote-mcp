using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Server.Grants;

namespace OneNoteMcp.Server.Tools;

/// <summary>
/// <c>restore_from_journal</c> (AD-8; story 2.11). Thin (AD-5): the policy is in <see cref="RestoreFromJournalUseCase"/>
/// and the write pipeline (AD-7); the envelope comes from <see cref="ToolRunner"/>, so the tool never throws.
/// </summary>
[McpServerToolType]
public sealed class RestoreFromJournalTool(ToolRunner runner, RestoreFromJournalUseCase useCase)
{
    public const string Name = "restore_from_journal";

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
        "Writes to OneNote: brings back a page as it was before an update_page_content or delete_page_content, as a NEW page; it never overwrites or merges into an existing page. Call it only when the user asks to bring back a page's earlier version. "
        + "entryId is the meta.journalEntryId that write returned (or the entry ID its failure message names). Entries are kept for 30 days. "
        + "The page goes back into its own section if it still exists; else into the section at the same group/name path (warning restoredElsewhere); else into a section named Recovered directly under the notebook, created if needed (warning restoredElsewhere). "
        + "A page from a password-protected section goes back only into that section while it is still protected (ObjectNotFound otherwise; SectionLocked while it is locked); it needs no read grant. "
        + "The notebook must be open in OneNote (ObjectNotFound otherwise) and needs the user's write grant (a dialog may ask the user to type the notebook's name; only the user can grant, never run the grant command yourself). "
        + "Images missing from the snapshot, and attachments whose temporary copy is gone, aren't restored: warning binaryMissing, with counts. Only page entries can be restored (NotSupported otherwise). "
        + "If the content write fails after the page was created, the error names the new, possibly empty page: ask the user to delete it before retrying. If the call fails with outcomeUnknown: true, re-read the section's pages before trying again. "
        + "Success: {ok:true, meta:{correlationId, pageId, parentId}, warnings:[]}. "
        + "Failure (isError): {ok:false, error:{code, hresult?, retryable, outcomeUnknown?, userAction, message}, meta:{correlationId}}.")]
    public Task<CallToolResult> RestoreFromJournal(
        [Description("The journal entry's ID (a GUID): meta.journalEntryId from update_page_content or delete_page_content.")] string entryId,
        CancellationToken cancellationToken = default,
        McpServer? server = null) =>
        runner.RunAsync(
            Name,
            useCase,
            (context, ct) => useCase.ExecuteAsync(context, entryId ?? string.Empty, ct),
            cancellationToken,
            ElicitationGrantPrompt.For(server));
}
