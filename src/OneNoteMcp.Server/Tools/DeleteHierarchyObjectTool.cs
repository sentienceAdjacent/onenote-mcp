using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Core.Writes;
using OneNoteMcp.Server.Grants;

namespace OneNoteMcp.Server.Tools;

/// <summary>
/// A delete's <c>basis</c> argument (story 5.5): AD-17's basis closed to <c>{lastModifiedTime?, pageHash?}</c>, since a
/// delete compares no element hash. For a page both come from <c>get_page_content</c>'s <c>meta.basis</c>; for a section or
/// section group only <c>lastModifiedTime</c>, from its element in <c>get_hierarchy</c>.
/// </summary>
public sealed record DeleteBasisArgument(
    [property: Description("A page's meta.basis.lastModifiedTime from get_page_content, or a section's or section group's lastModifiedTime attribute from get_hierarchy, unchanged.")] string? LastModifiedTime = null,
    [property: Description("A page's meta.basis.pageHash from get_page_content, unchanged. Never for a section or section group.")] string? PageHash = null)
{
    public PageWriteBasis ToCore() => new(LastModifiedTime, PageHash);
}

/// <summary>
/// <c>delete_hierarchy_object</c>: <c>DeleteHierarchy</c> to the recycle bin (Phill, 2026-11-13, 5.5 plan Q1 (b); story
/// 5.6's permanent delete is a tool of its own). Thin (AD-5): the policy is in <see cref="SoftDeleteUseCase"/> and the write
/// pipeline (AD-7); the envelope comes from <see cref="ToolRunner"/>, so the tool never throws.
/// </summary>
[McpServerToolType]
public sealed class DeleteHierarchyObjectTool(ToolRunner runner, SoftDeleteUseCase useCase)
{
    public const string Name = SoftDeleteUseCase.OperationName;

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
        "Writes to OneNote: moves a page, section or section group to its notebook's recycle bin, where the user can restore it in OneNote. Call it only when the user asks to delete one. "
        + "A page goes to Deleted Pages under a new ID (meta.recycleBinId); a section keeps its ID there; a section group dissolves, its sections going to the recycle bin one by one. "
        + "Pass basis so a change made since your read is caught: for a page, lastModifiedTime and pageHash from get_page_content's meta.basis (leave out elementHashes, which fails with InvalidArgument); "
        + "for a section or section group, only lastModifiedTime, the attribute on its element in get_hierarchy (a pageHash fails with InvalidArgument). A change since then fails with StaleWrite: re-read, then decide again. Without basis the delete runs with the warning noBasis. "
        + "The check on a section or section group is weak (a rename inside it doesn't change its date). A delete straight after one of your own writes, to any target (a page's hierarchy date is sent too), can get a spurious StaleWrite while that date catches up: re-read and retry. "
        + "Refused: a notebook (NotSupported: COM can't delete one), a page in a password-protected section (NotSupported: it can only be deleted permanently), a page that has subpages (InvalidArgument: delete them first, deepest first), "
        + "a locked section, a page in one or a section group holding one (SectionLocked: the user unlocks it in OneNote), and the recycle bin or anything in it (ObjectNotFound). "
        + "The notebook needs the user's write grant (a dialog may ask the user to type the notebook's name; only the user can grant, never run the grant command yourself). "
        + "Every delete first saves a recovery-journal entry: meta.journalEntryId. For a page, restore_from_journal can bring it back as a new copy, though the original stays in Deleted Pages; prefer OneNote's own restore. A section's or section group's entry only records its layout. "
        + "recycleBinIdMissing means the page's new ID in Deleted Pages couldn't be told apart; the delete still happened. If the call fails with outcomeUnknown: true, re-read the hierarchy before trying again. "
        + "Success: {ok:true, meta:{correlationId, objectId, journalEntryId, recycleBinId?}, warnings:[]}. "
        + "Failure (isError): {ok:false, error:{code, hresult?, retryable, outcomeUnknown?, userAction, message}, meta:{correlationId}}.")]
    public Task<CallToolResult> DeleteHierarchyObject(
        [Description("OneNote ID of the page, section or section group to delete.")] string objectId,
        [Description("The basis the delete is based on: {lastModifiedTime, pageHash} for a page, {lastModifiedTime} for a section or section group.")] DeleteBasisArgument? basis = null,
        CancellationToken cancellationToken = default,
        McpServer? server = null) =>
        runner.RunAsync(
            Name,
            useCase,
            (context, ct) => useCase.ExecuteAsync(context, objectId ?? string.Empty, basis?.ToCore(), ct),
            cancellationToken,
            ElicitationGrantPrompt.For(server));
}
