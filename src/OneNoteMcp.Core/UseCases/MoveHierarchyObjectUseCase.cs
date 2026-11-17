using System.Globalization;
using System.Runtime.Versioning;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Core.Writes;

namespace OneNoteMcp.Core.UseCases;

/// <summary>
/// <c>move_hierarchy_object</c> (story 3.4; AD-5, AD-7 Moves): a page moved to another section, in its notebook or another
/// one, through the write pipeline as one <c>HierarchyWrite</c> (<see cref="MovePageWrite"/>). Core builds the
/// <c>UpdateHierarchy</c> XML from IDs; nothing a caller writes reaches OneNote. The result is <c>meta.objectId</c> (the
/// page's current ID: its new ID after the move; the old ID, with <c>readBackDiffers</c>, when the read-back finds it still
/// in the source; absent with <c>readBackFailed</c>), <c>meta.parentId</c> (the target section), <c>meta.journalEntryId</c> (the
/// source section's <c>hierarchy</c> entry) and <c>meta.journalEntryIds</c> (both entries, source then target; 3.4 Q1),
/// with no <c>meta.basis</c>. Sections and section groups wait for story 3.12. Messages carry IDs only (AD-9).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MoveHierarchyObjectUseCase(HierarchyWriteServices services, WritePipeline pipeline) : IUseCase
{
    public OperationClass Class => OperationClass.HierarchyWrite;

    /// <summary>
    /// Moves the page <paramref name="objectId"/> into the section <paramref name="parentId"/>, before the page now at the
    /// 1-based <paramref name="position"/>, or last when it is null.
    /// </summary>
    public async Task<ToolSuccess> MoveAsync(ToolCallContext context, string objectId, string parentId, int? position, PageWriteBasis? basis, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(pipeline);
        var write = new MovePageWrite(services, context.CorrelationId, objectId ?? string.Empty, parentId ?? string.Empty, position, basis);
        var result = await pipeline.RunAsync(context, write, ct).ConfigureAwait(false);
        var entries = write.JournalEntryIds.Select(id => id.ToString("D", CultureInfo.InvariantCulture)).ToList();
        return new ToolSuccess(
            null,
            new ToolMeta(
                context.CorrelationId,
                ParentId: write.TargetId,
                ObjectId: result.NewPageId,
                JournalEntryId: entries.Count > 0 ? entries[0] : null,
                JournalEntryIds: entries.Count > 0 ? entries : null),
            result.Warnings);
    }
}
