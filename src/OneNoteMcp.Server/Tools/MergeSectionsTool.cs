using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Server.Grants;

namespace OneNoteMcp.Server.Tools;

/// <summary>
/// Story 3.6: a section merged into another (epic 3 E6; AD-14: named after its COM method, <c>MergeSections</c>). Thin
/// (AD-5): the policy is in <see cref="MergeSectionsUseCase"/> and the write pipeline (AD-7); the envelope comes from
/// <see cref="ToolRunner"/>, so no tool throws.
/// </summary>
[McpServerToolType]
public sealed class MergeSectionsTool(ToolRunner runner, MergeSectionsUseCase useCase)
{
    public const string Name = "merge_sections";

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
        "Writes to OneNote: merges one section into another, in the same notebook or another open one, by copying every page of the source section to the end of the destination section. Call it only when the user asks to merge sections. "
        + "sourceId is the section whose pages are copied; destinationId is the section they are copied into. The source section stays as it was, with its own ID and pages: this tool never deletes it (if the user wants it gone afterwards, use delete_hierarchy_object). The copies get new IDs, at the end of the destination, subpages kept at their levels; meta.pageIds lists the copies' new IDs in destination order. "
        + "Read the source section first with get_hierarchy and pass its lastModifiedTime as basis: {lastModifiedTime}; a source changed since fails with StaleWrite. The check is weak: straight after another hierarchy write a date can move a second late and give a spurious StaleWrite (re-read, then retry). It is the source's date, so it can't tell that the same pair was already merged: merging twice makes a second set of copies. Without basis the merge runs with the warning noBasis. "
        + "Both notebooks need the user's write grant (a dialog may ask the user to type each notebook's name; only the user can grant, never run the grant command yourself). A destination in a password-protected section needs only that: the copies land inside its encryption, and meta.pageIds is left out, with counts only in messages. "
        + "Refused before anything is merged: a password-protected source section, locked or not (NotSupported: the user merges it in OneNote); anything in the recycle bin, or the recycle bin itself (ObjectNotFound); an ID that isn't a section, the same section twice, or a source with no pages (InvalidArgument); and a locked destination (SectionLocked: the user unlocks it in OneNote). "
        + "The destination's hierarchy is first saved to the recovery journal (meta.journalEntryId; restore_from_journal can't restore it, it is for the user). A merge that touches a cloud (OneDrive or SharePoint) notebook waits 10 s before re-reading, with the warning settled: ask the user to check it synced. Afterwards both sections are re-read: readBackDiffers means the copies' count differs from the source's pages, or the source changed; readBackFailed that the re-read failed (read the destination with get_hierarchy). "
        + "If the call fails with outcomeUnknown: true (a merge can fail after copying some pages; the message says what a re-read of the destination found), the copies have new IDs, so before any retry read the destination with get_hierarchy (scope children) and compare its pages, their count and names, with the source's: delete any partial copies first, or the retry duplicates them. "
        + "Success: {ok:true, meta:{correlationId, objectId, parentId, journalEntryId, pageIds?}, warnings:[]}. "
        + "Failure (isError): {ok:false, error:{code, hresult?, retryable, outcomeUnknown?, userAction, message}, meta:{correlationId}}.")]
    public Task<CallToolResult> MergeSections(
        [Description("OneNote ID of the section whose pages are copied. It stays as it was.")] string sourceId,
        [Description("OneNote ID of the section the copies go into, at its end.")] string destinationId,
        [Description("{lastModifiedTime} of the source section, unchanged from a get_hierarchy read (the lastModifiedTime attribute of its one:Section element). Nothing else.")] PageBasisArgument? basis = null,
        CancellationToken cancellationToken = default,
        McpServer? server = null) =>
        runner.RunAsync(
            Name,
            useCase,
            (context, ct) => useCase.MergeAsync(context, sourceId ?? string.Empty, destinationId ?? string.Empty, basis?.ToCore(), ct),
            cancellationToken,
            ElicitationGrantPrompt.For(server));
}
