using System.Globalization;
using System.Runtime.Versioning;
using OneNoteMcp.Core.Journal;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Core.Writes;

namespace OneNoteMcp.Core.UseCases;

/// <summary>
/// <c>restore_from_recycle_bin</c> (story 3.5; AD-5, AD-7, AD-8): brings a soft-deleted page or section back out of its
/// notebook's recycle bin through the write pipeline as one <c>HierarchyWrite</c> (<see cref="RestoreFromRecycleBinWrite"/>).
/// Core builds the <c>UpdateHierarchy</c> XML from IDs; nothing a caller writes reaches OneNote. The result is
/// <c>meta.objectId</c> (the item's current ID: a page's new ID, a section's own; absent with a page's
/// <c>readBackFailed</c>), <c>meta.parentId</c> (where it went) and <c>meta.journalEntryId</c> (the target's
/// <c>hierarchy</c> entry). Messages carry IDs, counts and dates only (AD-9).
/// <para>Q7 (Phill, 2026-11-17): a plain section whose <c>parentId</c> is in another notebook is one call in two steps. The
/// restore puts it back under its original parent in its own notebook; then a second pipeline run moves it with
/// <see cref="MoveContainerWrite"/>, exactly as <c>move_hierarchy_object</c> does, so every move rule applies. If the move
/// is refused or fails, the result is still a success, with the warning <c>restoredNotMoved</c>. <c>meta.journalEntryIds</c>
/// lists the restore's entry, then the move's.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RestoreFromRecycleBinUseCase(
    HierarchyWriteServices services,
    WritePipeline pipeline,
    Func<SettingsSnapshot> settings,
    Func<TimeSpan, CancellationToken, Task>? settle = null,
    Func<string, IReadOnlyList<JournalEntry>>? loadEntries = null) : IUseCase
{
    /// <summary>What to do after the warning <c>restoredNotMoved</c> (Q7).</summary>
    public const string RestoredNotMovedUserAction =
        "The section is restored in its own notebook (meta.parentId), but it wasn't moved. Tell the user. To move it, deal with what the move's error says, then use move_hierarchy_object; or leave it where it is.";

    public OperationClass Class => OperationClass.HierarchyWrite;

    /// <summary>
    /// Restores <paramref name="objectId"/> (a page in Deleted Pages, or a section in the recycle bin) under
    /// <paramref name="parentId"/>, or its original parent when that is empty; <paramref name="entryId"/> picks its journal
    /// entry among several; <paramref name="position"/> is 1-based as for <c>move_hierarchy_object</c>, last when null.
    /// </summary>
    public async Task<ToolSuccess> RestoreAsync(ToolCallContext context, string objectId, string? parentId, string? entryId, int? position, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(settings);
        var wait = settle ?? DelayAsync;
        var write = new RestoreFromRecycleBinWrite(
            services,
            () => (loadEntries ?? SoftDeleteEntries.Load)(settings().Journal.Path),
            wait,
            context.CorrelationId,
            objectId ?? string.Empty,
            parentId,
            entryId,
            position);
        var result = await pipeline.RunAsync(context, write, ct).ConfigureAwait(false);
        var restoreEntry = write.JournalEntryId?.ToString("D", CultureInfo.InvariantCulture);
        if (write.MoveTargetId is not { } moveTo)
        {
            return new ToolSuccess(
                null,
                new ToolMeta(context.CorrelationId, ParentId: write.TargetId, ObjectId: result.NewPageId, JournalEntryId: restoreEntry),
                result.Warnings);
        }

        // Q7 step 2: the existing section move, its own pipeline run, so every move rule applies (grants, J1, settle...).
        var move = new MoveContainerWrite(services, context.CorrelationId, write.ObjectId, moveTo, position, basis: null, wait);
        var warnings = new List<ToolWarning>(result.Warnings);
        string finalParent;
        try
        {
            var moved = await pipeline.RunAsync(context, move, ct).ConfigureAwait(false);

            // The section was just placed by this call's restore, so there is no caller read to compare: noBasis would only mislead.
            warnings.AddRange(moved.Warnings.Where(w => w.Code != ToolWarnings.NoBasis));
            finalParent = move.TargetId;
        }
        catch (Exception ex) when (ex is OneNoteException or OperationCanceledException)
        {
            var code = ex is OneNoteException one ? one.Code.ToString() : nameof(ErrorCode.Cancelled);
            warnings.Add(new ToolWarning(
                ToolWarnings.RestoredNotMoved,
                $"Section {write.ObjectId} was restored under {write.TargetId} in its own notebook, but moving it to {moveTo} didn't happen: {code}: {ex.Message}",
                RestoredNotMovedUserAction));
            finalParent = write.TargetId ?? string.Empty;
        }

        var entries = new[] { write.JournalEntryId }.OfType<Guid>().Concat(move.JournalEntryIds)
            .Select(id => id.ToString("D", CultureInfo.InvariantCulture))
            .ToList();
        return new ToolSuccess(
            null,
            new ToolMeta(
                context.CorrelationId,
                ParentId: finalParent,
                ObjectId: write.ObjectId,
                JournalEntryId: restoreEntry,
                JournalEntryIds: entries.Count > 0 ? entries : null),
            warnings);
    }

    private static Task DelayAsync(TimeSpan delay, CancellationToken ct) => Task.Delay(delay, ct);
}
