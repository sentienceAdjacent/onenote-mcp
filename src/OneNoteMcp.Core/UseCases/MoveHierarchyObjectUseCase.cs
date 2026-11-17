using System.Globalization;
using System.Runtime.Versioning;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Core.Writes;

namespace OneNoteMcp.Core.UseCases;

/// <summary>
/// <c>move_hierarchy_object</c> (stories 3.4 and 3.12; AD-5, AD-7 Moves): a page moved to another section
/// (<see cref="MovePageWrite"/>), or a section or section group moved to another notebook or section group
/// (<see cref="MoveContainerWrite"/>), in its notebook or another one, through the write pipeline as one
/// <c>HierarchyWrite</c>. Core builds the <c>UpdateHierarchy</c> XML from IDs; nothing a caller writes reaches OneNote.
/// Every call starts as a page move, whose step 1 walks to the page's section before any <c>self</c> read, because OneNote
/// hides a locked section's pages (3.3 sitting S1); when the object turns out to be a section or section group
/// (<see cref="ObjectIsContainer"/>, before any grant, lock or write), the call runs as <see cref="MoveContainerWrite"/>.
/// The result is <c>meta.objectId</c> (the item's current ID: a page's new ID after the move, or a section's or group's
/// own; the old ID, with <c>readBackDiffers</c>, when the read-back finds a page still in the source; absent with a page's
/// <c>readBackFailed</c>), <c>meta.parentId</c> (the new parent), <c>meta.journalEntryId</c> (the old parent's
/// <c>hierarchy</c> entry) and <c>meta.journalEntryIds</c> (both entries, old then new; 3.4 Q1), with no
/// <c>meta.basis</c>. Messages carry IDs only (AD-9).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MoveHierarchyObjectUseCase(HierarchyWriteServices services, WritePipeline pipeline, Func<TimeSpan, CancellationToken, Task>? settle = null) : IUseCase
{
    public OperationClass Class => OperationClass.HierarchyWrite;

    /// <summary>
    /// Moves <paramref name="objectId"/> into <paramref name="parentId"/>, at the 1-based <paramref name="position"/> among
    /// the new parent's pages (for a page) or its children of the same kind (for a section or group), or last when null.
    /// </summary>
    public async Task<ToolSuccess> MoveAsync(ToolCallContext context, string objectId, string parentId, int? position, PageWriteBasis? basis, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(pipeline);
        var id = objectId ?? string.Empty;
        var parent = parentId ?? string.Empty;
        var write = new MovePageWrite(services, context.CorrelationId, id, parent, position, basis);
        try
        {
            var result = await pipeline.RunAsync(context, write, ct).ConfigureAwait(false);
            return Success(context, write.TargetId, result, write.JournalEntryIds);
        }
        catch (ObjectIsContainer)
        {
            // Step 1 of the page move found a section or section group before any grant, lock or write (story 3.12).
        }

        var container = new MoveContainerWrite(services, context.CorrelationId, id, parent, position, basis, settle ?? DelayAsync);
        var moved = await pipeline.RunAsync(context, container, ct).ConfigureAwait(false);
        return Success(context, container.TargetId, moved, container.JournalEntryIds);
    }

    private static Task DelayAsync(TimeSpan delay, CancellationToken ct) => Task.Delay(delay, ct);

    private static ToolSuccess Success(ToolCallContext context, string parentId, MoveResult result, IReadOnlyList<Guid> journalEntryIds)
    {
        var entries = journalEntryIds.Select(id => id.ToString("D", CultureInfo.InvariantCulture)).ToList();
        return new ToolSuccess(
            null,
            new ToolMeta(
                context.CorrelationId,
                ParentId: parentId,
                ObjectId: result.NewPageId,
                JournalEntryId: entries.Count > 0 ? entries[0] : null,
                JournalEntryIds: entries.Count > 0 ? entries : null),
            result.Warnings);
    }
}
