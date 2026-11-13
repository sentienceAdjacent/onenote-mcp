using System.Globalization;
using System.Runtime.Versioning;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Core.Writes;

namespace OneNoteMcp.Core.UseCases;

/// <summary>
/// <c>UpdateHierarchy</c> renames and reorders (story 5.4; Phill, 2026-11-13, Q1): <c>rename_hierarchy_object</c>
/// (<see cref="RenameHierarchyWrite"/>) and <c>reorder_hierarchy_children</c> (<see cref="ReorderHierarchyWrite"/>), each
/// through the write pipeline (AD-7) as a <c>HierarchyWrite</c>, checked against the caller's <c>lastModifiedTime</c>
/// basis (AD-17). Core builds the XML from IDs and a name; nothing else a caller sends reaches OneNote. Adding goes through
/// story 5.3's create tools only (Q2), and moves wait for epic 3. The result is <c>meta.objectId</c> (the renamed node,
/// or the reordered parent) and <c>meta.journalEntryId</c> (the <c>hierarchy</c> entry step 6 took; absent when nothing
/// changed), with no <c>meta.basis</c>. Messages carry IDs only (AD-9).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UpdateHierarchyUseCase(HierarchyWriteServices services, WritePipeline pipeline) : IUseCase
{
    public OperationClass Class => OperationClass.HierarchyWrite;

    /// <summary><c>rename_hierarchy_object</c>: the section, section group or notebook <paramref name="objectId"/> gets the displayed name <paramref name="name"/>.</summary>
    public Task<ToolSuccess> RenameAsync(ToolCallContext context, string objectId, string name, PageWriteBasis? basis, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(services);
        return RunAsync(context, new RenameHierarchyWrite(services, context.CorrelationId, objectId ?? string.Empty, name ?? string.Empty, basis, services.ReadAccess.Now()), ct);
    }

    /// <summary><c>reorder_hierarchy_children</c>: the live children of <paramref name="parentId"/> take the order of <paramref name="childIds"/>, which lists every one of them.</summary>
    public Task<ToolSuccess> ReorderAsync(ToolCallContext context, string parentId, IReadOnlyList<string>? childIds, PageWriteBasis? basis, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(services);
        return RunAsync(context, new ReorderHierarchyWrite(services, context.CorrelationId, parentId ?? string.Empty, childIds, basis, services.ReadAccess.Now()), ct);
    }

    private async Task<ToolSuccess> RunAsync(ToolCallContext context, HierarchyWrite write, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        var result = await pipeline.RunAsync(context, write, ct).ConfigureAwait(false);
        return new ToolSuccess(
            null,
            new ToolMeta(context.CorrelationId, ObjectId: write.TargetId, JournalEntryId: write.JournalEntryId?.ToString("D", CultureInfo.InvariantCulture)),
            result.Warnings);
    }
}
